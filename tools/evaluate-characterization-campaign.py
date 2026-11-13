#!/usr/bin/env python3
"""Fit on whole training runs and evaluate whole held-out runs; output is evidence, never auto-qualified."""
from __future__ import annotations

import argparse
import importlib.util
import hashlib
import json
import math
import re
import statistics
from pathlib import Path

spec = importlib.util.spec_from_file_location("trace_analyzer", Path(__file__).with_name("analyze-controller-trace.py"))
trace_analyzer = importlib.util.module_from_spec(spec)
assert spec and spec.loader
spec.loader.exec_module(trace_analyzer)
MOTOR = re.compile(rb"MOTOR (-?\d{1,4}) (-?\d{1,4})\Z")


def renode_advance_bracket(host_ticks: int, boundaries: list[dict]) -> tuple[int, int] | None:
    """Bound simulated advancement around an observed UART read; this is not byte-consumption time."""
    prior = [row for row in boundaries if row["HostMonotonicTicks"] <= host_ticks]
    following = [row for row in boundaries if row["HostMonotonicTicks"] > host_ticks]
    if not prior or not following:
        return None
    lower = max(prior, key=lambda row: row["HostMonotonicTicks"])["SimTimeNs"]
    upper = min(following, key=lambda row: row["HostMonotonicTicks"])["SimTimeNs"]
    if upper < lower:
        raise ValueError("Renode advancement boundaries regress in simulation time")
    return lower, upper


def matched_events(trace_path: Path, controller_id: str | None = None) -> list[dict]:
    records = trace_analyzer.load(trace_path)
    incoming = trace_analyzer.lines(records, "controller_to_mcu")
    outgoing = trace_analyzer.lines(records, "mcu_to_controller")
    boundaries = [row for row in records if row.get("RecordType") == "RenodeTimeBoundary"
                  or ("RecordType" not in row and "HostMonotonicTicks" in row and "SimTimeNs" in row
                      and "Channel1SignedDuty" not in row)]
    boundaries.sort(key=lambda row: row["HostMonotonicTicks"])
    replies = sorted(outgoing, key=lambda row: row["host"])
    pwm = sorted((row for row in records if "Channel1SignedDuty" in row), key=lambda row: row["HostMonotonicTicks"])
    result = []
    for item in incoming:
        if controller_id is not None and item["controller"] != controller_id:
            continue
        match = MOTOR.fullmatch(item["line"])
        if not match:
            continue
        left, right = map(int, match.groups())
        if not all(-100 <= value <= 100 for value in (left, right)):
            continue
        ack = next((row for row in replies if row["controller"] == item["controller"] and row["line"] == b"OK MOTOR" and row["host"] >= item["host"]), None)
        sample = next((row for row in pwm if row["ControllerId"] == item["controller"] and ack and row["HostMonotonicTicks"] >= ack["host"]), None)
        if sample:
            bracket = renode_advance_bracket(item["host"], boundaries)
            result.append({"input": (left, right), "output": (sample["Channel1SignedDuty"], sample["Channel2SignedDuty"]),
                           "sim_time_ns": sample["SimTimeNs"], "ack_sample_ms": (sample["HostMonotonicTicks"] - ack["host"]) / 1e4,
                           "uart_receive_sim_time_bracket_ns": bracket})
    return result


def fit_operator(events: list[dict], channel: int, operator: str) -> tuple[float, float]:
    points = [(event["input"][channel], event["output"][channel]) for event in events]
    if not points:
        raise ValueError("model fitting fold is empty")
    if operator == "constant":
        return 0.0, statistics.mean(y for _, y in points)
    if operator == "pass_through":
        return 0.01, statistics.mean(y - 0.01 * x for x, y in points)
    if operator == "affine":
        mean_x, mean_y = statistics.mean(x for x, _ in points), statistics.mean(y for _, y in points)
        denominator = sum((x - mean_x) ** 2 for x, _ in points)
        if denominator == 0:
            raise ValueError("training input channel does not vary")
        gain = sum((x - mean_x) * (y - mean_y) for x, y in points) / denominator
        return gain, mean_y - gain * mean_x
    raise ValueError(f"unsupported candidate operator: {operator}")


