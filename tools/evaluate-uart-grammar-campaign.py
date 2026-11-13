#!/usr/bin/env python3
"""Check exact observed UART line grammar on whole held-out Renode sessions."""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import re
from pathlib import Path

spec = importlib.util.spec_from_file_location("trace_analyzer", Path(__file__).with_name("analyze-controller-trace.py"))
trace_analyzer = importlib.util.module_from_spec(spec)
assert spec and spec.loader
spec.loader.exec_module(trace_analyzer)
MOTOR = re.compile(rb"MOTOR (-?\d{1,4}) (-?\d{1,4})\Z")
BOOT = re.compile(rb"BOOT ([0-9a-f]{8})\Z")
PONG = re.compile(rb"PONG ([0-9a-f]{8})\Z")
DISTANCE = re.compile(rb"DIST mm=(\d+) valid=([01])\Z")


def response_expectation(line: bytes, build_id: bytes) -> tuple[str, str]:
    if line == b"PING":
        return "ping", "PONG " + build_id.decode("ascii")
    if len(line) > 64:
        return "overlength", "ERR LINE"
    motor = MOTOR.fullmatch(line)
    if motor:
        left, right = map(int, motor.groups())
        if -100 <= left <= 100 and -100 <= right <= 100:
            return "motor", "OK MOTOR"
        return "motor-range", "ERR RANGE"
    if line == b"SENSOR?":
        return "sensor-query", "DIST mm=<integer> valid=<0|1>"
    if line.startswith(b"MOTOR "):
        return "malformed-motor", "ERR CMD"
    if line in (b"MOVE 100 100", b"STATUS?", b"BOGUS") or line:
        return "unknown-command", "ERR CMD"
    return "empty-command", "ERR CMD"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("campaign_dirs", nargs="+", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    sequences = []
    firmware_hashes = set()
    build_ids = set()
    aggregate = {"training": {"commands": 0, "matched": 0}, "heldout": {"commands": 0, "matched": 0}}
    for campaign_dir in args.campaign_dirs:
        campaign = campaign_dir.resolve()
        manifest_name = "sensor-sequence-manifest.json" if (campaign / "sensor-sequence-manifest.json").exists() else "sequence-manifest.json"
        manifest_path = campaign / manifest_name
        manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
        for entry in manifest:
            if entry["split"] not in ("training", "heldout"):
                raise ValueError(f"unsupported sequence split {entry['split']!r}")
            trace_path = campaign / entry["trace"]
            if hashlib.sha256(trace_path.read_bytes()).hexdigest().upper() != entry["trace_sha256"].upper():
                raise ValueError(f"trace hash mismatch: {entry['sequence_id']}")
            records = trace_analyzer.load(trace_path)
            incoming = sorted(trace_analyzer.lines(records, "controller_to_mcu"), key=lambda item: item["host"])
            outgoing = sorted(trace_analyzer.lines(records, "mcu_to_controller"), key=lambda item: item["host"])
            boot_lines = [item for item in outgoing if BOOT.fullmatch(item["line"])]
            if len(boot_lines) != 1:
                raise ValueError(f"{entry['sequence_id']}: expected one firmware boot banner, observed {len(boot_lines)}")
            boot_id = BOOT.fullmatch(boot_lines[0]["line"]).group(1)
            build_ids.add(boot_id.decode("ascii"))
            firmware_hashes.add(entry["firmware_sha256"].upper())
            replies = [item for item in outgoing if item is not boot_lines[0]]
            reply_index = 0
            matched_commands = 0
            mismatches = []
            command_classes = {}
            for request in incoming:
                command_class, expected = response_expectation(request["line"], boot_id)
                command_classes[command_class] = command_classes.get(command_class, 0) + 1
                if reply_index >= len(replies):
                    mismatches.append({"request": request["line"].decode("ascii", "replace"), "expected": expected, "observed": None})
                    continue
                reply = replies[reply_index]
                reply_index += 1
                observed = reply["line"]
                if command_class == "sensor-query":
                    exact = DISTANCE.fullmatch(observed) is not None
                else:
                    exact = observed.decode("ascii", "replace") == expected
                if not exact or reply["host"] < request["host"]:
                    mismatches.append({"request": request["line"].decode("ascii", "replace"),
                                       "expected": expected, "observed": observed.decode("ascii", "replace"),
                                       "response_before_request": reply["host"] < request["host"]})
                else:
                    matched_commands += 1
            if reply_index != len(replies):
                mismatches.append({"unexpected_responses": [item["line"].decode("ascii", "replace") for item in replies[reply_index:]]})
            split = entry["split"]
            aggregate[split]["commands"] += len(incoming)
            aggregate[split]["matched"] += matched_commands
            sequences.append({"sequence_id": entry["sequence_id"], "split": split,
                              "manifest_sha256": hashlib.sha256(manifest_path.read_bytes()).hexdigest().upper(),
                              "trace_sha256": entry["trace_sha256"].upper(), "input_commands": len(incoming),
                              "matched_responses": reply_index, "command_classes": command_classes,
                              "grammar_mismatches": mismatches})
    if len(firmware_hashes) != 1 or len(build_ids) != 1:
        raise ValueError("UART grammar corpus must use one firmware image and one boot identity")
    if aggregate["training"]["commands"] == 0 or aggregate["heldout"]["commands"] == 0:
        raise ValueError("both training and whole-sequence held-out command sets are required")
    mismatches = [item for sequence in sequences for item in sequence["grammar_mismatches"]]
    report = {
        "status": "UART_GRAMMAR_HELDOUT_PASS" if not mismatches else "UART_GRAMMAR_HELDOUT_FAIL",
        "evaluator": "evaluate-uart-grammar-campaign-1.0.0",
        "evaluator_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest().upper(),
        "firmware_sha256": next(iter(firmware_hashes)), "build_id": next(iter(build_ids)),
        "training": aggregate["training"], "heldout": aggregate["heldout"],
        "heldout_response_contract_match_percent": 100.0 * aggregate["heldout"]["matched"] / aggregate["heldout"]["commands"],
        "grammar_mismatch_count": len(mismatches), "sequences": sequences,
        "limitations": [
            "checks observed line grammar/response values, not UART baud serialization or firmware byte-consumption timestamps",
            "only commands present in the C01 corpus are covered; no arbitrary command language equivalence is claimed",
            "does not validate PWM edge timing, GPIO direction, faults, MCU hidden state, or loaded trajectory fidelity",
            "a grammar observation pass does not by itself qualify the combined controller profile or authorize fast runtime",
        ],
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"status": report["status"], "training_commands": aggregate["training"]["commands"],
                      "heldout_commands": aggregate["heldout"]["commands"], "heldout_response_contract_match_percent": report["heldout_response_contract_match_percent"],
                      "mismatches": len(mismatches), "output": str(args.output)}))
    return 0 if not mismatches else 1


if __name__ == "__main__":
    raise SystemExit(main())
