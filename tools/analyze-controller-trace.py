#!/usr/bin/env python3
"""Reconstruct UART lines and fit/evaluate a simple motor response from a version-1 Renode trace."""
from __future__ import annotations

import argparse
import base64
import json
import math
import re
import statistics
from pathlib import Path

MOTOR = re.compile(rb"MOTOR (-?\d{1,4}) (-?\d{1,4})\Z")


def load(path: Path) -> list[dict]:
    records = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]
    if not records:
        raise ValueError("trace is empty")
    if any(row.get("SchemaVersion") != 1 for row in records):
        raise ValueError("only trace schema 1 is supported")
    seq = [row["Sequence"] for row in records]
    if seq != sorted(set(seq)):
        raise ValueError("trace sequence is not strictly increasing")
    return records


def lines(records: list[dict], direction: str) -> list[dict]:
    streams: dict[str, bytearray] = {}
    starts: dict[str, dict] = {}
    result: list[dict] = []
    for row in records:
        if row.get("Direction") != direction:
            continue
        cid = row["ControllerId"]
        buf = streams.setdefault(cid, bytearray())
        for byte in base64.b64decode(row["BytesBase64"], validate=True):
            if not buf:
                starts[cid] = row
            if byte == 10:
                raw = bytes(buf).rstrip(b"\r")
                result.append({"controller": cid, "line": raw,
                               "host": starts[cid]["HostMonotonicTicks"],
                               "start_host": starts[cid]["HostMonotonicTicks"],
                               "line_complete_host": row["HostMonotonicTicks"],
                               "boundary_lower_ns": starts[cid]["LastCompletedBoundaryNsLowerBound"],
                               "line_complete_boundary_lower_ns": row["LastCompletedBoundaryNsLowerBound"],
                               "scheduled_sim_time_ns": row.get("ScheduledSimTimeNs")})
                buf.clear()
            elif len(buf) < 512:
                buf.append(byte)
            else:
                raise ValueError(f"UART line exceeds trace limit for {cid}")
    if any(streams.values()):
        raise ValueError("trace ends with a partial UART line")
    return result


def fit(points: list[tuple[float, float]]) -> tuple[float, float]:
    if len(points) < 2:
        raise ValueError("insufficient training points")
    xs, ys = zip(*points)
    mean_x, mean_y = statistics.mean(xs), statistics.mean(ys)
    denom = sum((x - mean_x) ** 2 for x in xs)
    if denom == 0:
        raise ValueError("training commands do not vary")
    slope = sum((x - mean_x) * (y - mean_y) for x, y in points) / denom
    return slope, mean_y - slope * mean_x


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("trace", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--heldout-modulus", type=int, default=5)
    parser.add_argument("--max-host-gap-ms", type=float, default=1000)
    args = parser.parse_args()
    if args.heldout_modulus < 2:
        parser.error("heldout modulus must be at least 2")
    records = load(args.trace)
    inputs = lines(records, "controller_to_mcu")
    replies = lines(records, "mcu_to_controller")
    pwm = [row for row in records if "Channel1SignedDuty" in row]
    response_by_time = sorted(replies, key=lambda row: row["host"])
    samples = sorted(pwm, key=lambda row: row["HostMonotonicTicks"])
    commands = []
    rejected = []
    for item in inputs:
        match = MOTOR.fullmatch(item["line"])
        if not match:
            if item["line"] not in (b"PING", b"STATUS?", b"SENSOR?"):
                rejected.append({"line": item["line"].decode("ascii", "replace"), "boundary_lower_ns": item["boundary_lower_ns"]})
            continue
        left, right = map(int, match.groups())
        if any(value < -100 or value > 100 for value in (left, right)):
            continue
        ack = next((row for row in response_by_time if row["controller"] == item["controller"] and row["line"] == b"OK MOTOR" and row["host"] >= item["host"]), None)
        sample = next((row for row in samples if row["ControllerId"] == item["controller"] and row["HostMonotonicTicks"] >= (ack["host"] if ack else item["host"])), None)
        if sample is None or ack is None:
            continue
        gap_ms = (sample["HostMonotonicTicks"] - ack["host"]) / 1e4
        if gap_ms > args.max_host_gap_ms:
            continue
        commands.append({"controller": item["controller"], "left_input": left, "right_input": right,
                         "left_pwm": sample["Channel1SignedDuty"], "right_pwm": sample["Channel2SignedDuty"],
                         "command_boundary_lower_ns": item["boundary_lower_ns"], "sample_sim_time_ns": sample["SimTimeNs"],
                         "ack_to_sample_host_ms": gap_ms})
    if len(commands) < args.heldout_modulus * 2:
        raise ValueError(f"only {len(commands)} matched motor events; need at least {args.heldout_modulus * 2}")
    train = [row for i, row in enumerate(commands) if i % args.heldout_modulus != 0]
    heldout = [row for i, row in enumerate(commands) if i % args.heldout_modulus == 0]
    models = {}
    errors = []
    for side in ("left", "right"):
        slope, intercept = fit([(row[f"{side}_input"] / 100, row[f"{side}_pwm"]) for row in train])
        models[side] = {"operator": "affine", "input": f"uart.motor.{side}_percent", "scale": slope, "offset": intercept}
        for row in heldout:
            errors.append(abs(row[f"{side}_pwm"] - (slope * row[f"{side}_input"] / 100 + intercept)))
    profile = {"schema_version": 1, "method": "ordinary_least_squares_from_trace", "training_event_count": len(train),
               "heldout_event_count": len(heldout), "input_lines": len(inputs), "unrecognized_lines": rejected,
               "models": models, "heldout_duty_error": {"n": len(errors), "p95_fraction": sorted(errors)[math.ceil(.95 * len(errors)) - 1], "max_fraction": max(errors)},
               "qualification": "UNQUALIFIED: trace-only motor PWM fit; command grammar, fault semantics, state, timing and trajectory gates remain untested"}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(profile, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"matched_events": len(commands), "train": len(train), "heldout": len(heldout),
                      "p95_duty_error_fraction": profile["heldout_duty_error"]["p95_fraction"], "output": str(args.output)}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
