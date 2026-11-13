"""External host-side controller; all protocol decisions remain in STM32 firmware."""

from __future__ import annotations

import argparse
import socket
import time


def read_line(connection: socket.socket, timeout: float) -> str:
    connection.settimeout(timeout)
    data = bytearray()
    while not data.endswith(b"\n"):
        chunk = connection.recv(1)
        if not chunk:
            raise ConnectionError("UART socket closed before line terminator")
        data.extend(chunk)
    return data.decode("ascii", errors="replace").rstrip("\r\n")


def query_sensor(connection: socket.socket, timeout: float, index: int) -> None:
    request = b"SENSOR?\n"
    sent_ns = time.perf_counter_ns()
    connection.sendall(request)
    reply = read_line(connection, timeout)
    observed_ns = time.perf_counter_ns() - sent_ns
    expected_ns = round((len(request) + len(reply.encode("ascii")) + 1) * 10 * 1_000_000_000 / 115_200)
    print(f"Sensor reply {index}: {reply}")
    print(f"UART_TIMING observed_rtt_ns={observed_ns} expected_8n1_ns={expected_ns} payload_bytes={len(request) + len(reply.encode('ascii')) + 1} baud=115200 framing=8N1")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=45123)
    parser.add_argument("--timeout", type=float, default=3.0)
    parser.add_argument("--ping-duration-seconds", type=float, default=0.0, help="keep the UART session open and periodically PING the firmware for this many seconds")
    parser.add_argument("--ping-interval-seconds", type=float, default=30.0, help="wall seconds between PINGs in a sustained session")
    parser.add_argument("--motor", nargs=2, type=int, metavar=("LEFT", "RIGHT"), help="send a raw MOTOR command through the virtual UART after PING")
    parser.add_argument("--motor-duration", type=float, default=0, help="hold --motor for this many seconds, then command zero duty")
    parser.add_argument("--motor-sequence", action="append", metavar="LEFT,RIGHT", help="send a raw MOTOR command in the specified order; repeat for a sequence")
    parser.add_argument("--motor-sequence-delay", type=float, default=1.0, help="wall seconds between sequence commands")
    parser.add_argument("--motor-sequence-final-hold-seconds", type=float, default=0.0, help="hold the final sequenced motor command this long, then send MOTOR 0 0")
    parser.add_argument("--sensor-interleave-every", type=int, default=0, help="query the MCU distance sensor after each N sequenced MOTOR commands")
    parser.add_argument("--sensor-interleave-start-after", type=int, default=1, help="begin interleaved queries after this many sequenced MOTOR commands")
    parser.add_argument("--sensor-count", type=int, default=0, help="query the firmware distance sensor this many times")
    parser.add_argument("--sensor-interval", type=float, default=0.2, help="wall seconds between distance queries")
    parser.add_argument("--uart-overflow-probe", action="store_true", help="send an overlength line and require the firmware ERR LINE policy")
    parser.add_argument("--uart-line-boundary-probe", action="store_true", help="probe the firmware's 47/48-byte accepted-line transition")
    parser.add_argument("--uart-grammar-probe", action="store_true", help="probe STATUS?, range rejection and malformed MOTOR command responses")
    parser.add_argument("--probe-move-bypass", action="store_true", help="send a direct MOVE command outside the firmware motion grammar")
    args = parser.parse_args()
    if args.sensor_interleave_every < 0 or args.sensor_interleave_start_after < 1:
        parser.error("sensor interleave interval must be non-negative and start-after must be positive")

    with socket.create_connection((args.host, args.port), timeout=args.timeout) as uart:
        sensor_queries_sent = 0
        print(f"UART boot: {read_line(uart, args.timeout)}")
        if args.ping_duration_seconds > 0:
            if args.ping_interval_seconds <= 0:
                parser.error("--ping-interval-seconds must be positive for a sustained session")
            deadline = time.monotonic() + args.ping_duration_seconds
            count = 0
            while True:
                sent_ns = time.perf_counter_ns()
                uart.sendall(b"PING\n")
                reply = read_line(uart, args.timeout)
                elapsed_ns = time.perf_counter_ns() - sent_ns
                if not reply.startswith("PONG "):
                    raise RuntimeError(f"unexpected firmware PING response: {reply!r}")
                count += 1
                print(f"SOAK_PING index={count} rtt_ns={elapsed_ns} reply={reply}", flush=True)
                sleep_for = min(args.ping_interval_seconds, deadline - time.monotonic())
                if sleep_for <= 0:
                    break
                time.sleep(sleep_for)
            print(f"SOAK_PING_COMPLETE count={count}", flush=True)
        else:
            uart.sendall(b"PING\n")
            print(f"UART reply: {read_line(uart, args.timeout)}")
        if args.probe_move_bypass:
            uart.sendall(b"MOVE 100 100\n")
            print(f"Invalid-command reply: {read_line(uart, args.timeout)}")
        if args.uart_overflow_probe:
            uart.sendall(b"X" * 80 + b"\n")
            print(f"UART overflow reply: {read_line(uart, args.timeout)}")
        if args.uart_line_boundary_probe:
            for payload_size, expected in ((47, "ERR CMD"), (48, "ERR LINE")):
                request = b"PING" + b" " * (payload_size - len(b"PING")) + b"\n"
                uart.sendall(request)
                reply = read_line(uart, args.timeout)
                print(f"UART_BOUNDARY payload_bytes={payload_size} reply={reply}", flush=True)
                if reply != expected:
                    raise RuntimeError(f"UART {payload_size}-byte line expected {expected!r}, got {reply!r}")
        if args.uart_grammar_probe:
            probes = ((b"STATUS?\n", "STATUS MCU=OK UART=OK"),
                      (b"MOTOR 101 0\n", "ERR RANGE"),
                      (b"MOTOR -101 0\n", "ERR RANGE"),
                      (b"MOTOR 1\n", "ERR CMD"),
                      (b"ping\n", "ERR CMD"))
            for request, expected in probes:
                uart.sendall(request)
                reply = read_line(uart, args.timeout)
                print(f"UART_GRAMMAR request_hex={request[:-1].hex()} reply={reply}", flush=True)
                if reply != expected:
                    raise RuntimeError(f"UART request {request!r} expected {expected!r}, got {reply!r}")
        if args.motor is not None:
            left, right = args.motor
            uart.sendall(f"MOTOR {left} {right}\n".encode("ascii"))
            print(f"Motor reply: {read_line(uart, args.timeout)}")
            if args.motor_duration > 0:
                time.sleep(args.motor_duration)
                uart.sendall(b"MOTOR 0 0\n")
                print(f"Motor stop reply: {read_line(uart, args.timeout)}")
        if args.motor_sequence is not None:
            for sequence_index, item in enumerate(args.motor_sequence):
                left_text, right_text = item.split(",", maxsplit=1)
                left, right = int(left_text), int(right_text)
                uart.sendall(f"MOTOR {left} {right}\n".encode("ascii"))
                print(f"Motor {left},{right} reply: {read_line(uart, args.timeout)}")
                time.sleep(max(args.motor_sequence_delay, 0.0))
                commands_sent = sequence_index + 1
                if (args.sensor_interleave_every > 0
                        and commands_sent >= args.sensor_interleave_start_after
                        and (commands_sent - args.sensor_interleave_start_after) % args.sensor_interleave_every == 0
                        and sensor_queries_sent < max(args.sensor_count, 0)):
                    if sensor_queries_sent:
                        time.sleep(max(args.sensor_interval, 0.0))
                    sensor_queries_sent += 1
                    query_sensor(uart, args.timeout, sensor_queries_sent)
            if args.motor_sequence_final_hold_seconds > 0:
                time.sleep(args.motor_sequence_final_hold_seconds)
                uart.sendall(b"MOTOR 0 0\n")
                print(f"Motor sequence stop reply: {read_line(uart, args.timeout)}", flush=True)
        for index in range(sensor_queries_sent, max(args.sensor_count, 0)):
            if index:
                time.sleep(max(args.sensor_interval, 0.0))
            sensor_queries_sent += 1
            query_sensor(uart, args.timeout, sensor_queries_sent)


if __name__ == "__main__":
    main()
