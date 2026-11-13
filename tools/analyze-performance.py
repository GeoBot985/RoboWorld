#!/usr/bin/env python3
"""Summarize one or more raw RoboWorld performance benchmark directories."""

from __future__ import annotations

import argparse
import csv
from collections import Counter
import json
import math
import re
import statistics
from pathlib import Path
from typing import Iterable


def read_csv(path: Path) -> list[dict[str, str]]:
    if not path.exists():
        return []
    with path.open(newline="", encoding="utf-8-sig") as stream:
        return list(csv.DictReader(stream))


def percentile(values: Iterable[float], p: float) -> float | None:
    ordered = sorted(float(v) for v in values if math.isfinite(float(v)))
    if not ordered:
        return None
    if len(ordered) == 1:
        return ordered[0]
    position = (len(ordered) - 1) * p
    lower = math.floor(position)
    upper = math.ceil(position)
    return ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower)


def numeric(rows: list[dict[str, str]], key: str) -> list[float]:
    result: list[float] = []
    for row in rows:
        try:
            result.append(float(row[key].replace(",", ".")))
        except (KeyError, ValueError, TypeError):
            continue
    return result


def max_integer(rows: list[dict[str, str]], key: str) -> int | None:
    values: list[int] = []
    for row in rows:
        try:
            value = row.get(key, "").strip()
            if value and value.lower() != "null":
                values.append(int(value))
        except (ValueError, TypeError):
            continue
    return max(values) if values else None


def stats(values: Iterable[float]) -> dict[str, float | int | None]:
    items = [float(value) for value in values if math.isfinite(float(value))]
    return {
        "n": len(items),
        "mean": statistics.fmean(items) if items else None,
        "p50": percentile(items, 0.50),
        "p95": percentile(items, 0.95),
        "p99": percentile(items, 0.99),
        "max": max(items) if items else None,
    }


def fmt(value: float | int | None, digits: int = 3) -> str:
    return "n/a" if value is None else f"{value:,.{digits}f}"


def nearest_prior(rows: list[dict[str, str]], mono_ns: float, frequency: float) -> int | None:
    candidates = [i for i, row in enumerate(rows) if int(row["mono_ticks"]) * 1e9 / frequency <= mono_ns]
    return candidates[-1] if candidates else None


def command_latencies(commands: list[dict[str, str]], ticks: list[dict[str, str]], frequency: float) -> tuple[list[float], list[float], int]:
    rtts: list[float] = []
    motion_ms: list[float] = []
    for command in commands:
        if command.get("event") != "motor" or command.get("reply") != "OK MOTOR":
            continue
        try:
            sent_ns = int(command["mono_ns"])
            rtts.append(int(command["rtt_ns"]) / 1e6)
            match = re.fullmatch(r"MOTOR\s+(-?\d+)\s+(-?\d+)", command.get("command", ""))
            if not match:
                continue
            direction = 1 if (int(match.group(1)) + int(match.group(2))) > 0 else -1 if (int(match.group(1)) + int(match.group(2))) < 0 else 0
            if direction == 0 or not ticks:
                continue
            start = nearest_prior(ticks, sent_ns, frequency)
            if start is None:
                continue
            base_z = float(ticks[start]["chassis_z_m"])
            for row in ticks[start + 1 :]:
                displacement = direction * (float(row["chassis_z_m"]) - base_z)
                if displacement >= 0.001:
                    motion_ms.append((int(row["mono_ticks"]) * 1e9 / frequency - sent_ns) / 1e6)
                    break
        except (KeyError, ValueError, TypeError):
            continue
    return rtts, motion_ms, len([row for row in commands if row.get("event") == "motor" and row.get("reply") == "OK MOTOR"])


