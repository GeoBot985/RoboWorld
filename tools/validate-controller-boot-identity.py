#!/usr/bin/env python3
"""Verify that Exact UART endpoints boot the firmware bound to each controller."""
from __future__ import annotations

import argparse
import csv
import hashlib
import json
import re
from pathlib import Path


CONTROLLERS = {
    "mcu-drive": Path("samples/firmware/stm32-drive/build"),
    "mcu-aux": Path("samples/firmware/stm32-arm/build"),
}


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("campaign_dir", type=Path)
    parser.add_argument("--package-root", type=Path, default=Path("artifacts/release/RoboWorld"))
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    campaign = args.campaign_dir.resolve()
    package = args.package_root.resolve()
    manifests = {
        controller_id: json.loads((campaign / controller_id / "sequence-manifest.json").read_text(encoding="utf-8-sig"))
        for controller_id in CONTROLLERS
    }
    expected: dict[str, str] = {}
    firmware_hashes: dict[str, str] = {}
    for controller_id, relative in CONTROLLERS.items():
        rows = manifests[controller_id]
        if not rows:
            raise ValueError(f"empty controller manifest: {controller_id}")
        firmware_hashes[controller_id] = rows[0]["firmware_sha256"].upper()
        if any(row["firmware_sha256"].upper() != firmware_hashes[controller_id] for row in rows):
            raise ValueError(f"firmware identity changes within campaign: {controller_id}")
        firmware_path = package / relative / "firmware.elf"
        if sha256(firmware_path) != firmware_hashes[controller_id]:
            raise ValueError(f"package firmware hash does not match campaign binding: {controller_id}")
        metadata = (package / relative / "build-metadata.txt").read_text(encoding="utf-8-sig")
        match = re.search(r"(?m)^Build ID: ([0-9a-f]{8})\s*$", metadata)
        if match is None:
            raise ValueError(f"missing build ID in package metadata: {controller_id}")
        expected[controller_id] = match.group(1).lower()

    results = []
    for sequence in json.loads((campaign / "sequence-manifest.json").read_text(encoding="utf-8-sig")):
        run = Path(sequence["run_directory"])
        if not run.is_absolute():
            run = (Path.cwd() / run).resolve()
        meta = json.loads((run / "run.meta.json").read_text(encoding="utf-8-sig"))
        actual = {}
        with (run / "controller-commands.csv").open(newline="", encoding="utf-8-sig") as source:
            for row in csv.DictReader(source):
                if row["event"] == "boot":
                    actual[row["controller_id"]] = row["reply"].removeprefix("BOOT ").lower()
        matches = actual == expected
        results.append({
            "sequence_id": sequence["sequence_id"],
            "run_id": sequence["run_id"],
            "committed_sim_ns": meta.get("performance_phase", {}).get("CommittedSimNs"),
            "expected_firmware_sha256": firmware_hashes,
            "expected_boot_id": expected,
            "observed_boot_id": actual,
            "status": "PASS" if matches else "FAIL_CONTROLLER_FIRMWARE_IDENTITY",
        })
    status = "PASS" if results and all(row["status"] == "PASS" for row in results) else "FAIL_CONTROLLER_FIRMWARE_IDENTITY"
    result = {"status": status, "campaign_dir": str(campaign), "sequences": results}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"status": status, "sequence_count": len(results), "mismatched_sequences": [row["sequence_id"] for row in results if row["status"] != "PASS"], "output": str(args.output)}))
    return 0 if status == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
