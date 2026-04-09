#!/usr/bin/env python3
"""
Log-backed reconciliation audit for one trading_date (Chicago session date).
Streams JSONL — prefilter lines before json.loads for performance.

Usage:
  python tools/reconciliation_day_audit.py --log-dir "C:/Users/jakej/QTSW2/logs/robot"
  python tools/reconciliation_day_audit.py --log-dir "C:/Users/jakej/QTSW2/logs/robot" --trading-date 2026-04-08
  python tools/reconciliation_day_audit.py ... --trading-date 2026-04-08 --max-incidents 100 --out docs/robot/audits/RECONCILIATION_DAY_AUDIT_AUTO.md
"""
from __future__ import annotations

import argparse
import json
import re
from collections import defaultdict
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

try:
    from zoneinfo import ZoneInfo
except ImportError:
    ZoneInfo = None  # type: ignore

CHICAGO = "America/Chicago"

PREFILTER_SUBSTR = (
    "RECONCILIATION",
    "STATE_CONSISTENCY_GATE",
    "GATE_STALL",
    "RECOVERY_SCOPE",
    "MISMATCH",
    "consistency_gate",
    "FORCED_CONVERGENCE",
    "NO_PROGRESS",
    "JOURNAL_INTEGRITY",
    "JOURNAL_PARITY",
    "JOURNAL_RECONSTRUCTION",
    "RECOVERED_INTENT",
    "RECONCILIATION_RECOVERED",
)


def resolve_log_dir(cli: Optional[str]) -> Path:
    import os

    if cli:
        return Path(cli)
    if os.environ.get("QTSW2_LOG_DIR"):
        return Path(os.environ["QTSW2_LOG_DIR"])
    if os.environ.get("QTSW2_PROJECT_ROOT"):
        return Path(os.environ["QTSW2_PROJECT_ROOT"]) / "logs" / "robot"
    return Path("logs") / "robot"


def parse_ts(s: Any) -> Optional[datetime]:
    if s is None:
        return None
    if isinstance(s, (int, float)):
        return datetime.fromtimestamp(float(s) / 1000.0, tz=timezone.utc)
    s = str(s).strip()
    if not s:
        return None
    try:
        if s.endswith("Z"):
            return datetime.fromisoformat(s.replace("Z", "+00:00"))
        dt = datetime.fromisoformat(s)
        if dt.tzinfo is None:
            dt = dt.replace(tzinfo=timezone.utc)
        return dt.astimezone(timezone.utc)
    except Exception:
        return None


def trading_date_from_obj(obj: Dict[str, Any]) -> str:
    d = obj.get("trading_date")
    if d:
        return str(d).strip()[:10]
    data = obj.get("data")
    if isinstance(data, dict) and data.get("trading_date"):
        return str(data["trading_date"]).strip()[:10]
    return ""


def chicago_date_str(ts: Optional[datetime]) -> str:
    if ts is None or ZoneInfo is None:
        return ""
    try:
        return ts.astimezone(ZoneInfo(CHICAGO)).date().isoformat()
    except Exception:
        return ""


def engine_files(log_dir: Path) -> List[Path]:
    out: List[Path] = []
    for name in ("robot_ENGINE.jsonl",):
        p = log_dir / name
        if p.is_file():
            out.append(p)
    out.extend(sorted(log_dir.glob("robot_ENGINE_*.jsonl")))
    arch = log_dir / "archive"
    if arch.is_dir():
        out.extend(sorted(arch.glob("robot_ENGINE*.jsonl")))
    # Dedupe by resolved path
    seen = set()
    uniq = []
    for p in out:
        r = p.resolve()
        if r not in seen:
            seen.add(r)
            uniq.append(p)
    return uniq


