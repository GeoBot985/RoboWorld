#!/usr/bin/env python3
"""Check exact-firmware behavior after a real Godot raycast stops producing echo pulses."""
from __future__ import annotations

import argparse
import importlib.util
import json
import re
from pathlib import Path


def load_helper(name: str, filename: str):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


trace_analyzer = load_helper("trace_analyzer", "analyze-controller-trace.py")
campaign_evaluator = load_helper("campaign_evaluator", "evaluate-characterization-campaign.py")
DISTANCE = re.compile(rb"DIST mm=(\d+) valid=([01])\Z")
REPLY_LINE = re.compile(rb"Sensor reply \d+: (DIST mm=\d+ valid=[01])")


def evaluate(trace_path: Path, controller_log: Path) -> dict:
    records = trace_analyzer.load(trace_path)
    samples = sorted((row for row in records if row.get("RecordType") == "DistanceSample"), key=lambda row: row["SimTimeNs"])
    edges = [row for row in records if row.get("RecordType") == "DistanceEchoEdge"]
    boundaries = sorted((row for row in records if row.get("RecordType") == "RenodeTimeBoundary"), key=lambda row: row["HostMonotonicTicks"])
    incoming = trace_analyzer.lines(records, "controller_to_mcu")
    outgoing = trace_analyzer.lines(records, "mcu_to_controller")
    queries = [row for row in incoming if row["line"] == b"SENSOR?"]
    responses = sorted((row for row in outgoing if DISTANCE.fullmatch(row["line"])), key=lambda row: row["host"])
    no_echo = [sample for sample in samples if sample["DistanceMeters"] < 0]
    in_range = [sample for sample in samples if sample["DistanceMeters"] >= 0]
    if not no_echo or not in_range:
        raise ValueError("trace must contain both in-range and no-echo raycast samples")
    rises = sorted((row for row in edges if row["High"]), key=lambda row: row["SimTimeNs"])
    falls = sorted((row for row in edges if not row["High"]), key=lambda row: row["SimTimeNs"])
    if len(rises) != len(falls) or len(rises) != len(in_range):
        raise ValueError(f"expected one complete pulse per in-range sample; samples={len(in_range)} rises={len(rises)} falls={len(falls)}")
    for sample, rise, fall in zip(in_range, rises, falls, strict=True):
        if rise["SimTimeNs"] != sample["SimTimeNs"] or fall["SimTimeNs"] <= rise["SimTimeNs"]:
            raise ValueError("in-range raycast sample and PA0 pulse timestamps do not pair in order")

    host_replies = [match.group(1) for match in REPLY_LINE.finditer(controller_log.read_bytes())]
    if len(queries) != len(host_replies):
        raise ValueError(f"controller log/trace query count differs: log={len(host_replies)} trace={len(queries)}")
    paired = []
    response_index = 0
    for query, expected_host_line in zip(queries, host_replies, strict=True):
        response = next((row for row in responses[response_index:]
                         if row["controller"] == query["controller"] and row["host"] >= query["host"]), None)
        if response is None:
            raise ValueError("a SENSOR? command has no subsequent firmware reply")
        response_index = responses.index(response) + 1
        if response["line"] != expected_host_line:
            raise ValueError("external UART output differs from trace-captured firmware UART reply")
        bracket = campaign_evaluator.renode_advance_bracket(response["host"], boundaries)
        if bracket is None:
            continue
        lower_ns, upper_ns = bracket
        after_no_echo = lower_ns >= no_echo[0]["SimTimeNs"]
        reply_match = DISTANCE.fullmatch(response["line"])
        assert reply_match
        reply_mm, valid = int(reply_match.group(1)), reply_match.group(2) == b"1"
        expected_stale_mm = None
        if after_no_echo:
            completed = [(rise, fall) for rise, fall in zip(rises, falls, strict=True) if fall["SimTimeNs"] <= lower_ns]
            if not completed:
                raise ValueError("no-echo reply has no causally prior valid echo from which stale state can be checked")
            rise, fall = completed[-1]
            # Firmware TIM2 counts whole microseconds; its C integer expression truncates again.
            pulse_us = (fall["SimTimeNs"] - rise["SimTimeNs"]) // 1000
            expected_stale_mm = (pulse_us * 343) // 2000
            if not valid or abs(reply_mm - expected_stale_mm) > 1:
                raise ValueError(f"firmware no-echo retention mismatch: got {reply_mm} valid={int(valid)}, expected stale {expected_stale_mm} mm")
        paired.append({"query_host_ticks": query["host"], "reply_host_ticks": response["host"],
                       "reply": response["line"].decode("ascii"), "reply_sim_time_bracket_ns": [lower_ns, upper_ns],
                       "after_no_echo_started": after_no_echo, "expected_retained_mm": expected_stale_mm})

    stale_replies = [row for row in paired if row["after_no_echo_started"]]
    if not stale_replies:
        raise ValueError("campaign did not query firmware after the no-echo transition")
    return {
        "status": "MEASURED_EXACT_FIRMWARE_NO_ECHO_BEHAVIOR_NOT_MODEL_QUALIFICATION",
        "trace": trace_path.name,
        "in_range_samples": len(in_range),
        "no_echo_samples": len(no_echo),
        "timed_echo_pulses": len(edges),
        "first_no_echo_sim_time_ns": no_echo[0]["SimTimeNs"],
        "sensor_queries": len(queries),
        "causally_bracketed_replies": len(paired),
        "post_no_echo_replies": len(stale_replies),
        "post_no_echo_firmware_behavior": "retains prior valid reading and valid=1; this is stale sample state, not a zero/invalid timeout",
        "replies": paired,
        "limitations": ["one reference STM32F407 firmware and one no-echo transition family", "does not cover echo timeout after a partial high pulse, noisy/overlapping edges, reset recovery, loaded trajectory equivalence, or arbitrary hidden firmware state", "characterization evidence only; candidate remains UNQUALIFIED"]
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("trace", type=Path)
    parser.add_argument("controller_log", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = evaluate(args.trace, args.controller_log)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"no_echo_samples": result["no_echo_samples"], "post_no_echo_replies": result["post_no_echo_replies"], "status": result["status"]}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
