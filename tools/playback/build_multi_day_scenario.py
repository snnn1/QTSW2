#!/usr/bin/env python3
"""Build an explicit multi-day NinjaTrader Playback scenario bundle.

The robot consumes the output only when QTSW2_PLAYBACK_SCENARIO points at the
generated playback_scenario.json. Normal Playback-account runs are unchanged.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
from datetime import date, datetime, timedelta, timezone
from pathlib import Path
from typing import Any


def _repo_root() -> Path:
    return Path(__file__).resolve().parents[2]


def _parse_day(raw: str) -> date:
    return datetime.strptime(raw, "%Y-%m-%d").date()


def _iter_days(start: date, end: date, include_weekends: bool) -> list[date]:
    if end < start:
        raise SystemExit("--end must be >= --start")
    days: list[date] = []
    cur = start
    while cur <= end:
        if include_weekends or cur.weekday() < 5:
            days.append(cur)
        cur += timedelta(days=1)
    if not days:
        raise SystemExit("date range produced no scenario days")
    return days


def _latest_matrix(root: Path) -> Path:
    matrix_dir = root / "data" / "master_matrix"
    candidates = sorted(matrix_dir.glob("master_matrix_*.parquet"), key=lambda p: p.stat().st_mtime, reverse=True)
    if not candidates:
        raise SystemExit(f"no master_matrix_*.parquet files found in {matrix_dir}")
    return candidates[0]


def _sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def _write_json(path: Path, payload: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, indent=2, sort_keys=False) + "\n", encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--start", required=True, help="First session trading date, YYYY-MM-DD")
    parser.add_argument("--end", required=True, help="Last session trading date, YYYY-MM-DD")
    parser.add_argument("--matrix", help="Master matrix parquet. Defaults to latest data/master_matrix/master_matrix_*.parquet")
    parser.add_argument("--run-id", help="Scenario run id/folder. Defaults to playback_scenario_<start>_<end>_<utc>")
    parser.add_argument("--scenario-id", help="Scenario id. Defaults to run id.")
    parser.add_argument("--include-weekends", action="store_true", help="Include Saturday/Sunday session dates instead of weekday-only.")
    args = parser.parse_args()

    root = _repo_root()
    sys.path.insert(0, str(root / "system"))

    import pandas as pd  # local import so --help has no data dependency
    from modules.timetable.timetable_engine import TimetableEngine

    start = _parse_day(args.start)
    end = _parse_day(args.end)
    days = _iter_days(start, end, args.include_weekends)
    matrix_path = Path(args.matrix).resolve() if args.matrix else _latest_matrix(root)
    if not matrix_path.exists():
        raise SystemExit(f"matrix file missing: {matrix_path}")

    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    run_id = args.run_id or f"playback_scenario_{start:%Y%m%d}_{end:%Y%m%d}_{stamp}"
    scenario_id = args.scenario_id or run_id
    scenario_dir = root / "runs" / run_id / "playback_scenario"
    timetable_dir = scenario_dir / "timetables"
    matrix_hash = _sha256(matrix_path)

    df = pd.read_parquet(matrix_path)
    engine = TimetableEngine(project_root=str(root))

    timetables: dict[str, dict[str, str]] = {}
    generated_days: list[str] = []
    for day in days:
        day_str = day.isoformat()
        preview = engine.write_execution_timetable_from_master_matrix(
            df,
            trade_date=day_str,
            execution_mode=True,
            preview_only=True,
            mode="historical",
            publish_context={
                "source": "playback_scenario_builder",
                "scenario_id": scenario_id,
                "matrix_source": str(matrix_path),
            },
        )
        if preview is None:
            raise SystemExit(f"no timetable streams produced for {day_str}")

        doc = {
            "as_of": datetime.now(timezone.utc).isoformat(),
            "session_trading_date": day_str,
            "trading_date": day_str,
            "timezone": "America/Chicago",
            "source": "playback_scenario",
            "timetable_hash": preview.timetable_hash,
            "previous_hash": None,
            "version_timestamp": datetime.now(timezone.utc).isoformat(),
            "metadata": {
                "replay": True,
                "scenario_id": scenario_id,
                "matrix_snapshot": matrix_path.name,
                "matrix_sha256": matrix_hash,
            },
            "streams": preview.streams,
        }
        out_path = timetable_dir / f"timetable_{day_str}.json"
        _write_json(out_path, doc)
        rel = out_path.relative_to(scenario_dir).as_posix()
        timetables[day_str] = {"path": rel, "hash": _sha256(out_path)}
        generated_days.append(day_str)

    manifest = {
        "scenario_id": scenario_id,
        "mode": "multi_day_carryover",
        "run_id": run_id,
        "matrix_snapshot": str(matrix_path),
        "matrix_sha256": matrix_hash,
        "created_utc": datetime.now(timezone.utc).isoformat(),
        "dates": generated_days,
        "state_policy": {
            "persistence": "isolated_run_root",
            "stream_journals": "load_and_carry_forward",
            "day_switch": "event_clock_cme_18ct",
        },
        "timetables": timetables,
    }
    manifest_path = scenario_dir / "playback_scenario.json"
    _write_json(manifest_path, manifest)

    print(f"scenario={scenario_id}")
    print(f"run_id={run_id}")
    print(f"manifest={manifest_path}")
    print(f"matrix={matrix_path}")
    print(f"days={','.join(generated_days)}")
    print(f"set QTSW2_PLAYBACK_SCENARIO={manifest_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