def is_reconciliation_event(ev: str) -> bool:
    if not ev:
        return False
    u = ev.upper()
    if "RECONCILIATION" in u:
        return True
    if "STATE_CONSISTENCY_GATE" in u:
        return True
    if "GATE_STALL" in u:
        return True
    if "RECOVERY_SCOPE" in u:
        return True
    if "MISMATCH" in u and ("RECONCILIATION" in u or "STATE" in u or "GATE" in u):
        return True
    if "CONSISTENCY_GATE" in u:
        return True
    if "FORCED_CONVERGENCE" in u or "FORCED_CONVERGENCE" in ev:
        return True
    if "NO_PROGRESS" in u and ("RECONCILIATION" in u or "GATE" in u or "IEA" in u):
        return True
    if "JOURNAL_INTEGRITY" in u or "JOURNAL_PARITY" in u or "JOURNAL_RECONSTRUCTION" in u:
        return True
    if "RECOVERED_INTENT" in u or "RECONCILIATION_RECOVERED" in u:
        return True
    return False


# High-volume periodic / diagnostic — omit from incident traces (still counted in §2).
NOISE_EVENTS = frozenset(
    {
        "RECONCILIATION_ASSEMBLE_MISMATCH_DIAG",
        "RECONCILIATION_MISMATCH_METRICS",
        "RECONCILIATION_PASS_SUMMARY",
        "RECONCILIATION_SCHEDULER_OUTCOME",
        "RECONCILIATION_CYCLE_STATE",
    }
)


def instrument_from_obj(obj: Dict[str, Any]) -> str:
    ins = str(obj.get("instrument") or "").strip()
    if ins:
        return ins
    data = obj.get("data")
    if isinstance(data, dict):
        ins = str(data.get("instrument") or "").strip()
        if ins:
            return ins
    return ""


def row_matches_trading_day(obj: Dict[str, Any], trading_date: str, ts: Optional[datetime]) -> bool:
    td = trading_date_from_obj(obj)
    if td == trading_date:
        return True
    if not td and chicago_date_str(ts) == trading_date:
        return True
    return False


def iter_reconciliation_lines(log_dir: Path, trading_date: str) -> List[Dict[str, Any]]:
    rows: List[Dict[str, Any]] = []
    for fp in engine_files(log_dir):
        try:
            with fp.open("r", encoding="utf-8", errors="replace") as f:
                for line in f:
                    if not any(s in line for s in PREFILTER_SUBSTR):
                        continue
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        obj = json.loads(line)
                    except json.JSONDecodeError:
                        continue
                    if not isinstance(obj, dict):
                        continue
                    ts = parse_ts(obj.get("ts_utc") or obj.get("ts") or obj.get("timestamp"))
                    if not row_matches_trading_day(obj, trading_date, ts):
                        continue
                    ev = str(obj.get("event") or obj.get("event_type") or "")
                    if not is_reconciliation_event(ev):
                        continue
                    rows.append(
                        {
                            "_file": fp.name,
                            "_ts": ts,
                            "_event": ev,
                            "_instrument": instrument_from_obj(obj),
                            "_run_id": str(obj.get("run_id") or obj.get("data", {}).get("run_id") or ""),
                            "raw": obj,
                        }
                    )
        except OSError:
            continue
    rows.sort(key=lambda r: (r["_ts"] or datetime.min.replace(tzinfo=timezone.utc), r["_file"]))
    return rows


def cluster_incidents(
    rows: List[Dict[str, Any]], gap_minutes: float = 12.0
) -> List[List[Dict[str, Any]]]:
    if not rows:
        return []
    clusters: List[List[Dict[str, Any]]] = []
    cur: List[Dict[str, Any]] = []
    last_ts: Optional[datetime] = None
    last_inst: Optional[str] = None

    for r in rows:
        ts = r["_ts"]
        inst = r["_instrument"] or "__unknown__"
        if not cur:
            cur = [r]
            last_ts = ts
            last_inst = inst
            continue
        gap_min = 9999.0
        if ts is not None and last_ts is not None:
            gap_min = (ts - last_ts).total_seconds() / 60.0
        if gap_min > gap_minutes or inst != last_inst:
            clusters.append(cur)
            cur = [r]
            last_ts = ts
            last_inst = inst
            continue
        cur.append(r)
        last_ts = ts
        last_inst = inst
    if cur:
        clusters.append(cur)
    return clusters


