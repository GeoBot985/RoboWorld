#!/usr/bin/env python3
"""Compare exact-firmware ultrasonic replies with the latest causally complete Godot echo sample."""
from __future__ import annotations

import argparse
import importlib.util
import json
import math
import re
from pathlib import Path

analyzer_spec = importlib.util.spec_from_file_location("trace_analyzer", Path(__file__).with_name("analyze-controller-trace.py"))
assert analyzer_spec and analyzer_spec.loader
trace_analyzer = importlib.util.module_from_spec(analyzer_spec)
analyzer_spec.loader.exec_module(trace_analyzer)
campaign_spec = importlib.util.spec_from_file_location("campaign_evaluator", Path(__file__).with_name("evaluate-characterization-campaign.py"))
assert campaign_spec and campaign_spec.loader
campaign_evaluator = importlib.util.module_from_spec(campaign_spec)
campaign_spec.loader.exec_module(campaign_evaluator)

DISTANCE = re.compile(rb"DIST mm=(\d+) valid=([01])\Z")


def latest_completed_echo(samples: list[dict], rises: list[dict], falls: list[dict], sim_time_ns: int) -> dict | None:
    """Return the latest raycast whose PA0 pulse had fallen by the given authoritative time."""
    completed = []
    for sample in samples:
        rise = next((edge for edge in rises if edge["SimTimeNs"] == sample["SimTimeNs"]), None)
        if rise is None:
            continue
        fall = next((edge for edge in falls if edge["SimTimeNs"] > rise["SimTimeNs"]), None)
        if fall is not None and fall["SimTimeNs"] <= sim_time_ns:
            completed.append((sample, fall["SimTimeNs"]))
    if not completed:
        return None
    sample, fall_time_ns = max(completed, key=lambda pair: pair[0]["SimTimeNs"])
    return {"sample": sample, "fall_sim_time_ns": fall_time_ns}


def evaluate(trace_path: Path) -> dict:
    records = trace_analyzer.load(trace_path)
    boundaries = [row for row in records if row.get("RecordType") == "RenodeTimeBoundary"
                  or ("RecordType" not in row and "HostMonotonicTicks" in row and "SimTimeNs" in row
                      and "Channel1SignedDuty" not in row)]
    boundaries.sort(key=lambda row: row["HostMonotonicTicks"])
    samples = sorted((row for row in records if row.get("RecordType") == "DistanceSample"), key=lambda row: row["SimTimeNs"])
    edges = [row for row in records if row.get("RecordType") == "DistanceEchoEdge"]
    rises = sorted((row for row in edges if row["High"]), key=lambda row: row["SimTimeNs"])
    falls = sorted((row for row in edges if not row["High"]), key=lambda row: row["SimTimeNs"])
    incoming = trace_analyzer.lines(records, "controller_to_mcu")
    outgoing = trace_analyzer.lines(records, "mcu_to_controller")
    queries = [row for row in incoming if row["line"] == b"SENSOR?"]
    responses = sorted((row for row in outgoing if DISTANCE.fullmatch(row["line"])), key=lambda row: row["host"])
    result, used = [], set()
    for query in queries:
        response_index = next((index for index, row in enumerate(responses)
                               if index not in used and row["controller"] == query["controller"] and row["host"] >= query["host"]), None)
        if response_index is None:
            continue
        used.add(response_index)
        response = responses[response_index]
        match = DISTANCE.fullmatch(response["line"])
        assert match
        millimeters, valid = int(match.group(1)), match.group(2) == b"1"
        bracket = campaign_evaluator.renode_advance_bracket(response["host"], boundaries)
        item = {"reply_mm": millimeters, "valid": valid, "response_sim_time_bracket_ns": bracket}
        if valid and bracket is not None:
            reference = latest_completed_echo(samples, rises, falls, bracket[0])
            if reference is not None:
                sample = reference["sample"]
                item.update({"reference_sample_sim_time_ns": sample["SimTimeNs"],
                             "reference_echo_fall_sim_time_ns": reference["fall_sim_time_ns"],
                             "raycast_distance_mm": sample["DistanceMeters"] * 1000,
                             "absolute_error_mm": abs(millimeters - sample["DistanceMeters"] * 1000)})
        result.append(item)
    errors = sorted(row["absolute_error_mm"] for row in result if "absolute_error_mm" in row)
    p95 = errors[math.ceil(0.95 * len(errors)) - 1] if errors else None
    return {"status": "REFERENCE_FIRMWARE_OBSERVATION_NOT_MODEL_QUALIFICATION",
            "trace": trace_path.name, "sensor_inputs": len(samples), "echo_rises": sum(1 for edge in edges if edge["High"]),
            "echo_falls": len(falls), "sensor_queries": len(queries), "paired_replies": len(result),
            "causally_matched_valid_replies": len(errors),
            "absolute_error_mm": {"n": len(errors), "p95": p95, "max": max(errors) if errors else None},
            "replies": result,
            "limitations": ["one firmware and one sensor-e2e scene/reset condition", "reply compared to latest echo fully complete by the conservative lower simulation-time bound", "does not qualify arbitrary sensor histories, MCU timing, faults, or a deterministic controller model"]}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("trace", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = evaluate(args.trace)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"paired_replies": result["paired_replies"],
                      "causally_matched_valid_replies": result["causally_matched_valid_replies"],
                      "p95_error_mm": result["absolute_error_mm"]["p95"], "output": str(args.output)}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
