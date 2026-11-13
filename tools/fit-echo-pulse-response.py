#!/usr/bin/env python3
"""Fit the measured MCU echo-pulse-width to UART-distance response by whole sequence."""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import statistics
from pathlib import Path

THRESHOLD_MM_P95 = 2.0


def percentile(values: list[float], probability: float = .95) -> float:
    ordered = sorted(values)
    return ordered[max(0, math.ceil(probability * len(ordered)) - 1)]


def fit(points: list[tuple[float, float]]) -> tuple[float, float]:
    if len(points) < 2:
        raise ValueError("at least two training observations are required")
    mean_x = statistics.mean(x for x, _ in points)
    mean_y = statistics.mean(y for _, y in points)
    denominator = sum((x - mean_x) ** 2 for x, _ in points)
    if denominator <= 0:
        raise ValueError("training pulse widths do not vary")
    gain = sum((x - mean_x) * (y - mean_y) for x, y in points) / denominator
    return gain, mean_y - gain * mean_x


def load_rows(campaign: Path) -> tuple[list[dict], str]:
    manifest_path = campaign / "sensor-sequence-manifest.json"
    manifest_bytes = manifest_path.read_bytes()
    manifest = json.loads(manifest_bytes)
    rows = []
    for sequence in manifest:
        trace_path = campaign / sequence["trace"]
        evaluation_path = campaign / sequence["sensor_evaluation"]
        if hashlib.sha256(trace_path.read_bytes()).hexdigest().upper() != sequence["trace_sha256"].upper():
            raise ValueError(f"trace hash mismatch: {sequence['sequence_id']}")
        evaluation = json.loads(evaluation_path.read_text(encoding="utf-8-sig"))
        if hashlib.sha256(evaluation_path.read_bytes()).hexdigest().upper() != sequence["sensor_evaluation_sha256"].upper():
            raise ValueError(f"evaluation hash mismatch: {sequence['sequence_id']}")
        records = [json.loads(line) for line in trace_path.read_text(encoding="utf-8").splitlines() if line]
        samples = {row["SimTimeNs"]: row for row in records if row.get("RecordType") == "DistanceSample"}
        edges = [row for row in records if row.get("RecordType") == "DistanceEchoEdge"]
        for reply in evaluation["replies"]:
            if not reply.get("valid") or "reference_sample_sim_time_ns" not in reply:
                continue
            sample_time = reply["reference_sample_sim_time_ns"]
            rise = next((edge for edge in edges if edge["High"] and edge["SimTimeNs"] == sample_time), None)
            fall_time = reply.get("reference_echo_fall_sim_time_ns")
            fall = next((edge for edge in edges if not edge["High"] and edge["SimTimeNs"] == fall_time), None)
            if rise is None or fall is None or fall["SimTimeNs"] <= rise["SimTimeNs"]:
                raise ValueError(f"reply lacks a complete causally paired echo pulse: {sequence['sequence_id']}")
            rows.append({"sequence_id": sequence["sequence_id"], "split": sequence["split"],
                         "pulse_width_ns": fall["SimTimeNs"] - rise["SimTimeNs"],
                         "reply_mm": float(reply["reply_mm"]), "rise_sim_time_ns": rise["SimTimeNs"],
                         "fall_sim_time_ns": fall["SimTimeNs"]})
    return rows, hashlib.sha256(manifest_bytes).hexdigest().upper()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("campaign_dir", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    rows, manifest_hash = load_rows(args.campaign_dir.resolve())
    training = [row for row in rows if row["split"] == "training"]
    heldout = [row for row in rows if row["split"] == "heldout"]
    train_sequences = sorted({row["sequence_id"] for row in training})
    heldout_sequences = sorted({row["sequence_id"] for row in heldout})
    if len(train_sequences) < 2 or len(heldout_sequences) < 2 or set(train_sequences) & set(heldout_sequences):
        raise ValueError("need disjoint whole-sequence training and held-out sets with at least two sequences each")

    cv_errors = []
    folds = []
    for sequence_id in train_sequences:
        fold_train = [row for row in training if row["sequence_id"] != sequence_id]
        fold_valid = [row for row in training if row["sequence_id"] == sequence_id]
        gain, offset = fit([(row["pulse_width_ns"], row["reply_mm"]) for row in fold_train])
        errors = [abs(row["reply_mm"] - (gain * row["pulse_width_ns"] + offset)) for row in fold_valid]
        cv_errors.extend(errors)
        folds.append({"sequence_id": sequence_id, "n": len(errors), "p95_error_mm": percentile(errors), "max_error_mm": max(errors)})

    gain, offset = fit([(row["pulse_width_ns"], row["reply_mm"]) for row in training])
    minimum = min(row["pulse_width_ns"] for row in training)
    maximum = max(row["pulse_width_ns"] for row in training)
    in_domain = all(minimum <= row["pulse_width_ns"] <= maximum for row in heldout)
    heldout_errors = [abs(row["reply_mm"] - (gain * row["pulse_width_ns"] + offset)) for row in heldout]
    passed = in_domain and percentile(cv_errors) <= THRESHOLD_MM_P95 and percentile(heldout_errors) <= THRESHOLD_MM_P95
    result = {
        "status": "PULSE_WIDTH_RESPONSE_FIT_PASS" if passed else "PULSE_WIDTH_RESPONSE_FIT_FAIL",
        "profile_fidelity_label": "UNQUALIFIED",
        "campaign_manifest_sha256": manifest_hash,
        "input_signal": "measured DistanceEchoEdge rise-to-fall duration on authoritative simulation time",
        "output_signal": "measured integer UART DIST mm response from exact firmware",
        "threshold_declared_before_fit_mm_p95": THRESHOLD_MM_P95,
        "training_sequences": train_sequences, "heldout_sequences": heldout_sequences,
        "training_observations": len(training), "heldout_observations": len(heldout),
        "candidate": {"operator": "affine", "gain_mm_per_ns": gain, "offset_mm": offset,
                      "training_envelope_ns": {"minimum": minimum, "maximum": maximum}},
        "grouped_training_cv": {"n": len(cv_errors), "p95_error_mm": percentile(cv_errors),
                                 "max_error_mm": max(cv_errors), "folds": folds},
        "heldout_within_training_envelope": in_domain,
        "heldout_error_mm": {"n": len(heldout_errors), "p95": percentile(heldout_errors), "max": max(heldout_errors)},
        "heldout_observations": heldout,
        "runtime_eligibility": "FIT_ONLY: runtime eligibility requires the profile builder's bounded-state, UART, causality, and out-of-domain gates",
        "limitations": ["six whole sequences, one firmware build and one sensor scene", "only valid, causally paired replies are represented", "does not cover no-echo, timeout, overlap, malformed command, fault or recovery behavior", "fit is characterization evidence, not a qualified controller profile"],
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"status": result["status"], "training": len(training), "heldout": len(heldout),
                      "heldout_p95_mm": result["heldout_error_mm"]["p95"], "output": str(args.output)}))
    return 0 if passed else 1


if __name__ == "__main__":
    raise SystemExit(main())
