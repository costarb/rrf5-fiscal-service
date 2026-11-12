"""Scan every layer of a `docker save` archive for secret material (SF-004, T026).

Usage: python scripts/scan_image_layers.py image.tar

Each layer is read as stored (plain or gzip tar, Docker or OCI layout), and every
file of every layer is searched, so material that a later layer deletes is still
found. Exit code 1 lists the layer, path and pattern name, never the match itself.
"""

import gzip
import io
import re
import sys
import tarfile

PATTERNS = {
    "test canary": re.compile(rb"CANARY-[A-Z0-9-]{3,}"),
    "client secret": re.compile(rb"rrf5s_[A-Za-z0-9_-]{43}"),
    "private key": re.compile(rb"-----BEGIN (?:RSA |EC |ENCRYPTED )?PRIVATE KEY-----"),
}
SKIP_SUFFIXES = (".json",)  # image config and manifests carry no file content


def open_layer(data: bytes) -> tarfile.TarFile | None:
    if data[:2] == b"\x1f\x8b":
        data = gzip.decompress(data)
    try:
        return tarfile.open(fileobj=io.BytesIO(data), mode="r:")
    except tarfile.TarError:
        return None


def main(path: str) -> int:
    findings = []
    layers = 0
    with tarfile.open(path, mode="r:") as archive:
        for member in archive.getmembers():
            if not member.isfile() or member.name.endswith(SKIP_SUFFIXES):
                continue
            layer = open_layer(archive.extractfile(member).read())
            if layer is None:
                continue
            layers += 1
            with layer:
                for entry in layer.getmembers():
                    if not entry.isfile():
                        continue
                    content = layer.extractfile(entry).read()
                    for name, pattern in PATTERNS.items():
                        if pattern.search(content):
                            findings.append(f"{member.name}: {entry.name}: {name}")
    if layers == 0:
        print("no layer found in the archive")
        return 1
    for finding in findings:
        print(finding)
    print(f"scanned {layers} layers: {len(findings)} findings")
    return 1 if findings else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1]))