def summarize_cluster(cluster: List[Dict[str, Any]]) -> Dict[str, Any]:
    evs = [r["_event"] for r in cluster]
    insts = {r["_instrument"] for r in cluster if r["_instrument"]}
    run_ids = {r["_run_id"] for r in cluster if r["_run_id"]}
    ts_list = [r["_ts"] for r in cluster if r["_ts"]]
    t0 = min(ts_list) if ts_list else None
    t1 = max(ts_list) if ts_list else None
    return {
        "instruments": sorted(insts),
        "run_ids": sorted(run_ids),
        "start_ts_utc": t0.isoformat() if t0 else None,
        "end_ts_utc": t1.isoformat() if t1 else None,
        "event_count": len(cluster),
        "event_types": _uniq_preserve_order(evs),
    }


def _uniq_preserve_order(evs: List[str]) -> List[str]:
    seen = set()
    out = []
    for e in evs:
        if e not in seen:
            seen.add(e)
            out.append(e)
    return out


def rle_group_spans(cluster: List[Dict[str, Any]]) -> List[Tuple[int, int]]:
    """Index spans [start, end) for consecutive identical (event, instrument) runs."""
    if not cluster:
        return []
    spans: List[Tuple[int, int]] = []
    i = 0
    while i < len(cluster):
        j = i + 1
        while j < len(cluster):
            a, b = cluster[i], cluster[j]
            if b["_event"] == a["_event"] and b["_instrument"] == a["_instrument"]:
                j += 1
            else:
                break
        spans.append((i, j))
        i = j
    return spans


def fmt_run_ids_cell(rids: List[str], max_show: int = 3) -> str:
    if not rids:
        return "—"
    if len(rids) <= max_show:
        return str(rids)
    head = rids[:max_show]
    return f"{head} (+{len(rids) - max_show} more)"


def fmt_compact(s: str, max_len: int = 220) -> str:
    s = s.strip()
    if len(s) <= max_len:
        return s
    return s[: max_len - 3] + "..."


def format_rle_span(cluster: List[Dict[str, Any]], start: int, end: int) -> str:
    run_len = end - start
    r0, r1 = cluster[start], cluster[end - 1]
    ts0, ts1 = r0["_ts"], r1["_ts"]
    ts_s0 = ts0.isoformat() if ts0 else "?"
    inst = r0["_instrument"]
    ev = r0["_event"]
    fn = r0["_file"]
    if run_len == 1:
        return f"- `{ts_s0}` **{ev}** inst=`{inst}` file=`{fn}`\n"
    ts_s1 = ts1.isoformat() if ts1 else "?"
    return (
        f"- `{ts_s0}` → `{ts_s1}` **{ev}** inst=`{inst}` file=`{fn}` **(×{run_len})**\n"
    )


def extract_qty_signals(cluster: List[Dict[str, Any]]) -> Dict[str, Any]:
    """Best-effort from first payload with mismatch/qty fields."""
    for r in cluster:
        o = r["raw"]
        data = o.get("data") if isinstance(o.get("data"), dict) else {}
        merged: Dict[str, Any] = {**data}
        for k in (
            "broker_position_qty",
            "journal_open_qty",
            "journal_qty",
            "account_qty",
            "broker_qty",
            "local_qty",
            "engine_qty",
            "mismatch_type",
            "gate_state",
            "release_ready",
            "skip_reason",
        ):
            if k in o and o[k] is not None:
                merged[k] = o[k]
        if any(x in merged for x in ("broker_position_qty", "journal_open_qty", "broker_qty", "local_qty", "account_qty", "journal_qty")):
            return {"source_event": r["_event"], "fields": {k: merged[k] for k in merged if k in (
                "broker_position_qty", "journal_open_qty", "broker_qty", "local_qty", "account_qty", "journal_qty",
                "mismatch_type", "gate_state", "release_ready", "unexplained_position_qty", "unexplained_working_count",
            )}}
    return {"source_event": None, "fields": {}}


