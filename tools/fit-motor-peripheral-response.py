#!/usr/bin/env python3
"""Fit rover motor duty magnitude and validate measured GPIO direction separately."""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import math
import statistics
from pathlib import Path

P95_DUTY_THRESHOLD_PP = 2.0
MOTOR_PREFIX = b"MOTOR "

spec = importlib.util.spec_from_file_location("trace_analyzer", Path(__file__).with_name("analyze-controller-trace.py"))
assert spec and spec.loader
analyzer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(analyzer)


def percentile(values: list[float]) -> float:
    return sorted(values)[max(0, math.ceil(.95 * len(values)) - 1)]


def fit(points: list[tuple[float, float]]) -> tuple[float, float]:
    if len(points) < 2:
        raise ValueError("at least two observations are required")
    mean_x = statistics.mean(x for x, _ in points)
    mean_y = statistics.mean(y for _, y in points)
    denominator = sum((x - mean_x) ** 2 for x, _ in points)
    if denominator <= 0:
        raise ValueError("training commands do not vary")
    gain = sum((x - mean_x) * (y - mean_y) for x, y in points) / denominator
    return gain, mean_y - gain * mean_x


def collect(campaign: Path) -> tuple[list[dict], str]:
    manifest_path = campaign / "sequence-manifest.json"
    manifest_bytes = manifest_path.read_bytes()
    manifest = json.loads(manifest_bytes)
    rows = []
    for sequence in manifest:
        trace_path = campaign / sequence["trace"]
        if hashlib.sha256(trace_path.read_bytes()).hexdigest().upper() != sequence["trace_sha256"].upper():
            raise ValueError(f"trace hash mismatch: {sequence['sequence_id']}")
        records = analyzer.load(trace_path)
        incoming = sorted(analyzer.lines(records, "controller_to_mcu"), key=lambda row: row["host"])
        replies = sorted(analyzer.lines(records, "mcu_to_controller"), key=lambda row: row["host"])
        boundaries = sorted((row for row in records if row.get("RecordType") == "RenodeTimeBoundary"), key=lambda row: row["HostMonotonicTicks"])
        samples = sorted((row for row in records if row.get("RecordType") == "PwmObservation"), key=lambda row: row["HostMonotonicTicks"])
        if not samples or not all("Channel1DirectionForward" in row and "Channel2DirectionForward" in row
                                  and "Channel1DutyFraction" in row and "Channel2DutyFraction" in row for row in samples):
            raise ValueError(f"trace lacks raw duty/direction fields: {sequence['sequence_id']}")
        for request in incoming:
            expected_controller = sequence.get("controller")
            if expected_controller is not None and request["controller"] != expected_controller:
                continue
            if not request["line"].startswith(MOTOR_PREFIX):
                continue
            parts = request["line"][len(MOTOR_PREFIX):].split()
            if len(parts) != 2:
                continue
            left, right = map(int, parts)
            ack = next((reply for reply in replies if reply["controller"] == request["controller"]
                        and reply["line"] == b"OK MOTOR" and reply["line_complete_host"] >= request["line_complete_host"]), None)
            scheduled_sim_time_ns = request.get("scheduled_sim_time_ns")
            if scheduled_sim_time_ns is not None:
                sample = next((row for row in samples if row["ControllerId"] == request["controller"]
                               and row["SimTimeNs"] > scheduled_sim_time_ns), None)
                previous_sample = next((row for row in reversed(samples) if row["ControllerId"] == request["controller"]
                                        and row["SimTimeNs"] <= scheduled_sim_time_ns), None)
            else:
                sample = next((row for row in samples if row["ControllerId"] == request["controller"]
                               and row["HostMonotonicTicks"] >= (ack["line_complete_host"] if ack else request["line_complete_host"])), None)
                previous_sample = next((row for row in reversed(samples) if row["ControllerId"] == request["controller"]
                                        and row["HostMonotonicTicks"] < request["line_complete_host"]), None)
            if ack is None or sample is None:
                continue
            if scheduled_sim_time_ns is None:
                next_boundary = next((row for row in boundaries if row["HostMonotonicTicks"] >= request["line_complete_host"]), None)
                if next_boundary is None:
                    raise ValueError(f"UART request is not bracketed by a later Renode boundary: {sequence['sequence_id']}")
                input_lower_ns = request["line_complete_boundary_lower_ns"]
                input_upper_ns = next_boundary["SimTimeNs"]
                timestamp_source = "host_arrival_boundary_bracket"
            else:
                input_lower_ns = input_upper_ns = scheduled_sim_time_ns
                timestamp_source = "tick_scheduled_input"
            if input_upper_ns < input_lower_ns:
                raise ValueError(f"UART request boundary bracket is inverted: {sequence['sequence_id']}")
            latency_min_ns = max(0, sample["SimTimeNs"] - input_upper_ns)
            latency_max_ns = max(0, sample["SimTimeNs"] - input_lower_ns)
            output_changed = previous_sample is not None and any(
                previous_sample[field] != sample[field] for field in (
                    "Channel1DutyFraction", "Channel2DutyFraction",
                    "Channel1DirectionForward", "Channel2DirectionForward"))
            rows.append({"sequence_id": sequence["sequence_id"], "split": sequence["split"],
                         "left_input_percent": left, "right_input_percent": right,
                         "left_duty_fraction": sample["Channel1DutyFraction"],
                         "right_duty_fraction": sample["Channel2DutyFraction"],
                         "left_direction_forward": sample["Channel1DirectionForward"],
                         "right_direction_forward": sample["Channel2DirectionForward"],
                         "command_sim_time_bracket_ns": [input_lower_ns, input_upper_ns],
                         "input_timestamp_source": timestamp_source,
                         "output_changed": output_changed,
                         "ack_boundary_lower_ns": ack["boundary_lower_ns"],
                         "sample_sim_time_ns": sample["SimTimeNs"],
                         "command_to_output_latency_min_ns": latency_min_ns,
                         "command_to_output_latency_max_ns": latency_max_ns,
                         "ack_to_output_ns": sample["SimTimeNs"] - ack["boundary_lower_ns"]})
    return rows, hashlib.sha256(manifest_bytes).hexdigest().upper()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("campaign_dir", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    rows, manifest_hash = collect(args.campaign_dir.resolve())
    training = [row for row in rows if row["split"] == "training"]
    heldout = [row for row in rows if row["split"] == "heldout"]
    train_sequences = sorted({row["sequence_id"] for row in training})
    heldout_sequences = sorted({row["sequence_id"] for row in heldout})
    if len(train_sequences) < 2 or len(heldout_sequences) < 2 or set(train_sequences) & set(heldout_sequences):
        raise ValueError("need disjoint whole-sequence training and held-out sets with at least two sequences each")

    fits = {}
    for side in ("left", "right"):
        errors = []
        folds = []
        for sequence_id in train_sequences:
            fold_train = [row for row in training if row["sequence_id"] != sequence_id]
            fold_valid = [row for row in training if row["sequence_id"] == sequence_id]
            gain, offset = fit([(abs(row[f"{side}_input_percent"]) / 100, row[f"{side}_duty_fraction"]) for row in fold_train])
            fold_errors = [abs(row[f"{side}_duty_fraction"] - (gain * abs(row[f"{side}_input_percent"]) / 100 + offset)) * 100 for row in fold_valid]
            errors.extend(fold_errors)
            folds.append({"sequence_id": sequence_id, "n": len(fold_errors), "p95_error_percentage_points": percentile(fold_errors), "max_error_percentage_points": max(fold_errors)})
        gain, offset = fit([(abs(row[f"{side}_input_percent"]) / 100, row[f"{side}_duty_fraction"]) for row in training])
        heldout_errors = [abs(row[f"{side}_duty_fraction"] - (gain * abs(row[f"{side}_input_percent"]) / 100 + offset)) * 100 for row in heldout]
        training_direction_mismatches = [row for row in training if row[f"{side}_direction_forward"] != (row[f"{side}_input_percent"] >= 0)]
        direction_mismatches = [row for row in heldout if row[f"{side}_direction_forward"] != (row[f"{side}_input_percent"] >= 0)]
        direction_folds = [{"sequence_id": sequence_id,
                            "n": sum(1 for row in training if row["sequence_id"] == sequence_id),
                            "mismatches": sum(row[f"{side}_direction_forward"] != (row[f"{side}_input_percent"] >= 0)
                                              for row in training if row["sequence_id"] == sequence_id)}
                           for sequence_id in train_sequences]
        fits[side] = {"gain": gain, "offset": offset, "grouped_training_cv": {"n": len(errors), "p95_error_percentage_points": percentile(errors), "max_error_percentage_points": max(errors), "folds": folds},
                      "heldout_error_percentage_points": {"n": len(heldout_errors), "p95": percentile(heldout_errors), "max": max(heldout_errors)},
                      "training_direction_mismatches_including_zero": len(training_direction_mismatches),
                      "grouped_direction_cv_folds": direction_folds,
                      "heldout_direction_mismatches": len(direction_mismatches)}

    threshold_pass = all(value["grouped_training_cv"]["p95_error_percentage_points"] <= P95_DUTY_THRESHOLD_PP
                         and value["heldout_error_percentage_points"]["p95"] <= P95_DUTY_THRESHOLD_PP
                         and value["training_direction_mismatches_including_zero"] == 0
                         and all(fold["mismatches"] == 0 for fold in value["grouped_direction_cv_folds"])
                         and value["heldout_direction_mismatches"] == 0 for value in fits.values())
    heldout_changes = [row for row in heldout if row["output_changed"]]
    heldout_command_to_output_min = [row["command_to_output_latency_min_ns"] for row in heldout_changes]
    heldout_command_to_output_max = [row["command_to_output_latency_max_ns"] for row in heldout_changes]
    heldout_ack_to_output = [row["ack_to_output_ns"] for row in heldout]
    timing_limit_ns = 8_333_334
    timing_status = "NOT_ENOUGH_TRANSITIONS" if not heldout_changes else "PASS" if max(heldout_command_to_output_max) <= timing_limit_ns else (
        "FAIL" if min(heldout_command_to_output_min) > timing_limit_ns else "UNRESOLVED_INPUT_TIME_BRACKET")
    passed = threshold_pass and timing_status == "PASS"
    status = "RAW_PERIPHERAL_FIT_PASS_PROFILE_UNQUALIFIED" if passed else (
        "RAW_PERIPHERAL_FIT_FAIL" if not threshold_pass or timing_status == "FAIL" else "RAW_PERIPHERAL_FIT_PASS_TIMING_UNRESOLVED_PROFILE_UNQUALIFIED")
    result = {"status": status,
              "profile_fidelity_label": "UNQUALIFIED", "campaign_manifest_sha256": manifest_hash,
              "threshold_declared_before_fit_duty_p95_percentage_points": P95_DUTY_THRESHOLD_PP,
              "timing_gate": {"physics_delta_ns": 8_333_333, "maximum_command_to_first_sample_ns": timing_limit_ns,
                              "heldout_output_transitions": len(heldout_changes), "heldout_commands_without_transition": len(heldout) - len(heldout_changes),
                              "heldout_command_to_output_latency_min_ns": {"n": len(heldout_command_to_output_min),
                                                                            "p50": percentile(heldout_command_to_output_min) if heldout_command_to_output_min else None,
                                                                            "p95": percentile(heldout_command_to_output_min) if heldout_command_to_output_min else None,
                                                                            "max": max(heldout_command_to_output_min) if heldout_command_to_output_min else None},
                              "heldout_command_to_output_latency_max_ns": {"n": len(heldout_command_to_output_max),
                                                                            "p50": percentile(heldout_command_to_output_max) if heldout_command_to_output_max else None,
                                                                            "p95": percentile(heldout_command_to_output_max) if heldout_command_to_output_max else None,
                                                                            "max": max(heldout_command_to_output_max) if heldout_command_to_output_max else None},
                              "heldout_ack_to_output_ns": {"n": len(heldout_ack_to_output), "p50": percentile(heldout_ack_to_output),
                                                           "p95": percentile(heldout_ack_to_output), "max": max(heldout_ack_to_output)},
                              "status": timing_status,
                              "within_one_physics_tick_proven": timing_status == "PASS"},
              "training_sequences": train_sequences, "heldout_sequences": heldout_sequences,
              "training_commands": len(training), "heldout_commands": len(heldout),
              "motor_output_fit": fits,
              "direction_rule": "measured exact: forward GPIO high iff command percentage is nonnegative, including zero; verified as a sign-level candidate on training and held-out command observations",
              "heldout_zero_command_observations": {side: [row for row in heldout if row[f"{side}_input_percent"] == 0]
                                                    for side in ("left", "right")},
              "training_observations": training,
              "heldout_observations": heldout,
              "limitations": ["raw UART byte parsing and reply timing are not modeled", "zero-duty GPIO direction is stateful and intentionally not predicted", "output latency is bounded by the measured physics tick but peripheral switching within a tick is not instrumented", "only measured MOTOR commands and this firmware/board identity are covered", "this fit does not itself qualify a runnable profile"]}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"status": result["status"], "training": len(training), "heldout": len(heldout),
                      "left_duty_p95_pp": fits["left"]["heldout_error_percentage_points"]["p95"],
                      "right_duty_p95_pp": fits["right"]["heldout_error_percentage_points"]["p95"],
                      "output_transitions": len(heldout_changes),
                      "command_to_output_latency_min_max_ns": [min(heldout_command_to_output_min), max(heldout_command_to_output_max)] if heldout_changes else None,
                      "timing_status": timing_status,
                      "direction_mismatches": fits["left"]["heldout_direction_mismatches"] + fits["right"]["heldout_direction_mismatches"],
                      "output": str(args.output)}))
    return 0 if passed else 1


if __name__ == "__main__":
    raise SystemExit(main())
