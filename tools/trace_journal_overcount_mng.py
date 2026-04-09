#!/usr/bin/env python3
"""
Trace source of journal overcount (+1 vs broker) for sample MNG mismatch episodes.

Reads JSONL for trading_date, selects episodes matching numerical audit criteria,
dumps 50–100 events before first mismatch per sample, scans for fills / duplicates / journal signals.

Usage:
  python tools/trace_journal_overcount_mng.py --log-dir "C:/Users/jakej/QTSW2/logs/robot" \\
    --trading-date 2026-04-08 --out docs/robot/audits/TRACE_JOURNAL_OVERCOUNT_MNG_2026-04-08.md
"""
from __future__ import annotations

import argparse
import bisect
import json
import re
import sys
from collections import Counter, defaultdict
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

_TOOLS = Path(__file__).resolve().parent
if str(_TOOLS) not in sys.path:
    sys.path.insert(0, str(_TOOLS))
import reconciliation_day_audit as rea  # noqa: E402
import reconciliation_numerical_mismatch_audit as num  # noqa: E402

# Import episode clustering from numerical module logic inline
EPISODE_GAP_SEC = num.EPISODE_GAP_SEC


def load_all_day_rows(log_dir: Path, trading_date: str) -> List[Dict[str, Any]]:
    """All JSON objects for the trading day with ts, sorted."""
    rows: List[Dict[str, Any]] = []
    for fp in rea.engine_files(log_dir):
        try:
            with fp.open("r", encoding="utf-8", errors="replace") as f:
                for line in f:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        obj = json.loads(line)
                    except json.JSONDecodeError:
                        continue
                    if not isinstance(obj, dict):
                        continue
                    ts = rea.parse_ts(obj.get("ts_utc") or obj.get("ts") or obj.get("timestamp"))
                    if not rea.row_matches_trading_day(obj, trading_date, ts):
                        continue
                    rows.append({"_ts": ts, "_file": fp.name, "raw": obj})
        except OSError:
            continue
    rows.sort(key=lambda r: (r["_ts"] or datetime.min.replace(tzinfo=timezone.utc), r["_file"]))
    return rows


def ts_key(r: Dict[str, Any]) -> datetime:
    return r["_ts"] or datetime.min.replace(tzinfo=timezone.utc)


def window_before_ts(sorted_rows: List[Dict[str, Any]], t: datetime, n: int) -> List[Dict[str, Any]]:
    """Last n rows strictly before timestamp t (first mismatch instant)."""
    keys = [ts_key(r) for r in sorted_rows]
    i = bisect.bisect_left(keys, t)
    start = max(0, i - n)
    return sorted_rows[start:i]


def extract_ids(blob: str) -> Dict[str, List[str]]:
    """Lightweight id harvest for duplication hints."""
    out: Dict[str, List[str]] = defaultdict(list)
    for pat, name in (
        (r'"execution_id"\s*:\s*"([^"]+)"', "execution_id"),
        (r'"ExecutionId"\s*:\s*"([^"]+)"', "ExecutionId"),
        (r'"fill_key"\s*:\s*"([^"]+)"', "fill_key"),
        (r'"intent_id"\s*:\s*"([^"]+)"', "intent_id"),
        (r'"order_id"\s*:\s*"([^"]+)"', "order_id"),
    ):
        for m in re.finditer(pat, blob, re.I):
            out[name].append(m.group(1))
    return dict(out)


