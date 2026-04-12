#!/usr/bin/env python3
"""
Analyze ENGINE_TICK_STALL_* events in robot JSONL logs.

Usage:
  python automation/scripts/analyze_engine_tick_stalls.py [--log-dir PATH] [--days N]

Scans robot_*.jsonl for ENGINE_TICK_STALL_RUNTIME, ENGINE_TICK_STALL_STARTUP,
ENGINE_TICK_STALL_RECOVERED and summarizes patterns (instrument, time, recovery).
"""

import argparse
import json
import sys
from datetime import datetime, timezone
from pathlib import Path

# Add project root for imports if needed
PROJECT_ROOT = Path(__file__).resolve().parents[2]
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))


def parse_ts(ts_str: str) -> datetime | None:
    """Parse ISO timestamp to datetime."""
    try:
        return datetime.fromisoformat(ts_str.replace("Z", "+00:00"))
    except (ValueError, TypeError):
        return None


def infer_instrument_from_path(path: Path) -> str:
    """Extract instrument from filename, e.g. robot_ES.jsonl -> ES."""
    name = path.stem
    if name.startswith("robot_"):
        return name[6:] or "UNKNOWN"
    return "UNKNOWN"


def scan_logs(log_dir: Path, days: int | None) -> list[dict]:
    """Scan robot JSONL files for stall events."""
    events: list[dict] = []
    cutoff = None
    if days is not None:
        cutoff = datetime.now(timezone.utc).replace(tzinfo=timezone.utc)
        from datetime import timedelta
        cutoff = cutoff - timedelta(days=days)

    stall_types = {"ENGINE_TICK_STALL_RUNTIME", "ENGINE_TICK_STALL_STARTUP", "ENGINE_TICK_STALL_RECOVERED"}

    for p in sorted(log_dir.glob("robot_*.jsonl")):
        if "archive" in str(p) or "frontend" in str(p):
            continue
        instrument = infer_instrument_from_path(p)
        try:
            with open(p, encoding="utf-8") as f:
                for line in f:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        rec = json.loads(line)
                    except json.JSONDecodeError:
                        continue
                    data_raw = rec.get("data")
                    ev = rec.get("event") or (data_raw.get("event") if isinstance(data_raw, dict) else None)
                    if ev not in stall_types:
                        continue
                    ts_str = rec.get("ts_utc") or rec.get("timestamp_utc") or ""
                    ts = parse_ts(ts_str)
                    if ts and cutoff and ts < cutoff:
                        continue
                    data = rec.get("data") if isinstance(rec.get("data"), dict) else {}
                    if isinstance(data, str):
                        data = {}
                    events.append({
                        "ts": ts,
                        "ts_str": ts_str,
                        "event": ev,
                        "instrument": instrument,
                        "run_id": rec.get("run_id", ""),
                        "elapsed_seconds": data.get("elapsed_seconds"),
                        "last_tick_utc": data.get("last_tick_utc"),
                        "in_startup_grace": data.get("in_startup_grace"),
                        "consecutive_count": data.get("consecutive_count"),
                    })
        except OSError as e:
            print(f"Warning: could not read {p}: {e}", file=sys.stderr)

    return sorted(events, key=lambda e: (e["ts"] or datetime.min, e["instrument"]))


def main():
    ap = argparse.ArgumentParser(description="Analyze ENGINE_TICK_STALL_* events in robot logs")
    ap.add_argument("--log-dir", type=Path, default=PROJECT_ROOT / "logs" / "robot", help="Robot log directory")
    ap.add_argument("--days", type=int, default=None, help="Only include events from last N days")
    args = ap.parse_args()

    if not args.log_dir.exists():
        print(f"Log dir not found: {args.log_dir}", file=sys.stderr)
        sys.exit(1)

    events = scan_logs(args.log_dir, args.days)

    runtime = [e for e in events if e["event"] == "ENGINE_TICK_STALL_RUNTIME"]
    startup = [e for e in events if e["event"] == "ENGINE_TICK_STALL_STARTUP"]
    recovered = [e for e in events if e["event"] == "ENGINE_TICK_STALL_RECOVERED"]

    print("=" * 60)
    print("ENGINE_TICK_STALL Analysis")
    print("=" * 60)
    print(f"Log dir: {args.log_dir}")
    if args.days:
        print(f"Filter: last {args.days} days")
    print()
    print(f"ENGINE_TICK_STALL_RUNTIME (notifications): {len(runtime)}")
    print(f"ENGINE_TICK_STALL_STARTUP (info only):      {len(startup)}")
    print(f"ENGINE_TICK_STALL_RECOVERED:               {len(recovered)}")
    print()

    if runtime:
        print("RUNTIME stalls (by instrument):")
        by_inst: dict[str, list] = {}
        for e in runtime:
            inst = e["instrument"]
            by_inst.setdefault(inst, []).append(e)
        for inst in sorted(by_inst.keys()):
            evs = by_inst[inst]
            print(f"  {inst}: {len(evs)} event(s)")
            for e in evs[:3]:
                ts = e["ts_str"][:19] if e["ts_str"] else "?"
                el = e.get("elapsed_seconds", "?")
                print(f"    {ts}  elapsed={el}s")
            if len(evs) > 3:
                print(f"    ... and {len(evs) - 3} more")
        print()

    if startup:
        print("STARTUP stalls (first 5):")
        for e in startup[:5]:
            ts = e["ts_str"][:19] if e["ts_str"] else "?"
            inst = e["instrument"]
            print(f"  {ts}  {inst}")
        if len(startup) > 5:
            print(f"  ... and {len(startup) - 5} more")
        print()

    print("See docs/robot/incidents/ENGINE_TICK_STALL_RUNTIME_INVESTIGATION.md for details.")


if __name__ == "__main__":
    main()
