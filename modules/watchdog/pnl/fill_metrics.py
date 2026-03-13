"""
Fill Metrics — Phase 4.3 Monitoring

Daily metrics for execution logging hygiene:
- fill_coverage_rate (must be 100%)
- unmapped_rate (target 0)
- null_trading_date_rate (target 0)

Data source: Raw robot logs (robot_*.jsonl in ROBOT_LOGS_DIR).
Scans for EXECUTION_FILLED and EXECUTION_PARTIAL_FILL events.
Event-based anomaly counts (BROKER_FLATTEN_FILL_RECOGNIZED, EXECUTION_UPDATE_UNKNOWN_ORDER_CRITICAL,
EXECUTION_FILL_BLOCKED_TRADING_DATE_NULL, EXECUTION_FILL_UNMAPPED) are aggregated separately
by EventProcessor and merged into fill_health in the aggregator.
"""
import json
from pathlib import Path
from typing import Any, Dict, Optional

from ..config import ROBOT_LOGS_DIR


def compute_fill_metrics(trading_date: str, stream: Optional[str] = None) -> Dict[str, Any]:
    """
    Scan robot logs for EXECUTION_FILLED events and compute daily metrics.

    Returns:
        {
            "trading_date": str,
            "total_fills": int,
            "mapped_fills": int,
            "unmapped_fills": int,
            "null_trading_date_fills": int,
            "fill_coverage_rate": float,  # mapped/total, 1.0 if total=0
            "unmapped_rate": float,       # unmapped/total, 0 if total=0
            "null_trading_date_rate": float,  # null_td/total, 0 if total=0
        }
    """
    total = 0
    mapped = 0
    unmapped = 0
    null_td = 0
    missing_execution_sequence = 0
    missing_fill_group_id = 0

    if not ROBOT_LOGS_DIR.exists():
        return _metrics_result(trading_date, 0, 0, 0, 0, 0, 0)

    # Scan only 15 most recent log files (by mtime) to reduce I/O on large deployments
    all_logs = list(ROBOT_LOGS_DIR.glob("robot_*.jsonl"))
    log_files = sorted(all_logs, key=lambda p: p.stat().st_mtime, reverse=True)[:15]
    for log_file in log_files:
        with open(log_file, "r", encoding="utf-8-sig") as f:
            for line in f:
                if not line.strip():
                    continue
                try:
                    event = json.loads(line)
                    event_type = event.get("event") or event.get("event_type")
                    if event_type not in ("EXECUTION_FILLED", "EXECUTION_PARTIAL_FILL"):
                        continue
                    data = event.get("data") or event
                    event_td = event.get("trading_date") or data.get("trading_date")
                    # Match by trading_date, or infer from ts_utc when trading_date empty
                    ts_utc = event.get("ts_utc") or event.get("timestamp_utc") or data.get("timestamp_utc") or ""
                    if event_td != trading_date:
                        if (not event_td or (isinstance(event_td, str) and not event_td.strip())) and ts_utc:
                            if not (isinstance(ts_utc, str) and ts_utc.startswith(trading_date)):
                                continue
                        else:
                            continue
                    if stream:
                        event_stream = event.get("stream") or data.get("stream")
                        event_instr = event.get("instrument") or data.get("instrument") or data.get("execution_instrument_key")
                        if event_stream and event_instr:
                            from .ledger_builder import canonicalize_stream
                            canonical_stream = canonicalize_stream(event_stream, event_instr)
                        else:
                            canonical_stream = event_stream
                        if canonical_stream != stream:
                            continue
                    total += 1
                    is_mapped = data.get("mapped", True)
                    if is_mapped is False:
                        unmapped += 1
                    else:
                        mapped += 1
                    td_val = data.get("trading_date") or event.get("trading_date")
                    if not td_val or (isinstance(td_val, str) and not td_val.strip()):
                        null_td += 1
                    # P3: Validate execution_sequence and fill_group_id (Phase 4.3)
                    if data.get("execution_sequence") is None:
                        missing_execution_sequence += 1
                    if not data.get("fill_group_id"):
                        missing_fill_group_id += 1
                except Exception:
                    continue

    return _metrics_result(trading_date, total, mapped, unmapped, null_td, missing_execution_sequence, missing_fill_group_id)


def _metrics_result(
    trading_date: str,
    total: int,
    mapped: int,
    unmapped: int,
    null_td: int,
    missing_execution_sequence: int = 0,
    missing_fill_group_id: int = 0,
) -> Dict[str, Any]:
    if total == 0:
        fill_coverage_rate = 1.0
        unmapped_rate = 0.0
        null_trading_date_rate = 0.0
    else:
        fill_coverage_rate = round(mapped / total, 4)
        unmapped_rate = round(unmapped / total, 4)
        null_trading_date_rate = round(null_td / total, 4)
    return {
        "trading_date": trading_date,
        "total_fills": total,
        "mapped_fills": mapped,
        "unmapped_fills": unmapped,
        "null_trading_date_fills": null_td,
        "fill_coverage_rate": fill_coverage_rate,
        "unmapped_rate": unmapped_rate,
        "null_trading_date_rate": null_trading_date_rate,
        "missing_execution_sequence_count": missing_execution_sequence,
        "missing_fill_group_id_count": missing_fill_group_id,
    }
