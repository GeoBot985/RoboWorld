#!/usr/bin/env python3
"""Deterministic performance workload for real firmware UART endpoints."""

from __future__ import annotations

import argparse
import csv
import socket
import time
from pathlib import Path


def read_line(sock: socket.socket, timeout_s: float = 5.0) -> str:
    sock.settimeout(timeout_s)
    data = bytearray()
    while True:
        chunk = sock.recv(1)
        if not chunk:
            raise ConnectionError("UART endpoint closed while waiting for a firmware line")
        if chunk == b"\n":
            return data.decode("ascii", errors="replace").rstrip("\r")
        data.extend(chunk)
        if len(data) > 512:
            raise ValueError("UART line exceeded the controller receive limit")


def read_latest_sim_time(path: Path) -> int | None:
    """Read the latest complete committed tick without rescanning the growing trace."""
    with path.open("rb") as trace:
        trace.seek(0, 2)
        end = trace.tell()
        if end == 0:
            return None
        start = max(0, end - 2048)
        while True:
            trace.seek(start)
            data = trace.read(end - start)
            lines = data.splitlines()
            # Ignore a row still being appended; the previous complete record is
            # sufficient until the writer flushes the next authoritative tick.
            candidate = lines[-1] if data.endswith(b"\n") else (lines[-2] if len(lines) > 1 else b"")
            columns = candidate.split(b",", 3)
            if len(columns) > 1 and columns[1].isdigit():
                return int(columns[1])
            if start == 0:
                return None
            end = start
            start = max(0, end - 2048)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--uart", action="append", required=True, help="controller-id=port; repeat for each controller")
    parser.add_argument("--run-seconds", type=float, default=0)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--cycle-seconds", type=float, default=3.0)
    parser.add_argument("--response-timeout-seconds", type=float, default=8.0,
                        help="wall timeout for command replies from exact firmware under heavy load")
    parser.add_argument("--expected-boot", action="append", default=[],
                        help="controller-id=firmware build id from the package build metadata")
    parser.add_argument("--motor-command", action="append", default=None,
                        help="repeat this validated MOTOR left right command sequence instead of the built-in four-phase pattern")
    parser.add_argument("--sensor-query-every", type=int, default=0,
                        help="issue SENSOR? after every Nth motor command; 0 disables queries")
    parser.add_argument("--sim-duration-seconds", type=int, default=0,
                        help="end on a fixed simulation-time horizon instead of wall time")
    parser.add_argument("--simulation-ticks-csv", type=Path)
    parser.add_argument("--sim-phase-start-ns", type=int)
    parser.add_argument("--sim-command-epoch-ns", type=int,
                        help="absolute shared simulation timestamp for sequence command zero")
    args = parser.parse_args()
    if (args.run_seconds <= 0 and args.sim_duration_seconds <= 0) or args.cycle_seconds <= 0 or args.sensor_query_every < 0 or args.response_timeout_seconds < 8:
        raise ValueError("run/cycle durations must be positive and response timeout must be at least 8 s")
    if args.motor_command is not None:
        if not args.motor_command:
            raise ValueError("at least one --motor-command is required when the option is present")
        for command in args.motor_command:
            parts = command.split()
            if len(parts) != 3 or parts[0] != "MOTOR":
                raise ValueError(f"invalid motor command pattern item: {command!r}")
            try:
                left, right = int(parts[1]), int(parts[2])
            except ValueError as error:
                raise ValueError(f"invalid motor command pattern item: {command!r}") from error
            if not -100 <= left <= 100 or not -100 <= right <= 100:
                raise ValueError(f"motor command pattern is outside firmware range: {command!r}")
    if args.sim_duration_seconds:
        if args.sim_duration_seconds <= 3 or args.simulation_ticks_csv is None or args.sim_phase_start_ns is None:
            raise ValueError("simulation-time control requires a horizon over 3 seconds, the tick trace and phase start")

    controllers: list[tuple[str, socket.socket]] = []
    expected_boot_ids: dict[str, str] = {}
    for item in args.expected_boot:
        controller_id, build_id = item.rsplit("=", 1)
        if not controller_id or len(build_id) != 8 or any(c not in "0123456789abcdef" for c in build_id.lower()):
            raise ValueError(f"invalid expected firmware boot identity: {item!r}")
        if controller_id in expected_boot_ids:
            raise ValueError(f"duplicate expected firmware boot identity for {controller_id}")
        expected_boot_ids[controller_id] = build_id.lower()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("w", newline="", encoding="utf-8") as csv_file:
        writer = csv.writer(csv_file)
        writer.writerow(["event", "sequence", "controller_id", "mono_ns", "command", "reply", "rtt_ns", "target_sim_ns"])
        try:
            for item in args.uart:
                controller_id, port_text = item.rsplit("=", 1)
                sock = socket.create_connection((args.host, int(port_text)), timeout=8)
                sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
                controllers.append((controller_id, sock))

            for controller_id, sock in controllers:
                started = time.perf_counter_ns()
                boot = read_line(sock, 10)
                writer.writerow(["boot", "", controller_id, started, "", boot, "", ""])
                if not boot.startswith("BOOT "):
                    raise RuntimeError(f"{controller_id}: expected genuine firmware BOOT, received {boot!r}")
                expected_boot = expected_boot_ids.get(controller_id)
                if expected_boot is not None and boot != f"BOOT {expected_boot}":
                    raise RuntimeError(f"{controller_id}: firmware identity mismatch; expected BOOT {expected_boot}, received {boot!r}")
                sock.sendall(b"PING\n")
                reply = read_line(sock, 8)
                writer.writerow(["ping", "", controller_id, time.perf_counter_ns(), "PING", reply, "", ""])
                if not reply.startswith("PONG "):
                    raise RuntimeError(f"{controller_id}: expected genuine firmware PONG, received {reply!r}")

            started_at = time.monotonic()
            next_command_at = started_at
            simulation_end_ns = (args.sim_phase_start_ns + args.sim_duration_seconds * 1_000_000_000
                                 if args.sim_duration_seconds else None)
            command_sequence = 0
            while True:
                now = time.monotonic()
                command_at_sim_ns = None
                if simulation_end_ns is not None:
                    try:
                        sim_now_ns = read_latest_sim_time(args.simulation_ticks_csv)
                    except OSError:
                        sim_now_ns = None
                    if sim_now_ns is None:
                        time.sleep(0.001)
                        continue
                    if sim_now_ns >= simulation_end_ns - 1_000_000_000:
                        break
                    # Leave time for the external process to connect and complete
                    # firmware BOOT/PING before the first scheduled command. The
                    # phase timer begins before the runner has finished launching
                    # this process, so a 1 s offset can already be in the past.
                    command_epoch_ns = (args.sim_command_epoch_ns if args.sim_command_epoch_ns is not None
                                        else args.sim_phase_start_ns + 5_000_000_000)
                    command_at_sim_ns = command_epoch_ns + command_sequence * int(args.cycle_seconds) * 1_000_000_000
                    if sim_now_ns < command_at_sim_ns:
                        time.sleep(0.001)
                        continue
                else:
                    if now - started_at >= args.run_seconds:
                        break
                    if now < next_command_at:
                        time.sleep(min(0.1, next_command_at - now))
                        continue
                if args.motor_command is not None:
                    command = args.motor_command[command_sequence % len(args.motor_command)]
                else:
                    phase = command_sequence % 4
                    command = ("MOTOR 40 40" if phase == 0 else "MOTOR 0 0" if phase == 1
                               else "MOTOR -40 -40" if phase == 2 else "MOTOR 0 0")
                for controller_id, sock in controllers:
                    sent_ns = time.perf_counter_ns()
                    sock.sendall((command + "\n").encode("ascii"))
                    while True:
                        reply = read_line(sock, args.response_timeout_seconds)
                        received_ns = time.perf_counter_ns()
                        if reply.startswith("DIST "):
                            writer.writerow(["sensor", command_sequence, controller_id, received_ns, "", reply, "", ""])
                            continue
                        writer.writerow(["motor", command_sequence, controller_id, sent_ns, command, reply,
                                         received_ns - sent_ns, command_at_sim_ns or ""])
                        if reply != "OK MOTOR":
                            raise RuntimeError(f"{controller_id}: {command!r} returned {reply!r}")
                        break
                    if args.sensor_query_every and (command_sequence + 1) % args.sensor_query_every == 0:
                        sent_ns = time.perf_counter_ns()
                        sock.sendall(b"SENSOR?\n")
                        reply = read_line(sock, args.response_timeout_seconds)
                        received_ns = time.perf_counter_ns()
                        if not reply.startswith("DIST "):
                            raise RuntimeError(f"{controller_id}: SENSOR? returned unsupported reply {reply!r}")
                        writer.writerow(["sensor_query", command_sequence, controller_id, sent_ns,
                                         "SENSOR?", reply, received_ns - sent_ns, command_at_sim_ns or ""])
                csv_file.flush()
                command_sequence += 1
                next_command_at = started_at + command_sequence * args.cycle_seconds

            for controller_id, sock in controllers:
                try:
                    sent_ns = time.perf_counter_ns()
                    sock.sendall(b"MOTOR 0 0\n")
                    reply = read_line(sock, args.response_timeout_seconds)
                    received_ns = time.perf_counter_ns()
                    writer.writerow(["final_stop", command_sequence, controller_id, sent_ns, "MOTOR 0 0", reply,
                                     received_ns - sent_ns, ""])
                except (OSError, TimeoutError):
                    pass
            csv_file.flush()
        finally:
            for _, sock in controllers:
                sock.close()
    print(f"PERF_CONTROLLER_COMPLETE controllers={len(controllers)} motor_commands={command_sequence} output={args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
