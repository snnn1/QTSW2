"""Scan robot_ENGINE*.jsonl for live-validation event rates (fast path skips json for unrelated lines)."""
from __future__ import annotations

import argparse
import glob
import json
import os
from collections import defaultdict
from datetime import datetime, timezone

EVENTS = frozenset(
    {
        "TAGGED_BROKER_WITHOUT_JOURNAL_DETECTED",
        "TAGGED_BROKER_EXPOSURE_RECOVERY_JOURNAL_UPSERT",
        "TAGGED_BROKER_WITHOUT_JOURNAL_REPAIR_COMPLETE",
        "RECONCILIATION_MISMATCH_DETECTED",
        "POSITION_DRIFT_DETECTED",
    }
)
_ANY = "TAGGED_BROKER_WITHOUT_JOURNAL_DETECTED"  # substring to cheap-filter lines


def parse_ts(s: str) -> datetime:
    s = (s or "").replace("\\u002B", "+")
    return datetime.fromisoformat(s.replace("Z", "+00:00"))


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--log-root", default=os.path.join(os.path.dirname(os.path.dirname(__file__)), "logs", "robot"))
    ap.add_argument("--since-utc", required=True, help="e.g. 2026-03-30T18:35:00+00:00")
    ap.add_argument(
        "--all-archives",
        action="store_true",
        help="Include every logs/robot/archive/robot_ENGINE*.jsonl (slow). Default: only archives for since-utc calendar day (YYYYMMDD in filename).",
    )
    args = ap.parse_args()
    since = datetime.fromisoformat(args.since_utc.replace("Z", "+00:00"))
    if since.tzinfo is None:
        since = since.replace(tzinfo=timezone.utc)

    files = [os.path.join(args.log_root, "robot_ENGINE.jsonl")]
    day = since.strftime("%Y%m%d")
    if args.all_archives:
        files.extend(glob.glob(os.path.join(args.log_root, "archive", "robot_ENGINE*.jsonl")))
    else:
        files.extend(glob.glob(os.path.join(args.log_root, "archive", f"robot_ENGINE_{day}_*.jsonl")))
    files = [f for f in files if os.path.isfile(f)]

    per_sec: dict[str, dict[str, int]] = defaultdict(lambda: defaultdict(int))
    totals: dict[str, int] = defaultdict(int)
    detected_rows: list[tuple[datetime, str, str, str, str]] = []

    for fpath in files:
        with open(fpath, encoding="utf-8", errors="replace") as fh:
            for line in fh:
                if _ANY not in line and "RECONCILIATION_MISMATCH_DETECTED" not in line and "POSITION_DRIFT_DETECTED" not in line:
                    if "TAGGED_BROKER_EXPOSURE_RECOVERY_JOURNAL_UPSERT" not in line and "TAGGED_BROKER_WITHOUT_JOURNAL_REPAIR_COMPLETE" not in line:
                        continue
                try:
                    o = json.loads(line)
                except json.JSONDecodeError:
                    continue
                ts = o.get("ts_utc") or ""
                try:
                    dt = parse_ts(ts)
                    if dt.tzinfo is None:
                        dt = dt.replace(tzinfo=timezone.utc)
                except (TypeError, ValueError):
                    continue
                if dt < since:
                    continue
                ev = o.get("event")
                if ev not in EVENTS:
                    continue
                sec = dt.strftime("%Y-%m-%dT%H:%M:%S")
                per_sec[ev][sec] += 1
                totals[ev] += 1
                if ev == "TAGGED_BROKER_WITHOUT_JOURNAL_DETECTED":
                    d = o.get("data") or {}
                    detected_rows.append(
                        (
                            dt,
                            str(d.get("instrument", "")),
                            str(d.get("account_qty", "")),
                            str(d.get("journal_qty", "")),
                            str(d.get("repair_result", "")),
                        )
                    )

    print(f"Since {args.since_utc} files={len(files)}")
    print("--- Totals ---")
    for ev in sorted(EVENTS):
        print(f"{ev} : {totals[ev]}")
    print("--- mean (nonempty sec) / peak ---")
    for ev in sorted(EVENTS):
        h = per_sec[ev]
        if not h:
            print(f"{ev} mean=0 peak=0")
            continue
        sm = sum(h.values())
        mean = round(sm / len(h), 4)
        sec_max = max(h, key=lambda k: h[k])
        print(f"{ev} mean={mean} active_sec={len(h)} peak={h[sec_max]} at {sec_max}")
    d = totals["TAGGED_BROKER_WITHOUT_JOURNAL_DETECTED"]
    u = totals["TAGGED_BROKER_EXPOSURE_RECOVERY_JOURNAL_UPSERT"]
    c = totals["TAGGED_BROKER_WITHOUT_JOURNAL_REPAIR_COMPLETE"]
    print(f"DETECTED:UPSERT:COMPLETE = {d}:{u}:{c}")
    if u:
        print(f"DETECTED:UPSERT ratio = {round(d / u, 4)}")
    if c:
        print(f"DETECTED:COMPLETE ratio = {round(d / c, 4)}")

    detected_rows.sort(key=lambda r: r[0])
    min_gap = None
    burst: list[tuple[float, str, tuple[str, str, str, str]]] = []
    prev: dict[tuple[str, str, str, str], datetime] = {}
    for row in detected_rows:
        key = row[1:]
        if key in prev:
            gap = (row[0] - prev[key]).total_seconds()
            if min_gap is None or gap < min_gap:
                min_gap = gap
            if gap < 3.0:
                burst.append((gap, row[0].isoformat(), key))
        prev[key] = row[0]
    print("--- Cooldown (TAGGED DETECTED same instrument/account_qty/journal_qty/repair_result) ---")
    if min_gap is not None:
        print(f"min_gap_sec={round(min_gap, 3)} pairs_under_3s={len(burst)}")
        for x in burst[:12]:
            print(" ", x)
    else:
        print("no pairs")


if __name__ == "__main__":
    main()
