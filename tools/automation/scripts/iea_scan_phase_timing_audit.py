"""One-off audit: IEA_ADOPTION_SCAN_PHASE_TIMING over logs/robot/**/*.jsonl."""
import json
import os
import sys
from datetime import datetime, timezone

PHASE_KEYS = [
    ("fingerprint_build_ms", "phase_fingerprint_build_ms"),
    ("phase_candidates_ms", "phase_candidates_ms"),
    ("phase_precount_ms", "phase_precount_ms"),
    ("phase_journal_diag_ms", "phase_journal_diag_ms"),
    ("phase_pre_loop_log_ms", "phase_pre_loop_log_ms"),
    ("phase_main_loop_ms", "phase_main_loop_ms"),
    ("phase_summary_ms", "phase_summary_ms"),
]


def num(x):
    if x is None:
        return 0.0
    if isinstance(x, (int, float)):
        return float(x)
    try:
        return float(str(x))
    except (TypeError, ValueError):
        return 0.0


def parse_ts(s):
    if not s:
        return None
    try:
        s2 = str(s).replace("Z", "+00:00")
        return datetime.fromisoformat(s2)
    except Exception:
        return None


def main():
    base = os.path.join(os.path.dirname(__file__), "..", "..", "logs", "robot")
    base = os.path.normpath(base)
    if len(sys.argv) > 1:
        base = os.path.normpath(sys.argv[1])

    paths = []
    for root, _dirs, files in os.walk(base):
        for f in files:
            if not f.endswith(".jsonl"):
                continue
            # Default: only engine + execution streams (hydration/ranges are huge).
            if len(sys.argv) < 2 and not (
                f.startswith("robot_ENGINE") or f == "robot_EXECUTION.jsonl"
            ):
                continue
            paths.append(os.path.join(root, f))

    timing_rows = []
    completed_recovery = []
    stalled = []

    for path in paths:
        try:
            with open(path, "r", encoding="utf-8", errors="replace") as fp:
                for line in fp:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        o = json.loads(line)
                    except json.JSONDecodeError:
                        continue
                    ev = o.get("event")
                    d = o.get("data") or {}
                    if ev == "IEA_ADOPTION_SCAN_PHASE_TIMING":
                        if d.get("scan_request_source") != "RecoveryAdoption":
                            continue
                        timing_rows.append(
                            {
                                "path": os.path.basename(path),
                                "ts": o.get("ts_utc"),
                                "d": d,
                            }
                        )
                    elif ev == "IEA_ADOPTION_SCAN_EXECUTION_COMPLETED":
                        if d.get("scan_request_source") != "RecoveryAdoption":
                            continue
                        completed_recovery.append(
                            {"path": os.path.basename(path), "ts": o.get("ts_utc"), "d": d}
                        )
                    elif ev == "EXECUTION_COMMAND_STALLED":
                        stalled.append(
                            {"path": os.path.basename(path), "ts": o.get("ts_utc"), "d": d}
                        )
        except OSError:
            pass

    stalled_rec = []
    for s in stalled:
        d = s["d"]
        blob = json.dumps(d)
        if "RecoveryAdoptionScan" in blob:
            stalled_rec.append(s)

    print("base", base)
    print("PHASE_TIMING RecoveryAdoption", len(timing_rows))
    print("EXECUTION_COMPLETED RecoveryAdoption", len(completed_recovery))
    print("EXECUTION_COMMAND_STALLED all", len(stalled))
    print("EXECUTION_COMMAND_STALLED RecoveryAdoptionScan", len(stalled_rec))

    n = len(timing_rows)
    if n:
        totals = [num(r["d"].get("total_wall_ms")) for r in timing_rows]
        print("total_wall_ms avg", sum(totals) / n, "max", max(totals))
        for key, _ in PHASE_KEYS:
            vals = [num(r["d"].get(key)) for r in timing_rows]
            pct = [100 * v / t if t > 0 else 0 for v, t in zip(vals, totals)]
            print(
                key,
                "avg",
                sum(vals) / n,
                "max",
                max(vals),
                "avg_pct_of_total",
                sum(pct) / n,
            )
        avgs = [(key, sum(num(r["d"].get(key)) for r in timing_rows) / n) for key, _ in PHASE_KEYS]
        avgs.sort(key=lambda x: -x[1])
        print("RANK_AVG", avgs)
        worst = [(key, max(num(r["d"].get(key)) for r in timing_rows)) for key, _ in PHASE_KEYS]
        worst.sort(key=lambda x: -x[1])
        print("RANK_MAX", worst)

    for r in sorted(timing_rows, key=lambda x: x["ts"] or ""):
        d = r["d"]
        print(
            "ROW",
            r["ts"],
            d.get("execution_instrument_key"),
            "tw",
            d.get("total_wall_ms"),
            "cand",
            d.get("phase_candidates_ms"),
            "fp",
            d.get("fingerprint_build_ms"),
            "main",
            d.get("phase_main_loop_ms"),
            "orders",
            d.get("account_orders_total"),
            "cic",
            d.get("candidate_intent_count"),
            "seen",
            d.get("main_loop_orders_seen"),
        )

    for s in stalled_rec:
        print("STALL", s["ts"], json.dumps(s["d"])[:800])

    # nearest timing per stall (same instrument)
    timing_by_inst = {}
    for r in timing_rows:
        inst = (r["d"].get("execution_instrument_key") or "").strip()
        timing_by_inst.setdefault(inst, []).append(r)

    for s in stalled_rec:
        d = s["d"]
        inst = str(d.get("execution_instrument_key") or d.get("instrument") or "").strip()
        st = parse_ts(s["ts"])
        best = None
        best_dt = None
        for r in timing_by_inst.get(inst, []):
            rt = parse_ts(r["ts"])
            if rt is None or st is None:
                continue
            # normalize tz
            if rt.tzinfo is None:
                rt = rt.replace(tzinfo=timezone.utc)
            if st.tzinfo is None:
                st = st.replace(tzinfo=timezone.utc)
            delta = (st - rt).total_seconds()
            if delta >= 0:
                if best_dt is None or delta < best_dt:
                    best_dt = delta
                    best = r
        print("STALL_NEAREST_TIMING", s["ts"], inst, "nearest_before_s", best_dt, "row_ts", best["ts"] if best else None)


if __name__ == "__main__":
    main()