def analyze_window(rows_slice: List[Dict[str, Any]]) -> Dict[str, Any]:
    event_counts = Counter()
    fill_like = 0
    journal_like = 0
    exit_like = 0
    dup_hints: Counter = Counter()
    id_occurrences: Dict[str, Counter] = defaultdict(Counter)

    fill_tokens = (
        "EXECUTION_FILLED",
        "EXECUTION_PARTIAL_FILL",
        "ORDER_FILLED",
        "FILL",
        "FLATTEN_FILL",
        "INTENT_EXIT_FILL",
        "UNTRACKED_FILL",
        "ADOPTED_ORDER_FILL",
    )
    journal_tokens = ("JOURNAL", "journal_qty", "ExecutionJournal", "RecordJournal")
    exit_tokens = ("EXIT", "FLATTEN", "close", "CLOSED", "reduce")

    for r in rows_slice:
        obj = r["raw"]
        ev = num.event_name(obj).upper()
        event_counts[ev] += 1
        blob = json.dumps(obj, default=str)
        if any(x in ev for x in fill_tokens) or "FILL" in ev:
            fill_like += 1
        if any(t in blob.upper() for t in ("JOURNAL", "journal_qty")):
            journal_like += 1
        if any(x in ev for x in exit_tokens):
            exit_like += 1
        ids = extract_ids(blob)
        for k, vals in ids.items():
            for v in vals:
                id_occurrences[k][v] += 1

    for k, ctr in id_occurrences.items():
        for v, c in ctr.items():
            if c >= 2:
                short = v[:40] + ("…" if len(v) > 40 else "")
                dup_hints[f"{k}:{short}"] = c

    return {
        "event_counts_top": event_counts.most_common(25),
        "fill_like_lines": fill_like,
        "journal_keyword_lines": journal_like,
        "exit_flatten_like_lines": exit_like,
        "duplicate_id_hints": dup_hints,
    }


