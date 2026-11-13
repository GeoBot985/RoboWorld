#!/usr/bin/env python3
"""Induce and held-out validate a bounded numeric distance-response candidate."""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import statistics
from pathlib import Path


# Declared before fitting: two millimetres bounds integer-mm UART quantization,
# the 1 us MCU timer granularity, and the observed echo/raycast edge uncertainty.
SENSOR_P95_THRESHOLD_MM = 2.0


def percentile(values: list[float], probability: float = 0.95) -> float:
    ordered = sorted(values)
    return ordered[max(0, math.ceil(probability * len(ordered)) - 1)]


def fit(points: list[tuple[float, float]], operator: str) -> tuple[float, float]:
    if not points:
        raise ValueError("sensor model fit has no samples")
    if operator == "constant":
        return 0.0, statistics.mean(output for _, output in points)
    if operator == "affine":
        mean_x = statistics.mean(value for value, _ in points)
        mean_y = statistics.mean(value for _, value in points)
        denominator = sum((value - mean_x) ** 2 for value, _ in points)
        if denominator <= 0:
            raise ValueError("training ranges do not vary")
        gain = sum((value - mean_x) * (output - mean_y) for value, output in points) / denominator
        return gain, mean_y - gain * mean_x
    raise ValueError(f"unsupported sensor operator {operator}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("campaign_dir", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    campaign = args.campaign_dir.resolve()
    manifest_path = campaign / "sensor-sequence-manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    events = []
    for row in manifest:
        evaluation_path = campaign / row["sensor_evaluation"]
        if hashlib.sha256((campaign / row["trace"]).read_bytes()).hexdigest().upper() != row["trace_sha256"].upper():
            raise ValueError(f"trace hash mismatch: {row['sequence_id']}")
        if hashlib.sha256(evaluation_path.read_bytes()).hexdigest().upper() != row["sensor_evaluation_sha256"].upper():
            raise ValueError(f"sensor evaluation hash mismatch: {row['sequence_id']}")
        evaluation = json.loads(evaluation_path.read_text(encoding="utf-8-sig"))
        if evaluation["causally_matched_valid_replies"] != 4:
            raise ValueError(f"causal input/output count is incomplete: {row['sequence_id']}")
        for reply in evaluation["replies"]:
            events.append({
                "sequence_id": row["sequence_id"], "split": row["split"],
                "input_m": reply["raycast_distance_mm"] / 1000.0,
                "output_mm": float(reply["reply_mm"]),
                "sim_time_ns": reply["reference_sample_sim_time_ns"],
            })
    training = [event for event in events if event["split"] == "training"]
    heldout = [event for event in events if event["split"] == "heldout"]
    train_sequences = sorted({event["sequence_id"] for event in training})
    heldout_sequences = sorted({event["sequence_id"] for event in heldout})
    if len(train_sequences) < 2 or len(heldout_sequences) < 2:
        raise ValueError("need at least two complete training and two complete held-out sequences")
    if set(train_sequences) & set(heldout_sequences):
        raise ValueError("whole-sequence training/held-out split is not disjoint")

    selection = {}
    for operator, complexity in (("constant", 1), ("affine", 2)):
        errors = []
        folds = []
        for sequence_id in train_sequences:
            fold_train = [event for event in training if event["sequence_id"] != sequence_id]
            fold_valid = [event for event in training if event["sequence_id"] == sequence_id]
            gain, offset = fit([(event["input_m"], event["output_mm"]) for event in fold_train], operator)
            fold_errors = [abs(event["output_mm"] - (gain * event["input_m"] + offset)) for event in fold_valid]
            errors.extend(fold_errors)
            folds.append({"sequence_id": sequence_id, "n": len(fold_errors), "p95_error_mm": percentile(fold_errors), "max_error_mm": max(fold_errors)})
        selection[operator] = {
            "complexity": complexity,
            "grouped_training_cv": {"n": len(errors), "p95_error_mm": percentile(errors), "max_error_mm": max(errors), "folds": folds},
            "meets_predeclared_p95_threshold": percentile(errors) <= SENSOR_P95_THRESHOLD_MM,
        }
    eligible = [operator for operator in selection if selection[operator]["meets_predeclared_p95_threshold"]]
    if not eligible:
        raise ValueError("no candidate meets the predeclared sensor cross-validation gate")
    selected = min(eligible, key=lambda operator: (selection[operator]["complexity"], selection[operator]["grouped_training_cv"]["p95_error_mm"]))
    train_points = [(event["input_m"], event["output_mm"]) for event in training]
    gain, offset = fit(train_points, selected)
    minimum_input = min(value for value, _ in train_points)
    maximum_input = max(value for value, _ in train_points)
    heldout_inputs_in_domain = all(minimum_input <= event["input_m"] <= maximum_input for event in heldout)
    heldout_errors = [abs(event["output_mm"] - (gain * event["input_m"] + offset)) for event in heldout]
    cv_pass = selection[selected]["meets_predeclared_p95_threshold"]
    heldout_pass = heldout_inputs_in_domain and percentile(heldout_errors) <= SENSOR_P95_THRESHOLD_MM
    model_document = {
        "schemaVersion": 1,
        "modelId": "rover-sensor-numeric-response-candidate-v1",
        "maximumRules": 1,
        "rules": [{
            "inputEndpointId": "sensor.distance_m", "inputKind": "AnalogSample",
            "outputEndpointId": "uart.sensor.distance_mm", "outputKind": "AnalogSample",
            "operator": "Affine", "gain": gain, "offset": offset,
            "minimumInput": minimum_input, "maximumInput": maximum_input, "unit": "mm",
        }],
    }
    report = {
        "status": "NUMERIC_SENSOR_RESPONSE_CANDIDATE_PASS" if cv_pass and heldout_pass else "NUMERIC_SENSOR_RESPONSE_CANDIDATE_FAIL",
        "profile_fidelity_label": "UNQUALIFIED",
        "campaign_manifest_sha256": hashlib.sha256(manifest_path.read_bytes()).hexdigest().upper(),
        "threshold_declared_before_fit_mm_p95": SENSOR_P95_THRESHOLD_MM,
        "threshold_basis": "1 mm reported UART unit resolution and 1 us MCU timer granularity, with margin for measured input edge uncertainty",
        "training_sequences": train_sequences, "heldout_sequences": heldout_sequences,
        "training_observations": len(training), "heldout_observations": len(heldout),
        "candidate_selection": selection, "selected_operator": selected,
        "model_document": model_document,
        "training_input_envelope_m": {"minimum": minimum_input, "maximum": maximum_input},
        "heldout_inputs_within_training_envelope": heldout_inputs_in_domain,
        "heldout_error_mm": {"n": len(heldout_errors), "p50": percentile(heldout_errors, 0.5), "p95": percentile(heldout_errors), "max": max(heldout_errors)},
        "heldout_observations": heldout,
        "runtime_eligibility": "NOT_ELIGIBLE: numeric response only; does not implement UART grammar, MCU timing/faults, or the complete controller contract",
        "limitations": [
            "parsed numeric response only; does not model raw UART command/response bytes",
            "one firmware/reset/scene and a deterministic range sequence; the held-out split is by whole sessions",
            "does not prove hidden firmware state, fault/recovery, full trajectory, or arbitrary sensor history",
            "does not qualify any complete controller profile or authorize fast runtime execution",
        ],
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"status": report["status"], "operator": selected, "training": len(training), "heldout": len(heldout), "heldout_p95_mm": report["heldout_error_mm"]["p95"], "output": str(args.output)}))
    return 0 if cv_pass and heldout_pass else 1


if __name__ == "__main__":
    raise SystemExit(main())
