#!/usr/bin/env python3
"""
Validate an execution timetable JSON file using the same rules as production writes
(validate_streams_before_execution_write — incident 2026-03-20).

Usage:
  python tools/validate_timetable_execution_file.py [path]
  python tools/validate_timetable_execution_file.py data/timetable/timetable_current.json
  python tools/validate_timetable_execution_file.py data/timetable/timetable_replay.json

Exit code 0 = valid, 1 = invalid or missing file.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path


def main() -> None:
    root = Path(__file__).resolve().parent.parent
    path = Path(sys.argv[1]) if len(sys.argv) > 1 else root / "data" / "timetable" / "timetable_current.json"
    path = path.resolve()
    if not path.is_file():
        print(f"ERROR: not a file: {path}", file=sys.stderr)
        sys.exit(1)
    data = json.loads(path.read_text(encoding="utf-8"))
    streams = data.get("streams")
    if not isinstance(streams, list):
        print("ERROR: missing or invalid 'streams' array", file=sys.stderr)
        sys.exit(1)
    sys.path.insert(0, str(root))
    from modules.timetable.timetable_write_guard import validate_streams_before_execution_write

    validate_streams_before_execution_write(streams)
    print(f"OK: {path} ({len(streams)} streams)")


if __name__ == "__main__":
    try:
        main()
    except ValueError as e:
        print(e, file=sys.stderr)
        sys.exit(1)
    except Exception as e:
        print(f"ERROR: {e}", file=sys.stderr)
        sys.exit(1)
