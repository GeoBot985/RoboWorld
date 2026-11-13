#!/usr/bin/env python3
"""Independently re-hash and replay whole-sequence no-echo reference evidence."""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
from pathlib import Path


def load_module(name: str, file_name: str):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(file_name))
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


evaluator = load_module("noecho_evaluator", "evaluate-sensor-noecho-trace.py")


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def contained(root: Path, relative: str) -> Path:
    path = (root / relative).resolve()
    if not path.is_relative_to(root.resolve()):
        raise ValueError(f"campaign path escapes evidence root: {relative}")
    return path


def validate(campaign: Path, workspace: Path) -> dict:
    manifest_path = campaign / "sequence-manifest.json"
    rows = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    if not isinstance(rows, list) or len(rows) < 4:
        raise ValueError("manifest must contain at least four complete sequences")
    ids = [row["sequence_id"] for row in rows]
    seeds = [row["seed"] for row in rows]
    if len(ids) != len(set(ids)) or len(seeds) != len(set(seeds)):
        raise ValueError("sequence IDs and independent seeds must be unique")
    training = [row for row in rows if row["split"] == "training"]
    heldout = [row for row in rows if row["split"] == "heldout"]
    if len(training) < 2 or len(heldout) < 2 or set(row["seed"] for row in training) & set(row["seed"] for row in heldout):
        raise ValueError("at least two complete training and two disjoint held-out sequences are required")
    firmware_hashes = {row["firmware_sha256"].upper() for row in rows}
    if len(firmware_hashes) != 1:
        raise ValueError("campaign mixes firmware images")
    output_rows = []
    for row in rows:
        sequence_id = row["sequence_id"]
        trace = contained(campaign, row["trace"])
        evaluation_path = contained(campaign, row["evaluation"])
        summary = contained(campaign, f"{sequence_id}/noecho-summary.txt")
        controller_log = contained(campaign, f"{sequence_id}/controller-noecho.log")
        trajectory = contained(workspace, f"artifacts/evidence/{row['evidence_task']}/godot-actuation-loaded.csv")
        if sha256(trace) != row["trace_sha256"].upper():
            raise ValueError(f"{sequence_id}: trace hash mismatch")
        if sha256(evaluation_path) != row["evaluation_sha256"].upper():
            raise ValueError(f"{sequence_id}: evaluation hash mismatch")
        if sha256(trajectory) != row["godot_trajectory_sha256"].upper():
            raise ValueError(f"{sequence_id}: loaded-physics trajectory hash mismatch")
        if sha256(workspace / "samples/firmware/stm32-drive/build/firmware.elf") != row["firmware_sha256"].upper():
            raise ValueError(f"{sequence_id}: reference firmware hash mismatch")
        stored = json.loads(evaluation_path.read_text(encoding="utf-8"))
        recomputed = evaluator.evaluate(trace, controller_log)
        if recomputed != stored:
            raise ValueError(f"{sequence_id}: stored evaluation does not match independent trace replay")
        if recomputed["post_no_echo_replies"] < 1 or recomputed["in_range_samples"] < 1:
            raise ValueError(f"{sequence_id}: required pre- and post-transition observations are absent")
        if row["post_no_echo_replies"] != recomputed["post_no_echo_replies"]:
            raise ValueError(f"{sequence_id}: manifest count differs from re-evaluated trace")
        if "retains prior valid reading and valid=1" not in recomputed["post_no_echo_firmware_behavior"]:
            raise ValueError(f"{sequence_id}: expected retained-state result is absent")
        output_rows.append({"sequence_id": sequence_id, "split": row["split"],
                            "in_range_samples": recomputed["in_range_samples"],
                            "no_echo_samples": recomputed["no_echo_samples"],
                            "post_no_echo_replies": recomputed["post_no_echo_replies"],
                            "trace_sha256": row["trace_sha256"], "evaluation_sha256": row["evaluation_sha256"]})
    return {"status": "PASS_MEASURED_REFERENCE_ONLY", "model_qualification": "UNQUALIFIED",
            "sequence_count": len(rows), "training_sequence_count": len(training), "heldout_sequence_count": len(heldout),
            "firmware_sha256": next(iter(firmware_hashes)), "sequences": output_rows,
            "limits": ["reference-firmware evidence is not fast-model qualification", "does not validate incomplete high-pulse timeout, overlap/noise, reset recovery, loaded motion equivalence, or arbitrary firmware state"]}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("campaign", type=Path)
    parser.add_argument("--workspace", type=Path, default=Path(__file__).resolve().parents[1])
    args = parser.parse_args()
    campaign = args.campaign.resolve()
    result = validate(campaign, args.workspace.resolve())
    validation_path = campaign / "campaign-validation.json"
    validation_path.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    reply_total = sum(row["post_no_echo_replies"] for row in result["sequences"])
    report = ["# C01 no-echo / stale-reading campaign", "",
              "**Validation:** PASS for measured exact-firmware reference behavior; this is not model qualification.", "",
              f"- Reference firmware SHA-256: `{result['firmware_sha256']}`",
              f"- Complete sequences: {result['sequence_count']} ({result['training_sequence_count']} training, {result['heldout_sequence_count']} whole held-out)",
              f"- Replayed post-no-echo UART replies: {reply_total}",
              "- Stimulus: real Godot physics raycast; the obstacle moves outside the sensor cone after a per-run sample index. Negative ray samples cause the Renode host to inject no PA0 pulse.",
              "- Timing evidence: distance samples, PA0 edge timestamps, Renode advancement boundaries, UART request/reply bytes, and loaded-physics trajectories are independently hashed and rechecked.",
              "- Observed firmware behavior: after one or more valid echoes, later no-echo samples leave the last distance and `valid=1` unchanged. This is stale retained state, not a zero/invalid timeout.",
              "- Runtime consequence: the deterministic model currently rejects no-echo input. The profile remains `UNQUALIFIED`; this campaign expands measured history only and does not authorize fast mode.",
              "- Limits: incomplete-high timeout, noisy/overlapping edges, reset/recovery, exact UART byte timing, loaded trajectory equivalence, and other hidden firmware states remain uncovered.",
              "", "Per-sequence traces, evaluations, logs, trajectory checksums, and split assignments are listed in `sequence-manifest.json`; independent revalidation is in `campaign-validation.json`.", ""]
    (campaign / "campaign-report.md").write_text("\n".join(report), encoding="utf-8")
    print(json.dumps({"status": result["status"], "sequences": result["sequence_count"],
                      "heldout": result["heldout_sequence_count"], "post_no_echo_replies": reply_total}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
