#!/usr/bin/env python3
"""
Rebuild Ledger From Logs — Audit Switch (Phase 4.2)

Given robot logs → rebuild ledger → identical PnL.

Usage:
  python scripts/rebuild_ledger_from_logs.py --date 2026-03-03
  python scripts/rebuild_ledger_from_logs.py --date 2026-03-03 --out snapshot.json
  python scripts/rebuild_ledger_from_logs.py --date 2026-03-03 --compare snapshot.json

Outputs:
  - Ledger rows (JSON)
  - Hash of PnL (deterministic checksum for audit)
  - Comparison vs snapshot when --compare provided

Exit codes:
  0: Success (hash matches if --compare)
  1: Invariant violation, missing data, or hash mismatch
"""

import argparse
import hashlib
import json
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional

# Add project root to path
project_root = Path(__file__).parent.parent
sys.path.insert(0, str(project_root))

from modules.watchdog.pnl.ledger_builder import LedgerBuilder, LedgerInvariantViolation
from modules.watchdog.pnl.pnl_calculator import (
    compute_intent_realized_pnl,
    aggregate_stream_pnl,
)
from modules.watchdog.pnl.fill_metrics import compute_fill_metrics


def _canonical_pnl_summary(ledger_rows: List[Dict], trading_date: str) -> Dict[str, Any]:
    """
    Build deterministic summary for hashing.
    Hash stability requirements:
    - Sorted rows (by stream, intent_id)
    - Sorted keys in JSON (via sort_keys=True in _compute_hash)
    - Fixed float precision (round to 2 decimals)
    - Integer fields as int (no 1.0 vs 1 variance)
    - No timestamps (avoids formatting variance)
    """
    rows = []
    for r in ledger_rows:
        eq = r.get("entry_qty")
        xq = r.get("exit_qty", 0)
        pnl = r.get("realized_pnl")
        rows.append({
            "intent_id": str(r.get("intent_id", "")),
            "stream": str(r.get("stream", "")),
            "status": str(r.get("status", "")),
            "entry_qty": int(eq) if eq is not None else 0,
            "exit_qty": int(xq) if xq is not None else 0,
            "realized_pnl": round(float(pnl) if pnl is not None else 0, 2),
        })
    rows.sort(key=lambda x: (x["stream"], x["intent_id"]))
    total = sum(r["realized_pnl"] for r in rows)
    return {
        "trading_date": str(trading_date),
        "intent_count": len(rows),
        "realized_pnl_total": round(total, 2),
        "rows": rows,
    }


def _compute_hash(summary: Dict[str, Any]) -> str:
    """
    SHA256 of canonical JSON. Deterministic across runs/locales.
    - sort_keys=True: stable key ordering
    - separators: no whitespace variance
    - ensure_ascii=True: no locale-dependent Unicode
    """
    canonical = json.dumps(summary, sort_keys=True, separators=(",", ":"), ensure_ascii=True)
    return hashlib.sha256(canonical.encode("utf-8")).hexdigest()


def rebuild_ledger(trading_date: str, stream: Optional[str] = None) -> tuple:
    """
    Rebuild ledger from robot logs for given trading date.

    Returns:
        (ledger_rows, summary, pnl_hash)
    """
    builder = LedgerBuilder()
    ledger_rows = builder.build_ledger_rows(trading_date, stream)

    for row in ledger_rows:
        compute_intent_realized_pnl(row)

    summary = _canonical_pnl_summary(ledger_rows, trading_date)
    pnl_hash = _compute_hash(summary)
    return ledger_rows, summary, pnl_hash


