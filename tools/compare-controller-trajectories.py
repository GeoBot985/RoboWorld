#!/usr/bin/env python3
"""Compare exact-reference and candidate-model actuator replays in Godot physics."""

import argparse
import csv
import hashlib
import json
import math
import re
from pathlib import Path


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def load_csv(path: Path, key: str) -> dict[int, dict[str, str]]:
    with path.open(newline="", encoding="utf-8-sig") as stream:
        rows = list(csv.DictReader(stream))
    result: dict[int, dict[str, str]] = {}
    for row in rows:
        time_ns = int(row[key])
        if time_ns in result:
            raise ValueError(f"{path}: duplicate timestamp {time_ns}")
        result[time_ns] = row
    return result


def percentile(values: list[float], quantile: float) -> float:
    ordered = sorted(values)
    position = (len(ordered) - 1) * quantile
    lower = math.floor(position)
    upper = math.ceil(position)
    return ordered[lower] * (upper - position) + ordered[upper] * (position - lower)


def trajectory_comparison(directory: Path) -> dict:
    repository = Path(__file__).resolve().parents[1]
    exact_manifest = json.loads((directory / "exact-actuation-replay-manifest.json").read_text(encoding="utf-8"))
    model_manifest = json.loads((directory / "model-actuation-replay-manifest.json").read_text(encoding="utf-8"))
    for manifest in (exact_manifest, model_manifest):
        if manifest["status"] != "CHARACTERIZATION_ONLY_UNQUALIFIED_CANDIDATE":
            raise ValueError(f"{directory}: replay manifest has unexpected status")
        if manifest["candidateFidelityLabel"] != "UNQUALIFIED" or manifest["productionProfilePreflightChanged"]:
            raise ValueError(f"{directory}: characterization replay must preserve production preflight")
    for key in ("sourceTraceSha256", "sourceSequenceManifestSha256", "sourceSequenceId", "candidateProfileId", "candidateProfileFileSha256", "candidateModelFileSha256", "candidateModelSha256", "sequenceStartSimTimeNs", "scheduledUartInputs"):
        if exact_manifest[key] != model_manifest[key]:
            raise ValueError(f"{directory}: exact/model replay manifests disagree on {key}")
    if exact_manifest["sourceSequenceSplit"] != "heldout":
        raise ValueError(f"{directory}: source sequence is not a whole held-out sequence")

    for manifest, file_key, hash_key in (
        (model_manifest, "candidateProfileFile", "candidateProfileFileSha256"),
        (model_manifest, "candidateModelFile", "candidateModelFileSha256"),
        (model_manifest, "sourceTraceFile", "sourceTraceSha256"),
        (model_manifest, "sourceSequenceManifestFile", "sourceSequenceManifestSha256"),
    ):
        source = (repository / manifest[file_key]).resolve()
        if not source.is_file() or sha256(source) != manifest[hash_key]:
            raise ValueError(f"{directory}: source artifact hash mismatch: {file_key}")
    for manifest, filename, hash_key in (
        (exact_manifest, "exact-actuation.csv", "exactActuatorTraceSha256"),
        (model_manifest, "model-actuation.csv", "modelActuatorTraceSha256"),
    ):
        if sha256(directory / filename) != manifest[hash_key]:
            raise ValueError(f"{directory}: actuator replay trace hash mismatch: {filename}")
    sequence_manifest = json.loads((repository / model_manifest["sourceSequenceManifestFile"]).read_text(encoding="utf-8"))
    source_trace_name = Path(model_manifest["sourceTraceFile"]).name
    source_row = next((row for row in sequence_manifest if row["sequence_id"] == model_manifest["sourceSequenceId"]), None)
    if source_row is None or source_row["split"] != "heldout" or source_row["trace"] != source_trace_name or source_row["trace_sha256"] != model_manifest["sourceTraceSha256"]:
        raise ValueError(f"{directory}: C01 sequence manifest does not authenticate the held-out replay trace")

    exact_path = directory / "godot-exact-trajectory.csv"
    model_path = directory / "godot-model-trajectory.csv"
    exact_log_path = directory / "godot-exact.log"
    model_log_path = directory / "godot-model.log"
    exact_log = exact_log_path.read_text(encoding="utf-8", errors="replace")
    model_log = model_log_path.read_text(encoding="utf-8", errors="replace")
    for mode, log in (("exact", exact_log), ("model", model_log)):
        if "ACTUATION_REPLAY complete" not in log or "worker=disabled" not in log or "ROBOT_SERVICE_ENDPOINT READY" in log:
            raise ValueError(f"{directory}: Godot {mode} run was not a completed offline replay")
    end_times = []
    epochs = []
    for log in (exact_log, model_log):
        mode_match = re.search(r"ACTUATION_REPLAY mode=offline .*?epoch_ns=(\d+) end_ns=(\d+)", log)
        complete_match = re.search(r"ACTUATION_REPLAY complete .*?sim_time_ns=(\d+)", log)
        if mode_match is None or complete_match is None:
            raise ValueError(f"{directory}: Godot replay log is missing its epoch or completion timestamp")
        epochs.append(int(mode_match.group(1)))
        end_times.append((int(mode_match.group(2)), int(complete_match.group(1))))
    if epochs[0] != epochs[1] or end_times[0][0] != end_times[1][0]:
        raise ValueError(f"{directory}: exact/model Godot replays used different epochs or requested end horizons")
    # Godot may observe the requested stop at adjacent frame/physics boundaries
    # because the loaded physics worker and authoritative simulation clock are
    # independently paced. Require identical sampled physics timestamps below;
    # tolerate at most one 120 Hz tick in the final clock observation.
    if abs(end_times[0][1] - end_times[1][1]) > 8_333_334:
        raise ValueError(f"{directory}: exact/model observed end times differ by more than one 120 Hz physics tick")
    exact = load_csv(exact_path, "sim_time_ns")
    model = load_csv(model_path, "sim_time_ns")
    if exact.keys() != model.keys():
        raise ValueError(f"{directory}: exact/model Godot physics trajectories do not share an identical simulation-time horizon")
    times = sorted(exact.keys() & model.keys())
    if len(times) < 20:
        raise ValueError(f"{directory}: fewer than 20 aligned Godot physics samples")

    start_exact, end_exact = exact[times[0]], exact[times[-1]]
    start_model, end_model = model[times[0]], model[times[-1]]

    def displacement(start: dict[str, str], end: dict[str, str]) -> float:
        dx = float(end["chassis_x_m"]) - float(start["chassis_x_m"])
        dz = float(end["chassis_z_m"]) - float(start["chassis_z_m"])
        return math.hypot(dx, dz)

    exact_displacement = displacement(start_exact, end_exact)
    model_displacement = displacement(start_model, end_model)
    allowed_difference = max(0.02, 0.05 * exact_displacement)
    position_errors = []
    left_wheel_errors = []
    right_wheel_errors = []
    for time_ns in times:
        exact_row, model_row = exact[time_ns], model[time_ns]
        position_errors.append(math.hypot(
            float(exact_row["chassis_x_m"]) - float(model_row["chassis_x_m"]),
            float(exact_row["chassis_z_m"]) - float(model_row["chassis_z_m"]),
        ))
        left_wheel_errors.append(abs(float(exact_row["left_wheel_omega_x_radps"]) - float(model_row["left_wheel_omega_x_radps"])))
        right_wheel_errors.append(abs(float(exact_row["right_wheel_omega_x_radps"]) - float(model_row["right_wheel_omega_x_radps"])))

    displacement_error = abs(exact_displacement - model_displacement)
    position_limit = max(0.02, 0.05 * exact_displacement)
    passed = displacement_error <= allowed_difference and max(position_errors) <= position_limit
    return {
        "sequence_id": directory.name,
        "heldout_trace_sha256": exact_manifest["sourceTraceSha256"],
        "source_sequence_id": exact_manifest["sourceSequenceId"],
        "source_sequence_split": exact_manifest["sourceSequenceSplit"],
        "source_sequence_manifest_sha256": exact_manifest["sourceSequenceManifestSha256"],
        "candidate_profile_id": model_manifest["candidateProfileId"],
        "candidate_profile_file_sha256": model_manifest["candidateProfileFileSha256"],
        "candidate_model_sha256": model_manifest["candidateModelSha256"],
        "candidate_fidelity_label": model_manifest["candidateFidelityLabel"],
        "scheduled_uart_inputs": exact_manifest["scheduledUartInputs"],
        "exact_pwm_observations": exact_manifest["exactPwmObservations"],
        "exact_actuator_state_changes": exact_manifest["exactActuatorStateChanges"],
        "model_actuator_state_changes": model_manifest["modelActuatorStateChanges"],
        "godot_physics": "Godot 4.7.2 headless loaded rover scene, 120 Hz, identical 10 ms simulation epoch",
        "replay_epoch_ns": epochs[0],
        "replay_requested_end_sim_time_ns": end_times[0][0],
        "exact_replay_observed_end_sim_time_ns": end_times[0][1],
        "model_replay_observed_end_sim_time_ns": end_times[1][1],
        "aligned_samples": len(times),
        "exact_samples": len(exact),
        "model_samples": len(model),
        "aligned_start_sim_time_ns": times[0],
        "aligned_end_sim_time_ns": times[-1],
        "exact_net_displacement_m": exact_displacement,
        "model_net_displacement_m": model_displacement,
        "net_displacement_difference_m": displacement_error,
        "allowed_displacement_difference_m": allowed_difference,
        "chassis_position_error_p95_m": percentile(position_errors, 0.95),
        "chassis_position_error_max_m": max(position_errors),
        "left_wheel_omega_error_p95_radps": percentile(left_wheel_errors, 0.95),
        "left_wheel_omega_error_max_radps": max(left_wheel_errors),
        "right_wheel_omega_error_p95_radps": percentile(right_wheel_errors, 0.95),
        "right_wheel_omega_error_max_radps": max(right_wheel_errors),
        "trajectory_gate": "PASS" if passed else "FAIL",
        "exact_trajectory_sha256": sha256(exact_path),
        "model_trajectory_sha256": sha256(model_path),
        "exact_godot_log_sha256": sha256(exact_log_path),
        "model_godot_log_sha256": sha256(model_log_path),
        "exact_actuator_trace_sha256": exact_manifest["exactActuatorTraceSha256"],
        "model_actuator_trace_sha256": model_manifest["modelActuatorTraceSha256"],
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("replay_directories", nargs="+", type=Path)
    args = parser.parse_args()
    sequences = [trajectory_comparison(path) for path in args.replay_directories]
    all_pass = all(item["trajectory_gate"] == "PASS" for item in sequences)
    build_log = (args.output.parent / "loaded-trajectory-replay-solution-build.log").resolve()
    if not build_log.is_file():
        raise ValueError("Missing full Release solution build log for the replay implementation")
    build_text = build_log.read_text(encoding="utf-8", errors="replace")
    if "Build succeeded." not in build_text or "0 Warning(s)" not in build_text or "0 Error(s)" not in build_text:
        raise ValueError("Full Release solution build did not record zero warnings and errors")
    report = {
        "status": "CHARACTERIZATION_ONLY_PARTIAL_TRAJECTORY_PASS" if all_pass else "CHARACTERIZATION_ONLY_TRAJECTORY_GATE_FAILED",
        "candidate_fidelity_label": "UNQUALIFIED",
        "production_profile_preflight_changed": False,
        "scope": "Two whole held-out motor command sequences replayed through the bounded deterministic backend and identical live Godot loaded-rover physics; sensor feedback and closed-loop trajectory behavior are not claimed.",
        "joint_measurements": "Not applicable to the rover fixture; it contains no articulated robot joints.",
        "release_solution_build": {
            "log": str(build_log.relative_to(Path(__file__).resolve().parents[1])).replace("\\", "/"),
            "status": "PASS: Release build with zero warnings and errors",
            "sha256": sha256(build_log),
        },
        "sequences": sequences,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))
    return 0 if all_pass else 1


if __name__ == "__main__":
    raise SystemExit(main())
