#!/usr/bin/env python3
"""Verify the real-firmware UART accepted-line transition across whole sequences."""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
from pathlib import Path

spec = importlib.util.spec_from_file_location("trace_analyzer", Path(__file__).with_name("analyze-controller-trace.py"))
assert spec and spec.loader
analyzer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(analyzer)


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("campaign_dir", type=Path)
    parser.add_argument("--firmware", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    campaign = args.campaign_dir.resolve()
    firmware_hash = sha256(args.firmware.resolve())
    manifest_path = campaign / "sequence-manifest.json"
    manifest_bytes = manifest_path.read_bytes()
    manifest = json.loads(manifest_bytes)
    if len(manifest) != 6 or sum(row["split"] == "training" for row in manifest) != 4 \
            or sum(row["split"] == "heldout" for row in manifest) != 2:
        raise ValueError("boundary campaign must contain four training and two whole held-out sequences")
    sequences = []
    for item in manifest:
        if not item.get("line_boundary_probe") or not item.get("uart_grammar_probe"):
            raise ValueError(f"sequence did not request line-boundary probes: {item['sequence_id']}")
        trace = campaign / item["trace"]
        if sha256(trace) != item["trace_sha256"].upper():
            raise ValueError(f"trace hash mismatch: {item['sequence_id']}")
        if item["firmware_sha256"].upper() != firmware_hash:
            raise ValueError(f"firmware hash mismatch: {item['sequence_id']}")
        records = analyzer.load(trace)
        requests = analyzer.lines(records, "controller_to_mcu")
        responses = analyzer.lines(records, "mcu_to_controller")
        if not responses or not responses[0]["line"].startswith(b"BOOT "):
            raise ValueError(f"firmware boot reply missing: {item['sequence_id']}")
        responses = responses[1:]
        if len(requests) != len(responses):
            raise ValueError(f"UART request/response line count differs: {item['sequence_id']} ({len(requests)} / {len(responses)})")
        pairs = list(zip(requests, responses, strict=True))
        boundary = {}
        for request, response in pairs:
            size = len(request["line"])
            if size in (47, 48):
                boundary[size] = {"request_hex": request["line"].hex(), "response": response["line"].decode("ascii")}
        expected = {47: b"ERR CMD", 48: b"ERR LINE"}
        if set(boundary) != set(expected):
            raise ValueError(f"sequence lacks exactly one 47/48-byte boundary pair: {item['sequence_id']}")
        for size, expected_reply in expected.items():
            observed = next(response["line"] for request, response in pairs if len(request["line"]) == size)
            if observed != expected_reply:
                raise ValueError(f"{size}-byte line expected {expected_reply!r}, got {observed!r} in {item['sequence_id']}")
        grammar_expected = {b"STATUS?": b"STATUS MCU=OK UART=OK", b"MOTOR 101 0": b"ERR RANGE",
                            b"MOTOR -101 0": b"ERR RANGE", b"MOTOR 1": b"ERR CMD", b"ping": b"ERR CMD"}
        grammar = {}
        for request, response in pairs:
            request_bytes, response_bytes = request["line"], response["line"]
            if request_bytes in grammar_expected:
                if request_bytes in grammar:
                    raise ValueError(f"duplicate grammar probe {request_bytes!r}: {item['sequence_id']}")
                if response_bytes != grammar_expected[request_bytes]:
                    raise ValueError(f"grammar probe {request_bytes!r} expected {grammar_expected[request_bytes]!r}, got {response_bytes!r}")
                grammar[request_bytes] = response_bytes
        if set(grammar) != set(grammar_expected):
            raise ValueError(f"sequence lacks the five expected UART grammar probes: {item['sequence_id']}")
        sequences.append({"sequence_id": item["sequence_id"], "split": item["split"], "firmware_sha256": firmware_hash,
                          "line_47": boundary[47], "line_48": boundary[48], "exact_matches": 2})
        sequences[-1]["grammar_probes"] = [{"request_hex": request.hex(), "response": grammar[request].decode("ascii")}
                                             for request in sorted(grammar)]
    heldout = [row for row in sequences if row["split"] == "heldout"]
    result = {"status": "REFERENCE_FIRMWARE_UART_GRAMMAR_AND_LINE_BOUNDARY_PASS_NOT_PROFILE_QUALIFICATION",
              "campaign_manifest_sha256": hashlib.sha256(manifest_bytes).hexdigest().upper(),
              "firmware_sha256": firmware_hash, "training_sequences": 4, "heldout_sequences": 2,
              "heldout_boundary_exact_matches": sum(row["exact_matches"] for row in heldout),
              "heldout_grammar_exact_matches": sum(len(row["grammar_probes"]) for row in heldout),
              "heldout_47_byte_reply": "ERR CMD", "heldout_48_byte_reply": "ERR LINE",
              "sequences": sequences,
              "scope": "UART parser boundary only; no controller-profile qualification or timing claim"}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"status": result["status"], "heldout_exact_matches": result["heldout_boundary_exact_matches"],
                      "heldout_grammar_exact_matches": result["heldout_grammar_exact_matches"],
                      "output": str(args.output)}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
