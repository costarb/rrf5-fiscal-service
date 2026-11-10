#!/usr/bin/env python3
"""Fail when repository/build-context controls can expose fiscal secrets."""

from __future__ import annotations

import pathlib
import re
import subprocess
import sys


ROOT = pathlib.Path(__file__).resolve().parents[1]
SELF = pathlib.Path(__file__).resolve()
SENSITIVE_SUFFIXES = {".pfx", ".p12", ".pem", ".key", ".cer", ".crt", ".jks"}
REQUIRED_IGNORES = {
    ".gitignore": {".env*", "*.pfx", "*.p12", "*.pem", "*.key", "*.cer", "secrets/", "artifacts/", "*.log"},
    ".dockerignore": {".git", ".env*", "*.pfx", "*.p12", "*.pem", "*.key", "*.cer", "secrets", "artifacts", "*.log"},
}
SECRET_MARKERS = (
    re.compile(r"-----BEGIN (?:RSA |EC |ENCRYPTED )?PRIVATE KEY-----"),
    re.compile(r"-----BEGIN PKCS12-----"),
)


def candidate_files() -> list[pathlib.Path]:
    result = subprocess.run(
        ["git", "ls-files", "--cached", "--others", "--exclude-standard", "-z"],
        cwd=ROOT,
        check=True,
        capture_output=True,
    )
    return [ROOT / item.decode("utf-8") for item in result.stdout.split(b"\0") if item]


def normalized_lines(path: pathlib.Path) -> set[str]:
    return {
        line.strip().replace("\\", "/")
        for line in path.read_text(encoding="utf-8").splitlines()
        if line.strip() and not line.lstrip().startswith("#")
    }


def main() -> int:
    errors: list[str] = []
    for relative, required in REQUIRED_IGNORES.items():
        actual = normalized_lines(ROOT / relative)
        for missing in sorted(required - actual):
            errors.append(f"{relative} is missing required exclusion: {missing}")

    for path in candidate_files():
        if path.resolve() == SELF or not path.is_file():
            continue
        relative = path.relative_to(ROOT).as_posix()
        if path.suffix.lower() in SENSITIVE_SUFFIXES:
            errors.append(f"sensitive certificate/key extension is tracked or unignored: {relative}")
            continue
        if path.suffix.lower() == ".log":
            errors.append(f"log file is tracked or unignored: {relative}")
            continue
        if path.stat().st_size > 5_000_000:
            continue
        payload = path.read_bytes()
        if b"\0" in payload:
            continue
        text = payload.decode("utf-8", errors="replace")
        for marker in SECRET_MARKERS:
            if marker.search(text):
                errors.append(f"private-key material marker found: {relative}")

    if errors:
        print("Secret-boundary verification: FAIL", file=sys.stderr)
        for error in errors:
            print(f"- {error}", file=sys.stderr)
        return 1

    print("Secret-boundary verification: PASS")
    print("Checked tracked/unignored files plus .gitignore and .dockerignore controls.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
