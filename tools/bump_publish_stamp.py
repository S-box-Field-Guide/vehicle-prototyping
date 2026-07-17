#!/usr/bin/env python3
"""Bump the vehicle_prototyping PUBLISH STAMP. RUN BEFORE EVERY PUBLISH.

The publish stamp (Code/Game/VpBuild.cs `PublishStamp`) is a monotonically increasing build number
surfaced in the boot log and on the Help overlay so a tester can always say exactly which build they
are running. vehicle_prototyping is SINGLE-PLAYER, so the stamp is purely for tester attribution —
there is no lobby metadata or wire gate.
This script increments that int by one and refreshes the human `PublishStampNote` date, then prints the
new value.

Usage (from anywhere):
    python tools/bump_publish_stamp.py [--note "short gist"]

It edits Code/Game/VpBuild.cs in place (byte-safe UTF-8, LF-agnostic) and is idempotent per invocation
(one run == one bump). It touches ONLY the two constant lines; the surrounding file is left untouched.
"""
import argparse
import datetime
import pathlib
import re
import sys

VPBUILD_REL = pathlib.Path("Code") / "Game" / "VpBuild.cs"

STAMP_RE = re.compile(r"(public\s+const\s+int\s+PublishStamp\s*=\s*)(\d+)(\s*;)")
NOTE_RE = re.compile(r'(public\s+const\s+string\s+PublishStampNote\s*=\s*")([^"]*)(";)')


def project_root() -> pathlib.Path:
    # tools/bump_publish_stamp.py -> project root is the parent of tools/
    return pathlib.Path(__file__).resolve().parent.parent


def main() -> int:
    ap = argparse.ArgumentParser(description="Bump the vehicle_prototyping publish stamp (run before every publish).")
    ap.add_argument("--note", default=None, help="Optional short gist appended to the dated note.")
    args = ap.parse_args()

    path = project_root() / VPBUILD_REL
    if not path.exists():
        print(f"ERROR: {path} not found", file=sys.stderr)
        return 1

    text = path.read_text(encoding="utf-8")

    m = STAMP_RE.search(text)
    if not m:
        print("ERROR: could not find `public const int PublishStamp = <n>;` in VpBuild.cs", file=sys.stderr)
        return 1

    old = int(m.group(2))
    new = old + 1
    text = STAMP_RE.sub(lambda mm: f"{mm.group(1)}{new}{mm.group(3)}", text, count=1)

    today = datetime.date.today().isoformat()
    gist = args.note.strip() if args.note else "publish"
    note_val = f"{today} - {gist} (build {new})"
    if NOTE_RE.search(text):
        text = NOTE_RE.sub(lambda mm: f"{mm.group(1)}{note_val}{mm.group(3)}", text, count=1)
    else:
        print("WARNING: PublishStampNote line not found — stamp bumped, note left unchanged", file=sys.stderr)

    path.write_text(text, encoding="utf-8")
    print(f"PublishStamp {old} -> {new}   note: {note_val}")
    print("Remember: record the build in CHANGELOG.md and republish.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
