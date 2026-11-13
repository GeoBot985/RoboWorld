from __future__ import annotations

import argparse
import socket
import time
import unittest


def read_line(connection: socket.socket, timeout: float = 4.0) -> str:
    connection.settimeout(timeout)
    data = bytearray()
    while not data.endswith(b"\n"):
        chunk = connection.recv(1)
        if not chunk:
            raise ConnectionError("UART socket closed before line terminator")
        data.extend(chunk)
    return data.decode("ascii", errors="replace").rstrip("\r\n")


class FirmwareUartTests(unittest.TestCase):
    host = "127.0.0.1"
    port = 45123
    build_id = ""

    @classmethod
    def setUpClass(cls) -> None:
        cls.uart = socket.create_connection((cls.host, cls.port), timeout=4.0)

    def setUp(self) -> None:
        self.uart = type(self).uart

    def tearDown(self) -> None:
        type(self).uart = self.uart

    @classmethod
    def tearDownClass(cls) -> None:
        cls.uart.close()

    def test_ping_reply_contains_real_build_id(self) -> None:
        self.uart.sendall(b"PING\n")
        self.assertEqual(read_line(self.uart), f"PONG {self.build_id}")

    def test_status_command(self) -> None:
        self.uart.sendall(b"STATUS?\n")
        self.assertEqual(read_line(self.uart), "STATUS MCU=OK UART=OK")

    def test_unknown_command(self) -> None:
        self.uart.sendall(b"MOVE 1 1\n")
        self.assertEqual(read_line(self.uart), "ERR CMD")

    def test_overlong_line_is_rejected(self) -> None:
        self.uart.sendall(b"X" * 80 + b"\n")
        self.assertEqual(read_line(self.uart), "ERR LINE")

    def test_incomplete_line_times_out_and_is_discarded(self) -> None:
        self.uart.sendall(b"PI")
        self.assertEqual(read_line(self.uart, timeout=5.0), "ERR TIMEOUT")
        self.uart.sendall(b"NG\n")
        self.assertEqual(read_line(self.uart), "ERR CMD")

    def test_uart_client_can_reconnect(self) -> None:
        self.uart.close()
        time.sleep(0.2)
        self.uart = socket.create_connection((self.host, self.port), timeout=4.0)
        self.uart.sendall(b"PING\n")
        self.assertEqual(read_line(self.uart), f"PONG {self.build_id}")


if __name__ == "__main__":
    argument_parser = argparse.ArgumentParser(add_help=False)
    argument_parser.add_argument("--host", default="127.0.0.1")
    argument_parser.add_argument("--port", type=int, default=45123)
    argument_parser.add_argument("--build-id", required=True)
    args, remaining = argument_parser.parse_known_args()
    FirmwareUartTests.host = args.host
    FirmwareUartTests.port = args.port
    FirmwareUartTests.build_id = args.build_id
    unittest.main(argv=[__file__, *remaining])
