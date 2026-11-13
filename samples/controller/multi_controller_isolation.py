import argparse
import socket
import time


def read_line(sock: socket.socket, deadline: float) -> str | None:
    sock.settimeout(max(0.05, deadline - time.monotonic()))
    data = bytearray()
    while time.monotonic() < deadline:
        try:
            chunk = sock.recv(1)
        except socket.timeout:
            continue
        if not chunk:
            return None
        if chunk == b"\n":
            return data.decode("ascii", errors="replace").rstrip("\r")
        data.extend(chunk)
    return None


def expect_pong(sock: socket.socket, deadline: float) -> str | None:
    while time.monotonic() < deadline:
        line = read_line(sock, deadline)
        if line is None:
            return None
        if line.startswith("PONG "):
            return line
    return None


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--primary-port", type=int, required=True)
    parser.add_argument("--secondary-port", type=int, required=True)
    args = parser.parse_args()
    with socket.create_connection((args.host, args.primary_port), timeout=8) as primary, socket.create_connection((args.host, args.secondary_port), timeout=8) as secondary:
        primary.settimeout(8)
        secondary.settimeout(8)
        boots = [read_line(primary, time.monotonic() + 8), read_line(secondary, time.monotonic() + 8)]
        if not all(line and line.startswith("BOOT ") for line in boots):
            raise RuntimeError(f"Missing genuine firmware boot lines: {boots!r}")
        primary.sendall(b"PING\n")
        secondary.sendall(b"PING\n")
        first = expect_pong(primary, time.monotonic() + 8)
        second = expect_pong(secondary, time.monotonic() + 8)
        if not first or not second:
            raise RuntimeError(f"Both MCU instances must answer before fault injection: primary={first!r}, secondary={second!r}")
        print(f"Initial firmware PING/PONG: PASS primary={first} secondary={second}")
        secondary.sendall(b"MOTOR 50 0\n")
        motor_reply = read_line(secondary, time.monotonic() + 8)
        if motor_reply != "OK MOTOR":
            raise RuntimeError(f"Auxiliary firmware did not acknowledge the channel-1 motor command: {motor_reply!r}")
        print("Auxiliary MOTOR 50 0 command: PASS")
        time.sleep(3.5)
        primary.sendall(b"PING\n")
        secondary.sendall(b"PING\n")
        primary_reply = expect_pong(primary, time.monotonic() + 6)
        secondary_reply = expect_pong(secondary, time.monotonic() + 2)
        if not primary_reply:
            raise RuntimeError("Primary drive MCU stopped answering after the auxiliary MCU fault.")
        if secondary_reply:
            raise RuntimeError(f"Halted auxiliary MCU returned {secondary_reply!r}.")
        print(f"Fault isolation: PASS primary replied {primary_reply}; halted auxiliary remained silent.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