def classify_episode(first: Dict[str, Any], last: Dict[str, Any], outcome: str) -> str:
    return num.classify_episode(first, last, outcome)


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--log-dir", type=str, default=None)
    ap.add_argument("--trading-date", type=str, required=True)
    ap.add_argument("--out", type=str, required=True)
    ap.add_argument("--window", type=int, default=100, help="Events before first mismatch")
    ap.add_argument("--samples", type=int, default=5)
    args = ap.parse_args()

    log_dir = rea.resolve_log_dir(args.log_dir)
    td = args.trading_date.strip()[:10]

    raw_lines = num.iter_engine_lines(log_dir, td)
    mismatch_rows: List[Dict[str, Any]] = []
    for r in raw_lines:
        nr = num.normalize_row(r)
        if nr:
            mismatch_rows.append(nr)

    episodes = num.cluster_episodes(mismatch_rows)

    timeline: List[Dict[str, Any]] = []
    for r in raw_lines:
        obj = r["raw"]
        ts = r["_ts"]
        ev = num.event_name(obj).upper()
        ins = num.instrument_from_obj(obj)
        if ev in ("RECONCILIATION_CONVERGED", "RECONCILIATION_STUCK") and ins:
            timeline.append({"_ts": ts, "raw": obj})
    timeline.sort(key=lambda x: (x["_ts"] or datetime.min.replace(tzinfo=timezone.utc),))

    # Outcome per episode
    enriched: List[Tuple[Dict[str, Any], Dict[str, Any], str, str]] = []
    for ep in episodes:
        if not ep:
            continue
        first = ep[0]
        last = ep[-1]
        after_last = last["ts"] or datetime.min.replace(tzinfo=timezone.utc)
        horizon_end = after_last + timedelta(seconds=num.OUTCOME_HORIZON_SEC)
        outcome = num.find_outcome_after(timeline, first["instrument"], after_last, horizon_end)
        cls = classify_episode(first, last, outcome)
        enriched.append((first, last, outcome, cls))

    # Filter: MNG, delta=-1, JOURNAL_HIGH
    candidates = [
        (f, l, o, c)
        for f, l, o, c in enriched
        if f["instrument"].upper() == "MNG"
        and f["delta"] == -1
        and c == "PERSISTENT_MISMATCH_JOURNAL_HIGH"
    ]

    # Spread samples across time
    n = args.samples
    if len(candidates) < n:
        chosen = candidates
    else:
        step = max(1, len(candidates) // n)
        chosen = [candidates[i * step] for i in range(n)]

    all_rows = load_all_day_rows(log_dir, td)

    lines_out: List[str] = []
    lines_out.append("# Trace: journal overcount (+1 vs broker) — MNG samples\n\n")
    lines_out.append(f"- **log_dir:** `{log_dir.resolve()}`\n")
    lines_out.append(f"- **trading_date:** `{td}`\n")
    lines_out.append(
        f"- **Selection:** `instrument=MNG`, `delta=-1` (journal = account+1), "
        f"`classification=PERSISTENT_MISMATCH_JOURNAL_HIGH`, **{len(chosen)}** episodes.\n"
    )
    lines_out.append(
        f"- **Back-window:** up to **{args.window}** log lines immediately **before** first mismatch timestamp.\n\n"
    )

    lines_out.append("## Method (log-backed limits)\n\n")
    lines_out.append(
        "- **Broker vs journal counts:** derived from mismatch row `account_qty` / `journal_qty` only; "
        "full fill ledger reconciliation requires order/fill exports outside this JSONL.\n"
    )
    lines_out.append(
        "- **Fill signal:** lines whose `event` contains fill-related tokens (see per-case histogram).\n"
    )
    lines_out.append(
        "- **Duplication:** repeated `execution_id` / `fill_key` / `intent_id` substrings in the same back-window.\n\n"
    )

    for i, (first, _last, outcome, cls) in enumerate(chosen, 1):
        t0 = first["ts"]
        if t0 is None:
            lines_out.append(f"## Case {i} — UNKNOWN (no timestamp)\n\n")
            continue

        window = window_before_ts(all_rows, t0, args.window)

        an = analyze_window(window)
        lines_out.append(f"## Case {i}\n\n")
        lines_out.append(f"- **instrument:** `{first['instrument']}`\n")
        lines_out.append(f"- **run_id:** `{first['run_id'] or 'UNKNOWN'}`\n")
        lines_out.append(f"- **first mismatch (episode):** `{t0.isoformat()}`\n")
        lines_out.append(
            f"- **snapshot at mismatch:** account_qty={first['account_qty']} journal_qty={first['journal_qty']} "
            f"delta={first['delta']} outcome={outcome} class={cls}\n\n"
        )

        lines_out.append("### Back-window summary\n\n")
        lines_out.append(f"- **lines in window:** {len(window)} (requested max {args.window})\n")
        lines_out.append(f"- **fill-like event lines (heuristic):** {an['fill_like_lines']}\n")
        lines_out.append(f"- **lines mentioning journal keywords:** {an['journal_keyword_lines']}\n")
        lines_out.append(f"- **exit/flatten-like event lines (heuristic):** {an['exit_flatten_like_lines']}\n\n")

        lines_out.append("**Top event types in window:**\n\n")
        for ev, c in an["event_counts_top"][:20]:
            lines_out.append(f"- `{ev}`: {c}\n")
        lines_out.append("\n**Duplicate id hints (same id appears ≥2× in window):**\n\n")
        if an["duplicate_id_hints"]:
            for k, c in an["duplicate_id_hints"].most_common(15):
                lines_out.append(f"- `{k}`: **{c}**\n")
        else:
            lines_out.append("- *(none in window)*\n")

        lines_out.append("\n### Sections 3–6 (answers)\n\n")
        lines_out.append(
            f"- **Expected broker position (from mismatch row):** **{first['account_qty']}** contracts (abs total in reconciliation snapshot).\n"
        )
        lines_out.append(
            f"- **Journal open qty (from mismatch row):** **{first['journal_qty']}** → **+1** vs broker in this row.\n"
        )
        lines_out.append(
            "- **Did journal record one extra fill?** "
            "Cannot prove from qty fields alone; window shows fill-related activity counts above. "
            "If `duplicate id hints` is non-empty, investigate those ids for double-processing.\n"
        )
        lines_out.append(
            "- **Exit/reduction gap:** If broker closed a lot but journal stayed high, look for missing "
            "`INTENT_EXIT_FILL` / flatten resolution in the window; heuristics counted exit-like lines above.\n"
        )
        lines_out.append(
            f"- **Discrepancy explanation (evidence-only):** Journal **{first['journal_qty']}** vs account **{first['account_qty']}** "
            f"at mismatch time; causal root requires correlating fills with journal writes (not fully present in sampled ENGINE lines).\n\n"
        )

        lines_out.append("<details><summary>Raw back-window event sequence (newest last)</summary>\n\n")
        for r in window[-40:]:
            ts = r["_ts"]
            ev = num.event_name(r["raw"])
            ts_s = ts.isoformat() if ts else "?"
            lines_out.append(f"- `{ts_s}` **{ev}** `{r['_file']}`\n")
        if len(window) > 40:
            lines_out.append(f"- … **{len(window) - 40}** more lines\n")
        lines_out.append("\n</details>\n\n")

    Path(args.out).parent.mkdir(parents=True, exist_ok=True)
    Path(args.out).write_text("".join(lines_out), encoding="utf-8")
    print(f"Wrote {args.out} cases={len(chosen)} candidates_total={len(candidates)}", flush=True)


if __name__ == "__main__":
    main()
