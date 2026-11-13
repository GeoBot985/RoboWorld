#!/usr/bin/env python3
"""Compare wheel and chassis state from aligned Godot performance traces."""

from __future__ import annotations

import argparse
import csv
import json
import math
from pathlib import Path
import re


def read_csv(path: Path) -> list[dict[str, str]]:
    with path.open(newline="", encoding="utf-8-sig") as stream:
        return list(csv.DictReader(stream))


def state_delta(left: dict[str, str], right: dict[str, str], fields: list[str]) -> float:
    return math.sqrt(sum((float(left[field]) - float(right[field])) ** 2 for field in fields))


def summarize(values: list[float]) -> dict[str, float | int | None]:
    if not values:
        return {"n": 0, "max": None, "rms": None, "final": None}
    return {
        "n": len(values),
        "max": max(values),
        "rms": math.sqrt(sum(value * value for value in values) / len(values)),
        "final": values[-1],
    }


def first_above(rows: list[dict[str, str]], values: list[float], threshold: float) -> int | None:
    return next((int(row["tick_id"]) for row, value in zip(rows, values) if value > threshold), None)


def percentile(values: list[float], fraction: float) -> float | None:
    if not values:
        return None
    ordered = sorted(values)
    position = (len(ordered) - 1) * fraction
    lower = math.floor(position)
    upper = math.ceil(position)
    return ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower)


def command_summary(run_dir: Path) -> list[dict[str, str]]:
    path = run_dir / "controller-commands.csv"
    if not path.exists():
        return []
    return read_csv(path)