def run_with_metrics(trading_date: str, stream: Optional[str] = None) -> tuple:
    """
    Rebuild ledger and compute fill metrics. Returns (ledger_rows, summary, pnl_hash, metrics).
    invariant_violation_count: 0 if ledger builds OK, 1 if LedgerInvariantViolation raised.
    """
    metrics = compute_fill_metrics(trading_date, stream)
    try:
        ledger_rows, summary, pnl_hash = rebuild_ledger(trading_date, stream)
        metrics["invariant_violation_count"] = 0
        return ledger_rows, summary, pnl_hash, metrics
    except LedgerInvariantViolation:
        metrics["invariant_violation_count"] = 1
        raise


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Rebuild ledger from robot logs. Audit switch for deterministic PnL."
    )
    parser.add_argument("--date", required=True, help="Trading date (YYYY-MM-DD)")
    parser.add_argument("--stream", help="Optional stream filter (e.g. NQ1)")
    parser.add_argument(
        "--out",
        help="Write snapshot JSON to path (for future --compare)",
    )
    parser.add_argument(
        "--compare",
        help="Compare against snapshot; exit 1 if hash differs",
    )
    parser.add_argument("--quiet", action="store_true", help="Minimal output")
    parser.add_argument("--metrics", action="store_true", help="Output fill metrics (Phase 4.3)")
    args = parser.parse_args()

    trading_date = args.date
    stream = args.stream or None

    try:
        ledger_rows, summary, pnl_hash, metrics = run_with_metrics(trading_date, stream)
    except LedgerInvariantViolation as e:
        metrics = compute_fill_metrics(trading_date, stream)
        metrics["invariant_violation_count"] = 1
        if args.metrics:
            print(f"METRICS:fill_coverage_rate={metrics['fill_coverage_rate']}")
            print(f"METRICS:unmapped_rate={metrics['unmapped_rate']}")
            print(f"METRICS:null_trading_date_rate={metrics['null_trading_date_rate']}")
            print(f"METRICS:invariant_violation_count=1")
        print(f"LEDGER_INVARIANT_VIOLATION: {e}", file=sys.stderr)
        if hasattr(e, "payload"):
            print(json.dumps(e.payload, indent=2), file=sys.stderr)
        return 1
    except Exception as e:
        print(f"ERROR: {e}", file=sys.stderr)
        return 1

    # Output
    if not args.quiet:
        print(f"REBUILD:trading_date={trading_date}")
        print(f"REBUILD:intent_count={len(ledger_rows)}")
        print(f"REBUILD:realized_pnl_total={summary['realized_pnl_total']}")
        print(f"REBUILD:pnl_hash={pnl_hash}")
        if args.metrics:
            print(f"METRICS:fill_coverage_rate={metrics['fill_coverage_rate']}")
            print(f"METRICS:unmapped_rate={metrics['unmapped_rate']}")
            print(f"METRICS:null_trading_date_rate={metrics['null_trading_date_rate']}")
            print(f"METRICS:invariant_violation_count={metrics['invariant_violation_count']}")

    if args.compare:
        compare_path = Path(args.compare)
        if not compare_path.exists():
            print(f"ERROR: Compare file not found: {compare_path}", file=sys.stderr)
            return 1
        with open(compare_path, "r") as f:
            prev = json.load(f)
        prev_hash = prev.get("pnl_hash", "")
        if prev_hash != pnl_hash:
            print(f"AUDIT:FAIL hash_mismatch expected={prev_hash} actual={pnl_hash}", file=sys.stderr)
            return 1
        if not args.quiet:
            print("AUDIT:PASS hash matches snapshot")

    # Write snapshot if requested
    if args.out:
        out_path = Path(args.out)
        snapshot = {
            "trading_date": trading_date,
            "stream": stream,
            "pnl_hash": pnl_hash,
            "realized_pnl_total": summary["realized_pnl_total"],
            "intent_count": len(ledger_rows),
            "summary": summary,
            "ledger_rows": ledger_rows,
        }
        if args.metrics:
            snapshot["metrics"] = metrics
        with open(out_path, "w") as f:
            json.dump(snapshot, f, indent=2)
        if not args.quiet:
            print(f"REBUILD:snapshot_written={out_path}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