def outcome_guess(cluster: List[Dict[str, Any]]) -> str:
    evs = " ".join(r["_event"] for r in cluster).upper()
    if "MISMATCH_CLEARED" in evs or "RECONCILIATION_MISMATCH_CLEARED" in evs:
        return "resolved_clean_or_cleared_signal"
    if "FAIL_CLOSED" in evs or "MISMATCH_FAIL_CLOSED" in evs:
        return "fail_closed_reported"
    if "FORCED_CONVERGENCE" in evs:
        return "forced_convergence_present"
    if "JOURNAL_INTEGRITY_RECOVERED" in evs or "PARITY_OK" in evs:
        return "integrity_repair_signal"
    return "unknown_outcome_from_event_names"


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--log-dir", type=str, default=None)
    ap.add_argument(
        "--trading-date",
        type=str,
        default=None,
        help="YYYY-MM-DD (Chicago session date). Default: today Chicago.",
    )
    ap.add_argument("--gap-minutes", type=float, default=12.0)
    ap.add_argument("--out", type=str, default=None, help="Write markdown report path")
    ap.add_argument(
        "--max-incidents",
        type=int,
        default=None,
        metavar="N",
        help="Emit at most N incident sections (default: all). §2 counts unchanged.",
    )
    args = ap.parse_args()

    log_dir = resolve_log_dir(args.log_dir)
    if not log_dir.is_dir():
        print(f"ERROR: log dir not found: {log_dir}", flush=True)
        raise SystemExit(2)

    if args.trading_date:
        td = args.trading_date.strip()[:10]
    elif ZoneInfo is not None:
        td = datetime.now(ZoneInfo(CHICAGO)).date().isoformat()
    else:
        td = datetime.now().date().isoformat()

    print(f"log_dir: {log_dir.resolve()}", flush=True)
    print(f"trading_date filter: {td}", flush=True)

    rows = iter_reconciliation_lines(log_dir, td)
    print(f"matching_lines: {len(rows)}", flush=True)

    clusters = cluster_incidents(rows, gap_minutes=args.gap_minutes)
    signal_rows = [r for r in rows if r["_event"] not in NOISE_EVENTS]
    noise_count = sum(1 for r in rows if r["_event"] in NOISE_EVENTS)
    sig_clusters = cluster_incidents(signal_rows, gap_minutes=args.gap_minutes)
    total_sig_clusters = len(sig_clusters)
    if args.max_incidents is not None:
        sig_clusters = sig_clusters[: max(0, args.max_incidents)]

    lines: List[str] = []
    lines.append(f"# Reconciliation audit (log-backed)\n")
    lines.append(f"- **log_dir:** `{log_dir.resolve()}`\n")
    lines.append(f"- **trading_date:** `{td}` (`trading_date` field **or** Chicago date from `ts_utc` when field empty)\n")
    shown = len(sig_clusters)
    cl_note = (
        f"showing **{shown}** of **{total_sig_clusters}**"
        if args.max_incidents is not None and shown < total_sig_clusters
        else f"**{shown}**"
    )
    lines.append(
        f"- **clusters (all events):** {len(clusters)} | **high-signal clusters:** {cl_note} "
        f"(gap > {args.gap_minutes} min or instrument change)\n"
    )
    lines.append("\n## SECTION 1 — Scope\n")
    lines.append("Sources: `robot_ENGINE.jsonl`, `robot_ENGINE_*.jsonl`, `archive/robot_ENGINE*.jsonl`.\n")

    lines.append("\n## SECTION 2 — Raw extraction summary\n")
    by_evt = defaultdict(int)
    for r in rows:
        by_evt[r["_event"]] += 1
    for ev, c in sorted(by_evt.items(), key=lambda x: (-x[1], x[0]))[:60]:
        lines.append(f"- `{ev}`: **{c}**\n")
    if len(by_evt) > 60:
        lines.append(f"- … ({len(by_evt)} distinct event types total)\n")

    noise_types = ", ".join(f"`{e}`" for e in sorted(NOISE_EVENTS))
    lines.append(
        f"\n**Note:** Diagnostic noise ({noise_types}): **{noise_count}** lines "
        f"(omitted from incident traces; still counted in §2).\n"
    )

    lines.append("\n## SECTION 3–4 — Incidents (clustered, high-signal only)\n")
    if args.max_incidents is not None and len(sig_clusters) < total_sig_clusters:
        lines.append(
            f"*Incident sections capped by `--max-incidents {args.max_incidents}` "
            f"({len(sig_clusters)} shown, {total_sig_clusters} total). Re-run without the flag for the full list.*\n\n"
        )
    for i, cl in enumerate(sig_clusters, 1):
        summ = summarize_cluster(cl)
        qty = extract_qty_signals(cl)
        oc = outcome_guess(cl)
        lines.append(f"\n### Incident {i}\n")
        lines.append(f"| Field | Value |\n|---|---|\n")
        lines.append(f"| instruments | {summ['instruments'] or '—'} |\n")
        lines.append(f"| run_ids | {fmt_run_ids_cell(summ['run_ids'])} |\n")
        lines.append(f"| start_ts_utc | {summ['start_ts_utc'] or '—'} |\n")
        lines.append(f"| end_ts_utc | {summ['end_ts_utc'] or '—'} |\n")
        lines.append(f"| events | {summ['event_count']} |\n")
        lines.append(f"| outcome_guess (event names only) | {oc} |\n")
        if qty.get("fields"):
            lines.append(f"| qty snapshot (first with fields) | `{fmt_compact(repr(qty))}` |\n")
        lines.append("\n**Event sequence (chronological, consecutive duplicates collapsed):**\n\n")
        spans = rle_group_spans(cl)
        cap = 80
        for s, e in spans[:cap]:
            lines.append(format_rle_span(cl, s, e))
        if len(spans) > cap:
            lines.append(
                f"- … **{len(spans) - cap}** more RLE group(s) in this cluster "
                f"({len(cl)} raw events; see JSONL by time range)\n"
            )

    lines.append("\n## SECTION 5 — Aggregate\n")
    lines.append(f"- Total matching events: **{len(rows)}** (incl. noise)\n")
    lines.append(f"- High-signal events: **{len(signal_rows)}**\n")
    lines.append(
        f"- Clusters (high-signal): **{total_sig_clusters}**"
        + (
            f" (incident sections emitted: **{len(sig_clusters)}**)"
            if len(sig_clusters) != total_sig_clusters
            else ""
        )
        + "\n"
    )

    lines.append("\n## SECTION 6 — System-level (strict)\n")
    lines.append(
        "Causal classification (partial fill / protective / journal lag / etc.) **not** inferred here — "
        "requires human review of payloads above. Double-count / stall / escalation require payload fields per line.\n"
    )

    lines.append("\n## SECTION 7 — Evidence note\n")
    lines.append(
        "Every row above is tied to a JSON line in the listed file at the given `ts_utc`. "
        "Re-run with same `--trading-date` for reproducibility.\n"
    )

    report = "".join(lines)
    if args.out:
        outp = Path(args.out)
        outp.write_text(report, encoding="utf-8")
        print(f"Wrote {outp.resolve()} ({len(report)} chars)", flush=True)
    else:
        print("\n" + report, flush=True)


if __name__ == "__main__":
    main()