def analyze(run_dir: Path) -> tuple[dict, str]:
    metadata = json.loads((run_dir / "run.meta.json").read_text(encoding="utf-8-sig"))
    phase = metadata.get("performance_phase") or {}
    if not phase.get("Rtf"):
        trace_text = (run_dir / "godot-stdout.log").read_text(encoding="utf-8-sig", errors="replace")
        marker = re.search(r"(?m)^PERF_PHASE_END .*?mono_ticks=(\d+).*?wall_elapsed_ns=(\d+).*?committed_sim_ns=(\d+).*?rtf=([0-9.]+)", trace_text)
        if marker:
            phase = {"MonoTicks": int(marker.group(1)), "WallElapsedNs": int(marker.group(2)),
                     "CommittedSimNs": int(marker.group(3)), "Rtf": float(marker.group(4))}
            metadata["performance_phase"] = phase
            metadata["phase_recovered_from_raw_stdout"] = True
    godot = read_csv(run_dir / "godot-ticks.csv")
    service = read_csv(run_dir / "robot-service-ticks.csv")
    deterministic_model = read_csv(run_dir / "deterministic-model-ticks.csv")
    frames = read_csv(run_dir / "godot-frames.csv")
    viewport_requests = read_csv(run_dir / "godot-viewport-update-requests.csv")
    physics_callback_trace = read_csv(run_dir / "godot-physics-callbacks.csv")
    processes = read_csv(run_dir / "process-samples.csv")
    threads = read_csv(run_dir / "process-thread-samples.csv")
    commands = read_csv(run_dir / "controller-commands.csv")
    frequency = float(metadata["stopwatch_frequency"])
    rtf = float(phase.get("Rtf", 0))
    frame_times_ms = [v / 1e6 for v in numeric(frames, "frame_interval_ns") if v > 0]
    fps_samples = [1000.0 / v for v in frame_times_ms]
    viewport_request_mono = numeric(viewport_requests, "mono_ticks")
    viewport_request_intervals_ms = [
        (current - previous) * 1000.0 / frequency
        for previous, current in zip(viewport_request_mono, viewport_request_mono[1:])
        if current >= previous
    ]
    viewport_request_fps = [1000.0 / interval for interval in viewport_request_intervals_ms if interval > 0]
    physics_callback_counts = numeric(frames, "physics_callbacks_since_previous_frame")
    physics_callback_distribution = {
        "per_frame": stats(physics_callback_counts),
        "histogram": {str(int(count)): physics_callback_counts.count(count) for count in sorted(set(physics_callback_counts))},
    } if physics_callback_counts else None
    physics_callback_mono = numeric(physics_callback_trace, "mono_ticks")
    physics_callback_intervals_ms = [
        (current - previous) * 1000.0 / frequency
        for previous, current in zip(physics_callback_mono, physics_callback_mono[1:])
        if current >= previous
    ]
    physics_callback_states = Counter(
        f"transport_paused={row.get('transport_paused')};ack_pending={row.get('ack_pending')};physics_step_granted={row.get('physics_step_granted')}"
        for row in physics_callback_trace
    )
    rtts, visible_ms, command_count = command_latencies(commands, godot, frequency)
    stage_keys = {
        "Godot callback": "callback_ns",
        "Godot engine physics monitor": "engine_physics_ms",
        "tick request serialization": "tick_serialization_ns",
        "tick request transport": "tick_transport_ns",
        "Godot acknowledgment wait (end-to-end)": "tick_ack_wait_ns",
        "Godot ACK arrival wait": "ack_arrival_wait_ns",
        "Godot ACK arrival-to-continuation dispatch": "ack_dispatch_delay_ns",
        "Fast worker tick queue wait": "fast_model_queue_wait_ns",
        "Fast worker model pipeline and output routing": "model_pipeline_ns",
        "Fast worker acknowledgment write": "ack_write_ns",
        "sensor raycast": "sensor_raycast_ns",
        "Godot event drain": "event_drain_ns",
        "Godot motor torque": "torque_apply_ns",
        "Godot snapshot capture": "snapshot_ns",
        "Godot diagnostics refresh": "diagnostics_ns",
        "service frame read": "frame_read_ns",
        "protobuf parse": "protobuf_parse_ns",
        "service channel blocked write": "channel_write_blocked_ns",
        "service queue wait": "queue_wait_ns",
        "service scheduler excluding ack write": "scheduler_ns",
        "Renode RunFor wall time": "renode_runfor_ns",
        "Renode monitor queries": "monitor_query_ns",
        "sensor echo injection": "echo_injection_ns",
        "peripheral event extraction/routing": "peripheral_event_ns",
        "periodic clock verification": "clock_verification_ns",
        "acknowledgment serialization": "ack_serialization_ns",
        "acknowledgment transport": "ack_transport_ns",
    }
    stage_table: list[tuple[str, dict]] = []
    for label, key in stage_keys.items():
        if key in {"frame_read_ns", "protobuf_parse_ns", "channel_write_blocked_ns", "queue_wait_ns", "scheduler_ns", "renode_runfor_ns", "monitor_query_ns", "echo_injection_ns", "peripheral_event_ns", "clock_verification_ns", "ack_serialization_ns", "ack_transport_ns"}:
            source = service
        elif key in {"fast_model_queue_wait_ns", "model_pipeline_ns", "ack_write_ns"}:
            source = deterministic_model
        else:
            source = godot
        values = numeric(source, key)
        if key.endswith("_ms"):
            values = [value * 1e6 for value in values]
        if any(value != 0 for value in values):
            stage_table.append((label, stats(values)))
    stage_table.sort(key=lambda item: item[1]["p95"] or 0, reverse=True)

    clock_ok = True
    if service:
        clock_ok = all(int(row["sim_time_ns"]) == int(row["expected_renode_time_ns"]) for row in service)
    sequential_ticks = len(godot) > 1 and all(int(b["tick_id"]) == int(a["tick_id"]) + 1 for a, b in zip(godot, godot[1:]))
    sequential_model_ticks = len(deterministic_model) > 1 and all(
        int(b["tick_id"]) == int(a["tick_id"]) + 1 for a, b in zip(deterministic_model, deterministic_model[1:])
    )
    godot_end_by_tick = {int(row["tick_id"]): int(row["sim_time_ns"]) for row in godot}
    initial_clock_epoch_ns = int(metadata.get("clock_epoch_sim_ns", 0))
    def model_start_matches_godot(row: dict[str, str]) -> bool:
        tick_id = int(row["tick_id"])
        expected_start_ns = initial_clock_epoch_ns if tick_id == 1 else godot_end_by_tick.get(tick_id - 1)
        return expected_start_ns is not None and int(row["sim_time_ns"]) == expected_start_ns

    model_clock_exact = bool(deterministic_model) and all(model_start_matches_godot(row) for row in deterministic_model)
    model_tick_ids = [int(row["tick_id"]) for row in deterministic_model]
    godot_tick_ids = [int(row["tick_id"]) for row in godot]
    service_tick_ids = [int(row["tick_id"]) for row in service]
    model_tick_count_matches_godot = not deterministic_model or model_tick_ids == godot_tick_ids
    service_tick_count_matches_godot = not service or service_tick_ids == godot_tick_ids
    if deterministic_model:
        clock_ok = clock_ok and model_clock_exact
    queue_high = max((int(row.get("queue_high_watermark", 0) or 0) for row in service), default=0)
    godot_queue_high = max((int(row.get("hardware_queue_high_watermark", 0) or 0) for row in godot), default=0)
    managed = [row for row in service if int(row.get("managed_heap_bytes", 0) or 0) > 0]
    process_groups: dict[str, list[dict[str, str]]] = {}
    for row in processes:
        process_groups.setdefault(row.get("process_name", "unknown"), []).append(row)
    process_summary = []
    for name, rows in process_groups.items():
        working_set_max = max_integer(rows, "working_set_bytes")
        private_max = max_integer(rows, "private_bytes")
        threads_max = max_integer(rows, "thread_count")
        process_summary.append({
            "name": name,
            "samples": len(rows),
            "cpu_host_p95": percentile([float(r["cpu_percent_host"].replace(",", ".")) for r in rows if r.get("cpu_percent_host") not in ("", "null")], .95),
            "working_set_max_mb": None if working_set_max is None else working_set_max / 1e6,
            "private_max_mb": None if private_max is None else private_max / 1e6,
            "threads_max": threads_max,
        })
    process_summary.sort(key=lambda row: row["working_set_max_mb"] or 0, reverse=True)
    thread_top: dict[str, float] = {}
    for row in threads:
        name = f"{row.get('process_name')}:{row.get('thread_id')}"
        try:
            thread_top[name] = max(thread_top.get(name, 0), float(row["thread_cpu_total_s"]))
        except (ValueError, KeyError):
            pass
    top_threads = sorted(thread_top.items(), key=lambda item: item[1], reverse=True)[:8]

    trace = (run_dir / "godot-stdout.log").read_text(encoding="utf-8-sig", errors="replace")
    echo_count = len(re.findall(r"DISTANCE_ECHO_RISE", trace))
    sensor_queries = [row for row in commands if row.get("event") == "sensor_query"]
    sensor_valid_distances_mm = []
    for row in sensor_queries:
        match = re.fullmatch(r"DIST\s+mm=(\d+)\s+valid=1", row.get("reply", ""))
        if match:
            sensor_valid_distances_mm.append(int(match.group(1)))
    meta = {
        "run_id": metadata["run_id"], "scenario": metadata["scenario"], "resolution": metadata["resolution"],
        "render_cap": metadata["render_fps_cap"], "duration_configured_s": metadata["duration_seconds"],
        "rtf": rtf, "committed_sim_s": int(phase.get("CommittedSimNs", 0)) / 1e9,
        "active_wall_s": int(phase.get("WallElapsedNs", 0)) / 1e9,
        "startup_wall_s": metadata.get("startup_wall_s"), "godot_ticks": len(godot), "service_ticks": len(service),
        "deterministic_model_ticks": len(deterministic_model), "deterministic_model_tick_ids_contiguous": sequential_model_ticks,
        "deterministic_model_clock_exact": model_clock_exact,
        "deterministic_model_tick_count_matches_godot": model_tick_count_matches_godot,
        "service_tick_count_matches_godot": service_tick_count_matches_godot,
        "frame_samples": len(frames), "fps_median": percentile(fps_samples, .50), "fps_p05": percentile(fps_samples, .05),
        "frame_time_ms": stats(frame_times_ms), "clock_exact": clock_ok, "tick_ids_contiguous": sequential_ticks,
        "viewport_update_request_samples": len(viewport_requests),
        "viewport_update_request_fps_median": percentile(viewport_request_fps, .50),
        "viewport_update_request_fps_p05": percentile(viewport_request_fps, .05),
        "viewport_update_request_interval_ms": stats(viewport_request_intervals_ms),
        "physics_callbacks_per_frame": physics_callback_distribution,
        "physics_callback_trace": {
            "count": len(physics_callback_trace),
            "rate_hz_over_active_window": len(physics_callback_trace) / (int(phase.get("WallElapsedNs", 0)) / 1e9) if int(phase.get("WallElapsedNs", 0)) > 0 else None,
            "inter_callback_interval_ms": stats(physics_callback_intervals_ms),
            "state_counts": dict(physics_callback_states),
        } if physics_callback_trace else None,
        "sensor_echo_rises": echo_count, "controller_sensor_queries": len(sensor_queries),
        "controller_sensor_valid_responses": len(sensor_valid_distances_mm),
        "controller_sensor_distance_mm": stats(sensor_valid_distances_mm), "controller_motor_commands": command_count,
        "controller_rtt_ms": stats(rtts), "command_to_1mm_visible_motion_ms": stats(visible_ms),
        "service_queue_high_watermark": queue_high, "godot_event_queue_high_watermark": godot_queue_high,
        "managed_heap_samples": len(managed), "managed_heap_max_mb": max((int(row["managed_heap_bytes"]) for row in managed), default=0) / 1e6,
        "managed_heap_committed_max_mb": max((int(row["managed_heap_committed_bytes"]) for row in managed), default=0) / 1e6,
        "managed_heap_fragmented_max_mb": max((int(row["managed_heap_fragmented_bytes"]) for row in managed), default=0) / 1e6,
        "gc_counts_max": {f"gen{i}": max((int(row[f"gc_gen{i}_count"]) for row in managed), default=0) for i in range(3)},
        "processes": process_summary, "top_threads_by_cumulative_cpu_s": top_threads,
        "stage_stats_ns": {label: values for label, values in stage_table},
        "metadata": metadata,
    }
    lines = [
        f"### {meta['scenario']} · {meta['resolution']} · cap {meta['render_cap']} FPS",
        "",
        f"- Run: `{meta['run_id']}` ({meta['duration_configured_s']} s configured; active {meta['active_wall_s']:.3f} s; startup {fmt(meta['startup_wall_s'])} s)",
        f"- RTF: **{rtf:.3f}**; committed simulation {meta['committed_sim_s']:.3f} s; samples {len(godot)} Godot ticks / {len(service)} Renode-service ticks / {len(deterministic_model)} deterministic-model ticks.",
        f"- Godot process loop: median {fmt(meta['fps_median'])} FPS, p05 {fmt(meta['fps_p05'])} FPS; loop interval p50/p95/p99 {fmt(meta['frame_time_ms']['p50'])}/{fmt(meta['frame_time_ms']['p95'])}/{fmt(meta['frame_time_ms']['p99'])} ms ({len(frames)} samples).",
        *([f"- Viewport draw requests: {meta['viewport_update_request_samples']} requested updates; median/p05 request cadence {fmt(meta['viewport_update_request_fps_median'])}/{fmt(meta['viewport_update_request_fps_p05'])} Hz; request interval p50/p95 {fmt(meta['viewport_update_request_interval_ms']['p50'])}/{fmt(meta['viewport_update_request_interval_ms']['p95'])} ms. These are render-target requests, not OS-present measurements."] if viewport_requests else []),
        *([f"- Godot fixed callbacks per rendered frame: p50/p95 {fmt(physics_callback_distribution['per_frame']['p50'])}/{fmt(physics_callback_distribution['per_frame']['p95'])}; histogram {physics_callback_distribution['histogram']}."] if physics_callback_distribution else []),
        *([f"- Fixed callback trace: {meta['physics_callback_trace']['count']} callbacks ({fmt(meta['physics_callback_trace']['rate_hz_over_active_window'])} Hz over active window); inter-callback interval p50/p95 {fmt(meta['physics_callback_trace']['inter_callback_interval_ms']['p50'])}/{fmt(meta['physics_callback_trace']['inter_callback_interval_ms']['p95'])} ms; states {meta['physics_callback_trace']['state_counts']}."] if meta['physics_callback_trace'] else []),
        f"- Clock/ticks: exact shared boundary={clock_ok}; contiguous Godot tick IDs={sequential_ticks}; contiguous deterministic-model tick IDs={sequential_model_ticks}; model/Godot interval-start boundaries exact={model_clock_exact if deterministic_model else 'not measured'}; worker/Godot tick IDs aligned={model_tick_count_matches_godot if deterministic_model else service_tick_count_matches_godot if service else 'not measured'}; service/Godot queue high-watermarks {queue_high}/{godot_queue_high}.",
        f"- Firmware pathway: UART motor commands={command_count}; RTT p50/p95/p99 {fmt(meta['controller_rtt_ms']['p50'])}/{fmt(meta['controller_rtt_ms']['p95'])}/{fmt(meta['controller_rtt_ms']['p99'])} ms; command-to-1 mm visible motion p50/p95/p99 {fmt(meta['command_to_1mm_visible_motion_ms']['p50'])}/{fmt(meta['command_to_1mm_visible_motion_ms']['p95'])}/{fmt(meta['command_to_1mm_visible_motion_ms']['p99'])} ms (n={meta['command_to_1mm_visible_motion_ms']['n']}).",
        f"- Sensor telemetry: {meta['controller_sensor_valid_responses']}/{meta['controller_sensor_queries']} SENSOR? queries returned valid DIST telemetry (mm p50/p95/p99 {fmt(meta['controller_sensor_distance_mm']['p50'], 0)}/{fmt(meta['controller_sensor_distance_mm']['p95'], 0)}/{fmt(meta['controller_sensor_distance_mm']['p99'], 0)}); hardware echo-rise trace rows={echo_count}. Managed heap samples={len(managed)} (120-tick cadence), max live/committed/fragmented {meta['managed_heap_max_mb']:.1f}/{meta['managed_heap_committed_max_mb']:.1f}/{meta['managed_heap_fragmented_max_mb']:.1f} MB.",
        "",
        "| Measured stage | Mean ms | p50 ms | p95 ms | p99 ms | n |",
        "|---|---:|---:|---:|---:|---:|",
    ]
    for label, values in stage_table:
        lines.append(f"| {label} | {fmt((values['mean'] or 0)/1e6)} | {fmt((values['p50'] or 0)/1e6)} | {fmt((values['p95'] or 0)/1e6)} | {fmt((values['p99'] or 0)/1e6)} | {values['n']} |")
    lines += ["", "| Process | Samples | CPU p95 (% host) | Max working set MB | Max private MB | Max threads |", "|---|---:|---:|---:|---:|---:|"]
    for process in process_summary:
        lines.append(f"| {process['name']} | {process['samples']} | {fmt(process['cpu_host_p95'])} | {fmt(process['working_set_max_mb'], 1)} | {fmt(process['private_max_mb'], 1)} | {fmt(process['threads_max'], 0)} |")
    if top_threads:
        lines += ["", "Top sampled threads by cumulative CPU seconds (not per-interval CPU):", ""]
        lines.extend(f"- `{name}`: {seconds:.2f} s" for name, seconds in top_threads)
    lines += ["", f"Raw evidence: `{run_dir}`.", ""]
    return meta, "\n".join(lines)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("run_dirs", nargs="+", type=Path)
    parser.add_argument("--output", type=Path, help="Markdown output path; defaults to the first run's baseline-report.md")
    parser.add_argument("--summary-output", type=Path, help="JSON summary path; defaults to analysis-summary.json beside the Markdown report")
    args = parser.parse_args()
    reports = [analyze(path.resolve()) for path in args.run_dirs]
    target = args.output.resolve() if args.output else args.run_dirs[0].resolve() / "baseline-report.md"
    target.write_text("# RoboWorld Performance Baseline\n\n" + "\n".join(report for _, report in reports), encoding="utf-8")
    summary = [{k: v for k, v in data.items() if k not in ("metadata", "stage_stats_ns", "processes", "top_threads_by_cumulative_cpu_s")} for data, _ in reports]
    summary_target = args.summary_output.resolve() if args.summary_output else target.parent / "analysis-summary.json"
    summary_target.parent.mkdir(parents=True, exist_ok=True)
    summary_target.write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print(f"PERFORMANCE_ANALYSIS_COMPLETE report={target} scenarios={len(reports)}")
    for data, _ in reports:
        print(f"scenario={data['scenario']} rtf={data['rtf']:.3f} fps_p50={fmt(data['fps_median'])} motion_p95_ms={fmt(data['command_to_1mm_visible_motion_ms']['p95'])}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
