#!/usr/bin/env python3
"""Verify hashes, whole-sequence splits, timed PA0 pulses, and causal sensor evaluations."""
from __future__ import annotations

import argparse
import hashlib
import json
import math
from pathlib import Path


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def percentile(values: list[float], probability: float) -> float:
    ordered = sorted(values)
    return ordered[max(0, math.ceil(probability * len(ordered)) - 1)]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("campaign_dir", type=Path)
    args = parser.parse_args()
    campaign = args.campaign_dir.resolve()
    manifest_path = campaign / "sensor-sequence-manifest.json"
    rows = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    if len(rows) < 4:
        raise ValueError("at least four whole sequences are required")
    ids = [row["sequence_id"] for row in rows]
    if len(set(ids)) != len(ids) or len({row["seed"] for row in rows}) != len(rows):
        raise ValueError("sequence IDs and seeds must be unique")
    training = [row for row in rows if row["split"] == "training"]
    heldout = [row for row in rows if row["split"] == "heldout"]
    if len(training) < 2 or len(heldout) < 2 or len(training) + len(heldout) != len(rows):
        raise ValueError("require separate training and held-out sets with at least two complete sequences each")
    firmware_hashes = {row["firmware_sha256"].upper() for row in rows}
    if len(firmware_hashes) != 1:
        raise ValueError("campaign mixes firmware images")

    sequence_results = []
    heldout_errors: list[float] = []
    all_replies: list[int] = []
    all_ranges: list[float] = []
    total_samples = total_edges = total_queries = 0
    for row in rows:
        trace_path = campaign / row["trace"]
        evaluation_path = campaign / row["sensor_evaluation"]
        trajectory_path = campaign.parent.parent / row["godot_trajectory"]
        for path, expected, name in (
            (trace_path, row["trace_sha256"], "trace"),
            (evaluation_path, row["sensor_evaluation_sha256"], "sensor evaluation"),
            (trajectory_path, row["godot_trajectory_sha256"], "Godot trajectory"),
        ):
            if not path.is_file() or sha256(path) != expected.upper():
                raise ValueError(f"{row['sequence_id']}: missing or hash-mismatched {name}: {path}")
        evaluation = json.loads(evaluation_path.read_text(encoding="utf-8-sig"))
        if evaluation["status"] != "REFERENCE_FIRMWARE_OBSERVATION_NOT_MODEL_QUALIFICATION":
            raise ValueError(f"{row['sequence_id']}: unexpected sensor evaluation status")
        if evaluation["causally_matched_valid_replies"] != 4 or len(evaluation["replies"]) != 4:
            raise ValueError(f"{row['sequence_id']}: expected four causally matched valid replies")
        if not all(reply["valid"] for reply in evaluation["replies"]):
            raise ValueError(f"{row['sequence_id']}: invalid firmware sensor reply")

        records = [json.loads(line) for line in trace_path.read_text(encoding="utf-8-sig").splitlines() if line.strip()]
        samples = [item for item in records if item.get("RecordType") == "DistanceSample"]
        rises = [item for item in records if item.get("RecordType") == "DistanceEchoEdge" and item["High"]]
        falls = [item for item in records if item.get("RecordType") == "DistanceEchoEdge" and not item["High"]]
        if len(samples) < 4 or len(samples) != len(rises) or len(rises) != len(falls):
            raise ValueError(f"{row['sequence_id']}: sensor samples and timed echo edges are incomplete")
        for sample, rise, fall in zip(samples, rises, falls, strict=True):
            expected_width_ns = round(2 * sample["DistanceMeters"] / 343 * 1_000_000_000)
            if rise["SimTimeNs"] != sample["SimTimeNs"] or fall["SimTimeNs"] - rise["SimTimeNs"] != expected_width_ns:
                raise ValueError(f"{row['sequence_id']}: echo timestamp/width mismatch")
        ranges = [sample["DistanceMeters"] for sample in samples]
        if len({round(value, 2) for value in ranges}) < 3 or max(ranges) - min(ranges) < 0.8:
            raise ValueError(f"{row['sequence_id']}: Godot stimulus lacks the required distance diversity")
        replies = [reply["reply_mm"] for reply in evaluation["replies"]]
        errors = [reply["absolute_error_mm"] for reply in evaluation["replies"]]
        if len(set(replies)) < 3 or max(replies) - min(replies) < 800:
            raise ValueError(f"{row['sequence_id']}: firmware responses lack the required distance diversity")
        if int(row["causal_valid_replies"]) != 4:
            raise ValueError(f"{row['sequence_id']}: manifest causal reply count does not match evaluation")
        if row["split"] == "heldout":
            heldout_errors.extend(errors)
        all_replies.extend(replies)
        all_ranges.extend(ranges)
        total_samples += len(samples)
        total_edges += len(rises) + len(falls)
        total_queries += len(evaluation["replies"])
        sequence_results.append({
            "sequence_id": row["sequence_id"], "split": row["split"], "samples": len(samples),
            "queries": len(replies), "distinct_firmware_replies": len(set(replies)),
            "firmware_reply_span_mm": max(replies) - min(replies),
            "error_p95_mm": evaluation["absolute_error_mm"]["p95"],
            "error_max_mm": evaluation["absolute_error_mm"]["max"],
        })

    heldout_summary = {
        "n": len(heldout_errors), "p50_mm": percentile(heldout_errors, 0.5),
        "p95_mm": percentile(heldout_errors, 0.95), "max_mm": max(heldout_errors),
    }
    report = {
        "status": "C01_SENSOR_REFERENCE_CAMPAIGN_PASS_NOT_MODEL_QUALIFICATION",
        "firmware_sha256": next(iter(firmware_hashes)),
        "sequence_count": len(rows), "training_sequences": len(training), "heldout_sequences": len(heldout),
        "training_heldout_are_disjoint_whole_sequences": True,
        "sensor_samples": total_samples, "timed_echo_edges": total_edges, "firmware_queries": total_queries,
        "godot_distance_span_m": {"min": min(all_ranges), "max": max(all_ranges)},
        "firmware_reply_span_mm": {"min": min(all_replies), "max": max(all_replies)},
        "heldout_reference_error_mm": heldout_summary,
        "sequences": sequence_results,
        "limitations": [
            "reference firmware observations only; no sensor/controller model was fitted",
            "one firmware, one reset/scene configuration, deterministic four-position ranges",
            "host UART terminal does not serialize bytes at baud; these data do not establish UART timing fidelity",
            "does not validate faults, noisy/invalid sensor inputs, full protocol grammar, trajectory model fidelity, or arbitrary histories",
        ],
    }
    report_path = campaign / "sensor-campaign-evaluation.json"
    report_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    lines = [
        "# C01 sensor range campaign — reference observations, not model qualification", "",
        f"Status: **{report['status']}**", "",
        f"- Firmware SHA-256: `{report['firmware_sha256']}`",
        f"- Complete sequences: {len(rows)} ({len(training)} training, {len(heldout)} whole-sequence held-out)",
        f"- Timed sensor samples / echo edges: {total_samples} / {total_edges}",
        f"- Valid causal firmware replies: {total_queries}",
        f"- Godot range span: {min(all_ranges):.3f}–{max(all_ranges):.3f} m",
        f"- Firmware reply span: {min(all_replies)}–{max(all_replies)} mm",
        f"- Held-out reference error: p50 {heldout_summary['p50_mm']:.3f} mm, p95 {heldout_summary['p95_mm']:.3f} mm, max {heldout_summary['max_mm']:.3f} mm (n={heldout_summary['n']})",
        "", "## Per-sequence results", "", "| Sequence | Split | Samples | Replies | Distinct reply values | Reply span (mm) | p95 error (mm) |",
        "|---|---:|---:|---:|---:|---:|---:|",
    ]
    lines.extend(
        f"| {item['sequence_id']} | {item['split']} | {item['samples']} | {item['queries']} | {item['distinct_firmware_replies']} | {item['firmware_reply_span_mm']} | {item['error_p95_mm']:.3f} |"
        for item in sequence_results
    )
    lines.extend(["", "The validator checked every trace/evaluation/trajectory hash, verified each timed PA0 pulse against its distance formula, confirmed causal firmware pairings, and kept complete held-out sequences disjoint from the training sequences. No model was fit using the held-out observations.", "", "## Limits", ""])
    lines.extend(f"- {item}" for item in report["limitations"])
    (campaign / "campaign-report.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(json.dumps({"status": report["status"], "sequences": len(rows), "heldout": len(heldout), "heldout_p95_mm": heldout_summary["p95_mm"], "report": str(report_path)}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