def select_operator(events: list[dict], channel: int, threshold_percentage_points: float = 2.0) -> tuple[str, dict]:
    sequences = sorted({event["sequence_id"] for event in events})
    if len(sequences) < 2:
        raise ValueError("complexity selection needs at least two whole training sequences")
    # The trace's input is percentage points and its output is signed duty fraction, so
    # unit-preserving pass-through is not a physically valid candidate for this dataset.
    complexities = {"constant": 1, "affine": 2}
    results = {}
    for operator, complexity in complexities.items():
        errors, folds = [], []
        for sequence_id in sequences:
            fold_train = [event for event in events if event["sequence_id"] != sequence_id]
            fold_validation = [event for event in events if event["sequence_id"] == sequence_id]
            gain, offset = fit_operator(fold_train, channel, operator)
            fold_errors = [abs(event["output"][channel] - (gain * event["input"][channel] + offset)) * 100
                           for event in fold_validation]
            errors.extend(fold_errors)
            folds.append({"sequence_id": sequence_id, "n": len(fold_errors), "max_error_pp": max(fold_errors)})
        errors.sort()
        p95 = errors[math.ceil(.95 * len(errors)) - 1]
        results[operator] = {"complexity": complexity,
                             "training_group_cv": {"n": len(errors), "p95_error_pp": p95, "max_error_pp": max(errors), "folds": folds},
                             "meets_cv_threshold": p95 <= threshold_percentage_points}
    eligible = [operator for operator in complexities if results[operator]["meets_cv_threshold"]]
    if not eligible:
        raise ValueError("no candidate meets the training-sequence cross-validation threshold")
    selected = min(eligible, key=lambda operator: (complexities[operator], results[operator]["training_group_cv"]["p95_error_pp"], operator))
    results["selected"] = selected
    results["not_considered"] = {"pass_through": "input percentage points and output signed fraction have different units"}
    results["selection_threshold_percentage_points"] = threshold_percentage_points
    return selected, results


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("campaign_dir", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--controller-id")
    args = parser.parse_args()
    manifest = json.loads((args.campaign_dir / "sequence-manifest.json").read_text(encoding="utf-8-sig"))
    declared_controllers = {sequence["controller"] for sequence in manifest if "controller" in sequence}
    if len(declared_controllers) > 1 and args.controller_id is None:
        raise ValueError("campaign contains multiple controller identities; select one with --controller-id")
    controller_id = args.controller_id or (next(iter(declared_controllers)) if declared_controllers else None)
    if controller_id is not None and declared_controllers != {controller_id}:
        raise ValueError(f"selected controller {controller_id!r} does not match campaign manifest identities {sorted(declared_controllers)}")
    training, heldout, runs = [], [], []
    for sequence in manifest:
        path = args.campaign_dir / sequence["trace"]
        if hashlib.sha256(path.read_bytes()).hexdigest().upper() != sequence["trace_sha256"].upper():
            raise ValueError(f"trace hash mismatch for {sequence['sequence_id']}")
        events = matched_events(path, controller_id)
        for event in events:
            event["sequence_id"] = sequence["sequence_id"]
        (training if sequence["split"] == "training" else heldout).extend(events)
        runs.append({"sequence_id": sequence["sequence_id"], "split": sequence["split"], "commands": len(sequence["commands"]), "matched_pwm_events": len(events)})
    if len(training) < 8 or len(heldout) < 4:
        raise ValueError("insufficient matched train/held-out events")
    models, errors, model_selection = [], [], {}
    for channel in (0, 1):
        operator, selection = select_operator(training, channel)
        gain, offset = fit_operator(training, channel, operator)
        models.append({"channel": channel + 1, "operator": operator, "input_unit": "percentage_points",
                       "output_unit": "fraction_signed", "gain_per_percentage_point": gain, "offset": offset})
        model_selection[f"channel_{channel + 1}"] = selection
        errors.extend(abs(y - (gain * x + offset)) for x, y in ((event["input"][channel], event["output"][channel]) for event in heldout))
    errors.sort()
    p95 = errors[math.ceil(.95 * len(errors)) - 1]
    bracket_widths = sorted(upper - lower for event in training + heldout
                            if (bracket := event["uart_receive_sim_time_bracket_ns"]) is not None
                            for lower, upper in [bracket])
    report = {"status": "UNQUALIFIED", "controller_id": controller_id,
              "training_runs": sum(row["split"] == "training" for row in runs),
              "heldout_runs": sum(row["split"] == "heldout" for row in runs), "runs": runs,
              "training_events": len(training), "heldout_events": len(heldout), "models": models,
              "model_selection": model_selection,
              "heldout_observations": [{"sequence_id": event["sequence_id"], "input": list(event["input"]),
                                        "observed_output": list(event["output"]), "sim_time_ns": event["sim_time_ns"]}
                                       for event in heldout],
              "heldout_duty_error_percentage_points": {"n": len(errors), "p95": p95 * 100, "max": max(errors) * 100},
              "heldout_ack_to_pwm_sample_ms": {"p95": sorted(row["ack_sample_ms"] for row in heldout)[math.ceil(.95 * len(heldout)) - 1]},
              "uart_receive_sim_time_bracket_ns": {"n": len(bracket_widths), "p95_width": sorted(bracket_widths)[math.ceil(.95 * len(bracket_widths)) - 1] if bracket_widths else None,
                                                   "max_width": max(bracket_widths) if bracket_widths else None,
                                                   "meaning": "Renode advancement interval bracketing host UART read, not exact firmware byte-consumption time"},
              "limitations": ["single firmware and reset condition", "Renode advancement interval around UART socket reads is a conservative bracket, not exact simulated byte-consumption time; the Renode terminal has no baud serialization", "only sampled PWM outputs quantitatively evaluated", "sensor responses, fault/latch semantics, UART timing grammar and loaded trajectory gates remain unvalidated", "this artifact cannot be used for runtime execution"]}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"training": len(training), "heldout": len(heldout), "p95_duty_pp": p95 * 100, "output": str(args.output)}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
