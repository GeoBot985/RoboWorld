from __future__ import annotations

import argparse
import socket


parser = argparse.ArgumentParser()
parser.add_argument("--host", default="127.0.0.1")
parser.add_argument("--port", type=int, default=45123)
parser.add_argument("--timeout", type=float, default=5.0)
args = parser.parse_args()

with socket.create_connection((args.host, args.port), timeout=args.timeout) as uart:
    uart.settimeout(args.timeout)
    uart.sendall(b"PING\n")
    while True:
        response = bytearray()
        while not response.endswith(b"\n"):
            data = uart.recv(1)
            if not data:
                raise ConnectionError("UART closed before PONG")
            response.extend(data)
        line = response.decode("ascii").rstrip("\r\n")
        print(line)
        if line.startswith("PONG "):
            break
