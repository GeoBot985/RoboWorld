#!/usr/bin/env python3
"""Verify raw Exact Renode arm MOTOR campaign completeness and held-out outputs.

This validates characterization evidence only. It does not qualify a Fast controller profile.
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import json
import math
from pathlib import Path


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def verify_sequence(root: Path, item: dict, firmware_hash: str) -> dict:
    if item["firmware_sha256"].lower() != firmware_hash:
        raise ValueError(f"{item['sequence_id']}: firmware identity mismatch")
    if item["robot_id"] != "arm-01" or item["controller_id"] != "arm-controller":
        raise ValueError(f"{item['sequence_id']}: unexpected robot/controller identity")
    trace_path = root / item["trace"]
    controller_path = root / item["controller_log"]
    stdout_path = root / item["godot_stdout"]
    for key, path in (("trace_sha256", trace_path), ("controller_log_sha256", controller_path),
                      ("godot_stdout_sha256", stdout_path)):
        if sha256(path) != item[key]:
            raise ValueError(f"{item['sequence_id']}: {key} mismatch")
    rows = [json.loads(line) for line in trace_path.read_text(encoding="utf-8").splitlines() if line]
    if not rows or any(row.get("SchemaVersion") != 1 for row in rows):
        raise ValueError(f"{item['sequence_id']}: missing or unsupported trace schema")
    if any(b["Sequence"] <= a["Sequence"] for a, b in zip(rows, rows[1:])):
        raise ValueError(f"{item['sequence_id']}: trace sequence is not strictly increasing")
    if any(b["SimTimeNs"] < a["SimTimeNs"] for a, b in zip(
        [r for r in rows if r["RecordType"] == "RenodeTimeBoundary"],
        [r for r in rows if r["RecordType"] == "RenodeTimeBoundary"][1:]
    )):
        raise ValueError(f"{item['sequence_id']}: Renode time moved backwards")

    requests: list[str] = []
    tx_bytes = b""
    rx_bytes = b""
    motor_schedules: list[tuple[str, int]] = []
    for row in rows:
        if row["RecordType"] != "UartChunk":
            continue
        payload = base64.b64decode(row["BytesBase64"], validate=True)
        if row["Direction"] == "controller_to_mcu":
            tx_bytes += payload
            if row.get("ScheduledSimTimeNs") is None:
                raise ValueError(f"{item['sequence_id']}: controller UART input lacks its tick boundary")
            text = payload.decode("ascii", errors="strict")
            for line in text.splitlines():
                requests.append(line)
                if line.startswith("MOTOR "):
                    motor_schedules.append((line, int(row["ScheduledSimTimeNs"])))
        elif row["Direction"] == "mcu_to_controller":
            rx_bytes += payload
    request_lines = tx_bytes.decode("ascii", errors="strict").splitlines()
    response_lines = rx_bytes.decode("ascii", errors="strict").splitlines()
    expected_requests = ["PING"] + [f"MOTOR {pair.replace(',', ' ')}" for pair in item["commands"]] + ["MOTOR 0 0"]
    if request_lines != expected_requests:
        raise ValueError(f"{item['sequence_id']}: traced UART requests differ from the recorded sequence")
    boot_id = next((line.split(" ", 1)[1] for line in response_lines if line.startswith("BOOT ")), None)
    if boot_id is None or response_lines != [f"BOOT {boot_id}", f"PONG {boot_id}"] + ["OK MOTOR"] * 4:
        raise ValueError(f"{item['sequence_id']}: traced firmware replies are incomplete or out of order")

    pwm = [row for row in rows if row["RecordType"] == "PwmObservation"]
    output_checks = []
    if len(motor_schedules) != len(item["commands"]) + 1:
        raise ValueError(f"{item['sequence_id']}: missing one or more timestamped MOTOR input boundaries")
    for command, scheduled_ns in motor_schedules:
        parts = command.split()
        left, right = int(parts[1]), int(parts[2])
        # At the scheduled boundary Godot/host may have sampled the previous state
        # before firmware parses the bytes; the following boundary is the first
        # observation that can causally contain the command's output.
        candidates = [row for row in pwm if int(row["SimTimeNs"]) > scheduled_ns]
        if not candidates:
            raise ValueError(f"{item['sequence_id']}: no per-tick output follows {command!r}")
        observed = min(candidates, key=lambda row: int(row["SimTimeNs"]))
        expected_left = left / 100.0
        expected_right = 0.0  # Arm firmware intentionally drives TIM3 channel 1 only.
        matches = (math.isclose(float(observed["Channel1SignedDuty"]), expected_left, abs_tol=1e-9)
                   and math.isclose(float(observed["Channel2SignedDuty"]), expected_right, abs_tol=1e-9)
                   and observed["Channel1DirectionForward"] is (left >= 0)
                   and observed["Channel2DirectionForward"] is (right >= 0))
        delta_ns = int(observed["SimTimeNs"]) - scheduled_ns
        if not matches:
            raise ValueError(f"{item['sequence_id']}: {command!r} output mismatch: {observed}")
        output_checks.append({"command": command, "scheduled_sim_time_ns": scheduled_ns,
                              "observed_sim_time_ns": int(observed["SimTimeNs"]),
                              "input_to_sample_ns": delta_ns, "channel1_signed_duty": expected_left,
                              "channel2_signed_duty": expected_right,
                              "channel1_direction_forward": left >= 0,
                              "channel2_direction_forward": right >= 0})
    return {"sequence_id": item["sequence_id"], "split": item["split"],
            "command_count": len(item["commands"]), "measured_shoulder_angle_rad": item["measured_shoulder_angle_rad"],
            "trace_record_count": len(rows), "firmware_reply_lines": response_lines,
            "per_tick_output_checks": output_checks}


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("campaign", type=Path)
    parser.add_argument("--firmware", type=Path, default=Path("samples/firmware/stm32-arm/build/firmware.elf"))
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    root = args.campaign.resolve()
    manifest_path = root / "sequence-manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    actual_firmware_hash = sha256(args.firmware.resolve())
    splits = {name: [row for row in manifest if row["split"] == name] for name in ("training", "heldout")}
    if len(manifest) != 6 or len(splits["training"]) != 4 or len(splits["heldout"]) != 2:
        raise SystemExit("Expected six independently recorded whole sequences: four training and two held-out.")
    sequences = [verify_sequence(root, row, actual_firmware_hash) for row in manifest]
    heldout = [seq for seq in sequences if seq["split"] == "heldout"]
    latencies = [check["input_to_sample_ns"] for seq in heldout for check in seq["per_tick_output_checks"]]
    result = {
        "status": "EXACT_FIRMWARE_ARM_MOTOR_CHARACTERIZATION_VERIFIED",
        "scope": "C01 reference characterization only; not a qualified deterministic profile and not Fast runtime acceptance",
        "campaign_manifest_sha256": sha256(manifest_path),
        "firmware_sha256": actual_firmware_hash,
        "sequence_counts": {"total": len(sequences), "training": len(splits["training"]), "heldout": len(splits["heldout"])},
        "heldout_output_check_count": len(latencies),
        "heldout_output_input_to_first_sample_ns": {"minimum": min(latencies), "maximum": max(latencies)},
        "arm_channel_behavior": "MOTOR left operand drives signed TIM3 channel 1 duty and PD2 direction; channel 2 duty remains zero; PD3 follows the right operand's sign.",
        "sequences": sequences,
        "qualification_status": "NOT_QUALIFIED",
        "limitations": ["Only the sampled normal MOTOR command envelope is captured.",
                        "UART states outside PING and the recorded MOTOR sequence are not characterized.",
                        "This campaign does not prove SENSOR? behavior, fault behavior, whole-profile acceptance, or Fast-model fidelity."],
    }
    output = args.output.resolve() if args.output else root / "arm-motor-campaign-validation.json"
    output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({key: result[key] for key in ("status", "sequence_counts", "heldout_output_check_count", "heldout_output_input_to_first_sample_ns", "qualification_status")}, indent=2))


if __name__ == "__main__":
    main()
