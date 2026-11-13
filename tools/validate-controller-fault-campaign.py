#!/usr/bin/env python3
"""Independently verify timestamped exact-firmware fault campaign artifacts."""

from __future__ import annotations

import hashlib
import csv
import json
import sys
from pathlib import Path


FIRMWARE_SHA256 = "5D5973A2E119167EF3C2EF18E8929E8AD9735AD65A63B639179256547575B6E9"
EXPECTED_TYPES = ("route-fault", "mcu-halt")


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def main() -> int:
    if len(sys.argv) != 2:
        raise SystemExit("usage: validate-controller-fault-campaign.py <campaign-directory>")
    campaign = Path(sys.argv[1]).resolve()
    manifest = json.loads((campaign / "sequence-manifest.json").read_text(encoding="utf-8-sig"))
    if len(manifest) < 8:
        raise ValueError("campaign must contain at least four whole sequences for each fault type")
    by_type: dict[str, list[dict]] = {kind: [] for kind in EXPECTED_TYPES}
    summaries: list[dict] = []
    sequence_ids: set[str] = set()
    trace_paths: set[str] = set()
    for row in manifest:
        if row["sequence_id"] in sequence_ids or row["trace"] in trace_paths:
            raise ValueError("sequence IDs and trace paths must be unique across the campaign")
        sequence_ids.add(row["sequence_id"])
        trace_paths.add(row["trace"])
        if row["split"] not in ("training", "heldout"):
            raise ValueError(f"{row['sequence_id']}: invalid whole-sequence split")
        if row["fault_type"] not in by_type:
            raise ValueError(f"unexpected fault type: {row['fault_type']}")
        by_type[row["fault_type"]].append(row)
        if row["firmware_sha256"].upper() != FIRMWARE_SHA256:
            raise ValueError(f"{row['sequence_id']}: firmware hash does not match the pinned STM32 ELF")
        trace = campaign / row["trace"]
        godot_log = campaign / row["godot_log"]
        controller_log = campaign / row["controller_log"]
        for path, key in ((trace, "trace_sha256"), (godot_log, "godot_log_sha256"), (controller_log, "controller_log_sha256")):
            if sha256(path) != row[key].upper():
                raise ValueError(f"{row['sequence_id']}: hash mismatch for {path.name}")
        text = godot_log.read_text(encoding="utf-8-sig", errors="replace")
        command_text = controller_log.read_text(encoding="utf-8-sig", errors="replace")
        commands = row["motor_commands"]
        if len(commands) < 2 or commands[-1] != "50,50" or f"Motor 50,50 reply: OK MOTOR" not in command_text:
            raise ValueError(f"{row['sequence_id']}: valid MOTOR command was not acknowledged")
        for command in commands:
            left, right = command.split(",")
            if f"Motor {left},{right} reply: OK MOTOR" not in command_text:
                raise ValueError(f"{row['sequence_id']}: training/held-out command {command} lacks its exact firmware acknowledgment")
        records = [json.loads(line) for line in trace.read_text(encoding="utf-8-sig").splitlines() if line.strip()]
        if not records or [item["Sequence"] for item in records] != list(range(1, len(records) + 1)):
            raise ValueError(f"{row['sequence_id']}: trace sequence is empty or noncontiguous")
        if any(item.get("SchemaVersion") != 1 for item in records):
            raise ValueError(f"{row['sequence_id']}: unknown trace schema")
        injected = [item for item in records if item.get("RecordType") == "FaultObservation" and item.get("Phase") == "injected"]
        observed = [item for item in records if item.get("RecordType") == "FaultObservation" and item.get("Phase") == "observed"]
        if len(injected) != 1:
            raise ValueError(f"{row['sequence_id']}: expected exactly one simulation-boundary fault injection")
        fault = injected[0]
        if fault["SimTimeNs"] < row["requested_fault_sim_time_ns"]:
            raise ValueError(f"{row['sequence_id']}: fault occurred before its requested simulation boundary")
        if fault["SimTimeNs"] - row["requested_fault_sim_time_ns"] > 8_333_334:
            raise ValueError(f"{row['sequence_id']}: injected fault missed its requested simulation boundary by more than one 120 Hz tick")
        preceding_pwm = [item for item in records if item.get("RecordType") == "PwmObservation" and item["SimTimeNs"] <= fault["SimTimeNs"]]
        if not preceding_pwm or not any(abs(item["Channel1SignedDuty"]) > 0 or abs(item["Channel2SignedDuty"]) > 0 for item in preceding_pwm):
            raise ValueError(f"{row['sequence_id']}: fault was not preceded by a measured active PWM output")
        if row["fault_type"] == "route-fault":
            if fault["FaultCode"] != "PWM_GPIO_AF_MISMATCH_PA6_PA7" or len(observed) != 1:
                raise ValueError(f"{row['sequence_id']}: route fault lacks the matching observed peripheral failure")
            if observed[0]["SimTimeNs"] < fault["SimTimeNs"] or "ACTUATION_FAULT reason=PWM_GPIO_AF_MISMATCH_PA6_PA7" not in text:
                raise ValueError(f"{row['sequence_id']}: route diagnostic lacks causal simulation time / safe-stop evidence")
            if observed[0]["SimTimeNs"] - fault["SimTimeNs"] > 8_333_334:
                raise ValueError(f"{row['sequence_id']}: route fault was not observed within one 120 Hz physics tick")
            if "signed_duty=0.000" not in text:
                raise ValueError(f"{row['sequence_id']}: Godot did not confirm safe zero-duty response")
            summaries.append({"sequence_id": row["sequence_id"], "split": row["split"], "fault_type": row["fault_type"],
                              "command_count": len(commands), "requested_fault_sim_time_ns": row["requested_fault_sim_time_ns"],
                              "injected_fault_sim_time_ns": fault["SimTimeNs"], "observed_fault_sim_time_ns": observed[0]["SimTimeNs"],
                              "route_fault_observation_delay_ns": observed[0]["SimTimeNs"] - fault["SimTimeNs"],
                              "pre_fault_pwm": [preceding_pwm[-1]["Channel1SignedDuty"], preceding_pwm[-1]["Channel2SignedDuty"]],
                              "godot_safe_stop_reported": True})
        else:
            if fault["FaultCode"] != "MCU_HALTED" or row["post_halt_ping_no_reply"] is not True:
                raise ValueError(f"{row['sequence_id']}: MCU halt or no-reply UART check failed")
            latched = [item for item in records if item.get("RecordType") == "PwmObservation" and item["SimTimeNs"] >= fault["SimTimeNs"]]
            if not latched or not any(abs(item["Channel1SignedDuty"]) > 0 and abs(item["Channel2SignedDuty"]) > 0 for item in latched):
                raise ValueError(f"{row['sequence_id']}: halted MCU did not retain measured active PWM outputs")
            prior = [item for item in preceding_pwm if item["SimTimeNs"] < fault["SimTimeNs"]]
            if not prior:
                raise ValueError(f"{row['sequence_id']}: no pre-halt PWM baseline exists")
            expected = prior[-1]
            if any(item["Channel1SignedDuty"] != expected["Channel1SignedDuty"]
                   or item["Channel2SignedDuty"] != expected["Channel2SignedDuty"] for item in latched):
                raise ValueError(f"{row['sequence_id']}: latched PWM changed after the MCU halt")
            trajectory_path = campaign / row["godot_trajectory"]
            if sha256(trajectory_path) != row["godot_trajectory_sha256"].upper():
                raise ValueError(f"{row['sequence_id']}: Godot actuation trajectory hash mismatch")
            with trajectory_path.open(newline="", encoding="utf-8-sig") as trajectory_file:
                trajectory = list(csv.DictReader(trajectory_file))
            post_halt_wheels = [item for item in trajectory if int(item["sim_time_ns"]) >= fault["SimTimeNs"]]
            if len(post_halt_wheels) < 10 or any(float(item["left_signed_duty"]) != 0.5
                                                or float(item["right_signed_duty"]) != 0.5
                                                or abs(float(item["left_wheel_omega_x_radps"])) < 10
                                                or abs(float(item["right_wheel_omega_x_radps"])) < 10
                                                for item in post_halt_wheels):
                raise ValueError(f"{row['sequence_id']}: Godot wheel response did not retain post-halt 50% drive")
            if f"TEST_MCU_HALTED controller_id=mcu-drive component_id=mcu-board sim_time_ns={fault['SimTimeNs']}" not in text:
                raise ValueError(f"{row['sequence_id']}: Godot log does not report the exact injected halt boundary")
            for diagnostic in ("DIAGNOSTIC_ROW id=mcu-board status=FAILED detail=Renode CPU halted",
                               "DIAGNOSTIC_ROW id=uart status=INACTIVE",
                               "DIAGNOSTIC_ROW id=motor-left status=DEGRADED detail=PWM latched after its MCU halt",
                               "DIAGNOSTIC_ROW id=motor-right status=DEGRADED detail=PWM latched after its MCU halt"):
                if diagnostic not in text:
                    raise ValueError(f"{row['sequence_id']}: hierarchical halt diagnostic is missing {diagnostic}")
            summaries.append({"sequence_id": row["sequence_id"], "split": row["split"], "fault_type": row["fault_type"],
                              "command_count": len(commands), "requested_fault_sim_time_ns": row["requested_fault_sim_time_ns"],
                              "injected_fault_sim_time_ns": fault["SimTimeNs"], "post_halt_pwm_observations": len(latched),
                              "latched_pwm": [expected["Channel1SignedDuty"], expected["Channel2SignedDuty"]],
                              "godot_post_halt_wheel_observations": len(post_halt_wheels),
                              "godot_post_halt_wheel_speed_min_rad_s": min(min(abs(float(item["left_wheel_omega_x_radps"])), abs(float(item["right_wheel_omega_x_radps"]))) for item in post_halt_wheels),
                              "post_halt_ping_no_reply": True})
    for kind in EXPECTED_TYPES:
        rows = by_type[kind]
        if len(rows) < 4 or sum(row["split"] == "training" for row in rows) < 2 or sum(row["split"] == "heldout" for row in rows) < 2:
            raise ValueError(f"{kind}: requires two training and two whole held-out sequences")
        if len({row["seed"] for row in rows}) != len(rows) or len({tuple(row["motor_commands"]) for row in rows}) != len(rows):
            raise ValueError(f"{kind}: each whole sequence must have a unique seed and motor-history stimulus")
    result = {"status": "PASS_MEASURED_REFERENCE_ONLY", "model_qualification": "UNQUALIFIED",
              "firmware_sha256": FIRMWARE_SHA256, "sequence_count": len(manifest),
              "fault_types": {key: len(value) for key, value in by_type.items()},
              "heldout_sequences_per_fault": {key: sum(row["split"] == "heldout" for row in value) for key, value in by_type.items()},
              "sequences": summaries,
              "limits": ["reference firmware observations do not qualify the deterministic model",
                         "route fault was injected by test harness at a simulation boundary; exact spontaneous fault probability is not modeled",
                         "MCU halt and GPIO route mismatch are explicit fault cases; unseen faults, reset recovery and arbitrary peripheral corruption remain unsupported"]}
    (campaign / "campaign-validation.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({key: value for key, value in result.items() if key != "sequences" and key != "limits"}, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