def compare(first_dir: Path, second_dir: Path) -> dict[str, object]:
    first_meta = json.loads((first_dir / "run.meta.json").read_text(encoding="utf-8-sig"))
    second_meta = json.loads((second_dir / "run.meta.json").read_text(encoding="utf-8-sig"))
    first = read_csv(first_dir / "godot-ticks.csv")
    second = read_csv(second_dir / "godot-ticks.csv")
    if not first or len(first) != len(second):
        raise ValueError("Runs must contain the same nonempty number of Godot tick rows.")
    if any(a["tick_id"] != b["tick_id"] or a["sim_time_ns"] != b["sim_time_ns"] for a, b in zip(first, second)):
        raise ValueError("Godot traces must have identical tick IDs and simulation-time boundaries.")

    transitions = []
    previous = (first[0]["left_duty"], first[0]["right_duty"])
    for row in first[1:]:
        current = (row["left_duty"], row["right_duty"])
        if current != previous:
            transitions.append({"tick_id": int(row["tick_id"]), "left_duty": float(current[0]), "right_duty": float(current[1])})
            previous = current
    second_transitions = []
    previous = (second[0]["left_duty"], second[0]["right_duty"])
    for row in second[1:]:
        current = (row["left_duty"], row["right_duty"])
        if current != previous:
            second_transitions.append({"tick_id": int(row["tick_id"]), "left_duty": float(current[0]), "right_duty": float(current[1])})
            previous = current
    if transitions != second_transitions:
        raise ValueError("Motor duty transitions differ between runs.")

    first_commands = command_summary(first_dir)
    second_commands = command_summary(second_dir)
    command_key = lambda row: (row["event"], row["sequence"], row["command"], row["target_sim_ns"])
    command_schedules_match = [command_key(row) for row in first_commands] == [command_key(row) for row in second_commands]
    first_non_sensor_responses = [(row["event"], row["sequence"], row["reply"]) for row in first_commands if row["event"] != "sensor_query"]
    second_non_sensor_responses = [(row["event"], row["sequence"], row["reply"]) for row in second_commands if row["event"] != "sensor_query"]
    first_sensors = {row["sequence"]: row for row in first_commands if row["event"] == "sensor_query"}
    second_sensors = {row["sequence"]: row for row in second_commands if row["event"] == "sensor_query"}
    sensor_differences: list[float] = []
    for sequence in sorted(first_sensors.keys() & second_sensors.keys()):
        first_match = re.fullmatch(r"DIST mm=(\d+) valid=1", first_sensors[sequence]["reply"])
        second_match = re.fullmatch(r"DIST mm=(\d+) valid=1", second_sensors[sequence]["reply"])
        if first_match and second_match:
            sensor_differences.append(abs(int(first_match.group(1)) - int(second_match.group(1))))

    position_fields = [f"{side}_wheel_{axis}_m" for side in ("left", "right") for axis in "xyz"]
    angular_fields = [f"{side}_wheel_angular_{axis}_radps" for side in ("left", "right") for axis in "xyz"]
    chassis_fields = ["chassis_x_m", "chassis_z_m"]
    wheel_position = [state_delta(a, b, position_fields) for a, b in zip(first, second)]
    wheel_angular = [state_delta(a, b, angular_fields) for a, b in zip(first, second)]
    chassis = [state_delta(a, b, chassis_fields) for a, b in zip(first, second)]

    start_tick = next((item["tick_id"] for item in transitions if item["left_duty"] or item["right_duty"]), None)
    stop_tick = next((item["tick_id"] for item in transitions if item["tick_id"] > (start_tick or 0) and not item["left_duty"] and not item["right_duty"]), None)
    windows: dict[str, tuple[int, int]] = {}
    if start_tick is not None and stop_tick is not None:
        windows = {
            "pre_actuation": (1, start_tick - 1),
            "actuated": (start_tick, stop_tick - 1),
            "after_stop": (stop_tick, int(first[-1]["tick_id"])),
        }

    phase_summaries: dict[str, object] = {}
    for label, (start, end) in windows.items():
        indexes = [index for index, row in enumerate(first) if start <= int(row["tick_id"]) <= end]
        phase_summaries[label] = {
            "tick_ids": [start, end],
            "wheel_position_delta_m": summarize([wheel_position[index] for index in indexes]),
            "wheel_angular_velocity_delta_radps": summarize([wheel_angular[index] for index in indexes]),
            "chassis_xz_delta_m": summarize([chassis[index] for index in indexes]),
        }
    transition_intervals: list[dict[str, object]] = []
    for index, transition in enumerate(transitions):
        start = transition["tick_id"]
        end = transitions[index + 1]["tick_id"] - 1 if index + 1 < len(transitions) else int(first[-1]["tick_id"])
        indexes = [row_index for row_index, row in enumerate(first) if start <= int(row["tick_id"]) <= end]
        transition_intervals.append({
            "tick_ids": [start, end],
            "signed_duty": [transition["left_duty"], transition["right_duty"]],
            "wheel_position_delta_m": summarize([wheel_position[row_index] for row_index in indexes]),
            "wheel_angular_velocity_delta_radps": summarize([wheel_angular[row_index] for row_index in indexes]),
            "chassis_xz_delta_m": summarize([chassis[row_index] for row_index in indexes]),
        })

    ids = [int(row["tick_id"]) for row in first]
    return {
        "status": "ALIGNED_TRACE_COMPARISON",
        "runs": [first_meta["run_id"], second_meta["run_id"]],
        "controller_modes": [first_meta.get("controller_mode"), second_meta.get("controller_mode")],
        "same_package_manifest_sha256": first_meta.get("package_manifest_sha256") == second_meta.get("package_manifest_sha256"),
        "package_manifest_sha256": [first_meta.get("package_manifest_sha256"), second_meta.get("package_manifest_sha256")],
        "godot_tick_count": len(ids),
        "tick_ids_contiguous": ids == list(range(ids[0], ids[-1] + 1)),
        "motor_duty_transitions": transitions,
        "controller_command_schedules_match": command_schedules_match,
        "non_sensor_controller_responses_match": first_non_sensor_responses == second_non_sensor_responses,
        "controller_command_counts": [len(first_commands), len(second_commands)],
        "sensor_reply_delta_mm": {
            "matched_sequences": len(sensor_differences),
            "p95": percentile(sensor_differences, 0.95),
            "max": max(sensor_differences) if sensor_differences else None,
        },
        "first_nonzero_duty_tick_id": start_tick,
        "first_zero_duty_after_motion_tick_id": stop_tick,
        "overall": {
            "wheel_position_delta_m": summarize(wheel_position),
            "wheel_angular_velocity_delta_radps": summarize(wheel_angular),
            "chassis_xz_delta_m": summarize(chassis),
            "first_chassis_tick_above_0_1mm": first_above(first, chassis, 0.0001),
            "first_wheel_position_tick_above_0_1mm": first_above(first, wheel_position, 0.0001),
            "first_wheel_angular_velocity_tick_above_0_01radps": first_above(first, wheel_angular, 0.01),
        },
        "by_phase": phase_summaries,
        "by_motor_interval": transition_intervals,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("first_run", type=Path)
    parser.add_argument("second_run", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = compare(args.first_run.resolve(), args.second_run.resolve())
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(f"WHEEL_STATE_COMPARISON_COMPLETE output={args.output.resolve()} status={result['status']} ticks={result['godot_tick_count']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
