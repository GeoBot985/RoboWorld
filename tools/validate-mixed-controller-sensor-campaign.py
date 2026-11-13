#!/usr/bin/env python3
"""Validate independent exact-firmware motor/sensor interaction sequences."""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import re
from pathlib import Path

ANALYZER_SPEC = importlib.util.spec_from_file_location("trace_analyzer", Path(__file__).with_name("analyze-controller-trace.py"))
assert ANALYZER_SPEC and ANALYZER_SPEC.loader
analyzer = importlib.util.module_from_spec(ANALYZER_SPEC)
ANALYZER_SPEC.loader.exec_module(analyzer)
MOTOR = re.compile(rb"MOTOR (-?\d{1,3}) (-?\d{1,3})\Z")


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def validate_sequence(root: Path, row: dict, firmware_hash: str) -> dict:
    trace_path = root / row["trace"]
    if sha256(trace_path) != row["trace_sha256"].upper():
        raise ValueError(f"{row['sequence_id']}: trace hash mismatch")
    if row["firmware_sha256"].upper() != firmware_hash:
        raise ValueError(f"{row['sequence_id']}: mixed corpus firmware hash mismatch")
    records = analyzer.load(trace_path)
    inputs = sorted(analyzer.lines(records, "controller_to_mcu"), key=lambda item: item["host"])
    replies = sorted(analyzer.lines(records, "mcu_to_controller"), key=lambda item: item["host"])
    if row.get("probe_move_bypass"):
        move_inputs = [item for item in inputs if item["line"] == b"MOVE 100 100"]
        move_replies = [item for item in replies if item["line"] == b"ERR CMD"]
        if len(move_inputs) != 1 or len(move_replies) != 1 or move_replies[0]["host"] < move_inputs[0]["host"]:
            raise ValueError(f"{row['sequence_id']}: firmware MOVE bypass rejection was missing or not causally ordered")
    motor_inputs = []
    for item in inputs:
        match = MOTOR.fullmatch(item["line"])
        if match:
            left, right = map(int, match.groups())
            if -100 <= left <= 100 and -100 <= right <= 100:
                motor_inputs.append((item, f"{left},{right}"))
    expected = row["motor_sequence"]
    if row.get("motor_sequence_final_hold_s", 0) > 0:
        expected = [*expected, row["terminal_stop_command"]]
    observed = [command for _, command in motor_inputs]
    if observed != expected:
        raise ValueError(f"{row['sequence_id']}: motor sequence differs from its manifest: {observed}")
    motor_acks = [reply for reply in replies if reply["line"] == b"OK MOTOR"]
    if len(motor_acks) < len(expected):
        raise ValueError(f"{row['sequence_id']}: only {len(motor_acks)} MOTOR acknowledgments for {len(expected)} sequence/stop commands")

    samples = sorted((record for record in records if record.get("RecordType") == "DistanceSample"), key=lambda item: item["SimTimeNs"])
    edges = sorted((record for record in records if record.get("RecordType") == "DistanceEchoEdge"), key=lambda item: item["SimTimeNs"])
    rises = [edge for edge in edges if edge["High"]]
    falls = [edge for edge in edges if not edge["High"]]
    if len(samples) < 8 or len(rises) != len(samples) or len(falls) != len(samples):
        raise ValueError(f"{row['sequence_id']}: incomplete echo trace samples={len(samples)} rises={len(rises)} falls={len(falls)}")
    pulse_errors = []
    for sample, rise, fall in zip(samples, rises, falls, strict=True):
        expected_width = int(2 * sample["DistanceMeters"] / 343 * 1_000_000_000 + 0.5)
        if rise["SimTimeNs"] != sample["SimTimeNs"] or fall["SimTimeNs"] - rise["SimTimeNs"] != expected_width:
            raise ValueError(f"{row['sequence_id']}: echo pulse does not preserve the sampled timestamp/width")
        pulse_errors.append(abs((fall["SimTimeNs"] - rise["SimTimeNs"]) - expected_width))

    first_motor_host = motor_inputs[0][0]["host"]
    last_motor_host = motor_inputs[-1][0]["host"]
    sensor_inputs = [item for item in inputs if item["line"] == b"SENSOR?"]
    overlapping_sensor_queries = [item for item in sensor_inputs
                                  if first_motor_host <= item["host"] <= last_motor_host]
    if row.get("sensor_interleave_every_motor_commands", 0) > 0 and len(overlapping_sensor_queries) < 3:
        raise ValueError(f"{row['sequence_id']}: only {len(overlapping_sensor_queries)} SENSOR? UART queries overlapped the motor command sequence")
    overlapping_samples = [sample for sample in samples
                           if first_motor_host <= sample["HostMonotonicTicks"] <= last_motor_host]
    if len(overlapping_samples) < 3:
        raise ValueError(f"{row['sequence_id']}: sensor inputs did not overlap the held-out motor sequence")
    pwm = [record for record in records if record.get("RecordType") == "PwmObservation"]
    mixed_pwm = [record for record in pwm if first_motor_host <= record["HostMonotonicTicks"] <= last_motor_host + 2_000_000]
    if not mixed_pwm:
        raise ValueError(f"{row['sequence_id']}: no Renode PWM observations overlap the MOTOR sequence")

    evaluation_path = root / row["sensor_evaluation"]
    if sha256(evaluation_path) != row["sensor_evaluation_sha256"].upper():
        raise ValueError(f"{row['sequence_id']}: sensor evaluation hash mismatch")
    evaluation = json.loads(evaluation_path.read_text(encoding="utf-8-sig"))
    if evaluation["causally_matched_valid_replies"] < 3 or evaluation["absolute_error_mm"]["p95"] > 2.0:
        raise ValueError(f"{row['sequence_id']}: exact firmware sensor acceptance failed: {evaluation['absolute_error_mm']}")
    return {
        "sequence_id": row["sequence_id"], "split": row["split"], "motor_commands": len(motor_inputs),
        "move_bypass_rejections": 1 if row.get("probe_move_bypass") else 0,
        "motor_acks": len(motor_acks), "distance_samples": len(samples), "overlapping_sensor_samples": len(overlapping_samples),
        "overlapping_sensor_uart_queries": len(overlapping_sensor_queries),
        "echo_pulses": len(rises), "overlapping_pwm_observations": len(mixed_pwm),
        "causally_matched_sensor_replies": evaluation["causally_matched_valid_replies"],
        "sensor_reply_error_p95_mm": evaluation["absolute_error_mm"]["p95"],
        "sensor_reply_error_max_mm": evaluation["absolute_error_mm"]["max"],
        "echo_pulse_width_error_max_ns": max(pulse_errors), "trace_sha256": row["trace_sha256"].upper(),
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("campaign", type=Path)
    parser.add_argument("--firmware", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    root = args.campaign.resolve()
    manifest_path = root / "sequence-manifest.json"
    rows = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    if len(rows) < 4 or len({row["sequence_id"] for row in rows}) != len(rows):
        raise ValueError("mixed campaign requires at least four uniquely identified whole sequences")
    training = [row for row in rows if row["split"] == "training"]
    heldout = [row for row in rows if row["split"] == "heldout"]
    if len(training) < 2 or len(heldout) < 2 or len(training) + len(heldout) != len(rows):
        raise ValueError("mixed campaign requires at least two training and two held-out whole sequences")
    firmware_hash = sha256(args.firmware)
    results = [validate_sequence(root, row, firmware_hash) for row in rows]
    report = {
        "status": "PASS_EXACT_FIRMWARE_MIXED_CONTROLLER_SENSOR_REFERENCE_ONLY",
        "model_qualification": "NOT_ESTABLISHED",
        "firmware_sha256": firmware_hash,
        "sequence_manifest_sha256": sha256(manifest_path),
        "training_whole_sequences": len(training), "heldout_whole_sequences": len(heldout),
        "scope": "Renode-observed real STM32 motor UART commands and PWM/direction outputs interleaved with Godot raycast samples, sub-tick GPIO echo pulses, and subsequent firmware UART sensor replies. Whole sequences are split before any model fitting.",
        "sequences": results,
        "limitations": ["reference-firmware characterization only; no deterministic model was evaluated by this validator", "does not establish loaded-robot fast-vs-exact trajectory equivalence or runtime performance"]
    }
    output = args.output or root / "mixed-campaign-validation.json"
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"status": report["status"], "training": len(training), "heldout": len(heldout),
                      "motor_commands": sum(item["motor_commands"] for item in results),
                      "overlapping_sensor_samples": sum(item["overlapping_sensor_samples"] for item in results),
                      "output": str(output)}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
