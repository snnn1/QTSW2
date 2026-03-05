"""
Session Eligibility Freeze — immutable per trading_date.

Written once per trading_date (typically at 18:00 CT). Robot fails closed if missing.
Never overwritten once written.

Author: Quantitative Trading System
"""

import json
import hashlib
import logging
from pathlib import Path
from datetime import datetime, timezone
from typing import Any, Dict, List, Optional

logger = logging.getLogger(__name__)


def load_eligibility(
    trading_date: str,
    output_dir: str = "data/timetable",
) -> Optional[Dict[str, Any]]:
    """
    Load eligibility_{trading_date}.json. Returns None if file does not exist.

    Returns:
        Dict with trading_date, freeze_time_utc, source_matrix_hash, eligible_streams
    """
    path = Path(output_dir) / f"eligibility_{trading_date}.json"
    if not path.exists():
        return None
    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)


def write_eligibility_file(
    streams: List[Dict],
    trading_date: str,
    output_dir: str = "data/timetable",
    source_matrix_hash: Optional[str] = None,
    overwrite: bool = False,
) -> Optional[Path]:
    """
    Write eligibility_<trading_date>.json.

    Args:
        streams: List of stream dicts with stream, enabled, block_reason (optional)
        trading_date: YYYY-MM-DD
        output_dir: Directory for eligibility files
        source_matrix_hash: Optional hash of source matrix
        overwrite: If True, overwrite existing file (e.g. when timetable save is source of truth).
                  If False, never overwrite (builder/resequence runs).

    Returns:
        Path to written file, or None if file already existed and overwrite=False (skipped)
    """
    output_path = Path(output_dir)
    output_path.mkdir(parents=True, exist_ok=True)
    eligibility_path = output_path / f"eligibility_{trading_date}.json"

    if eligibility_path.exists() and not overwrite:
        logger.info(
            f"SESSION_ELIGIBILITY_SKIP: eligibility_{trading_date}.json already exists, "
            "never overwrite (immutable per trading_date)"
        )
        return None

    freeze_time_utc = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
    eligible_streams = []
    for s in streams:
        stream_key = s.get("stream", "").strip()
        if not stream_key:
            continue
        enabled = s.get("enabled", False)
        reason = s.get("block_reason") if not enabled else None
        eligible_streams.append({
            "stream_key": stream_key,
            "enabled": enabled,
            "reason": reason,
        })

    eligible_count = sum(1 for es in eligible_streams if es["enabled"])
    payload = {
        "trading_date": trading_date,
        "freeze_time_utc": freeze_time_utc,
        "matrix_hash": source_matrix_hash,
        "source_matrix_hash": source_matrix_hash,
        "eligible_stream_count": eligible_count,
        "eligible_streams": eligible_streams,
    }

    temp_path = output_path / f"eligibility_{trading_date}.tmp"
    try:
        with open(temp_path, "w", encoding="utf-8") as f:
            json.dump(payload, f, indent=2, ensure_ascii=False)
        temp_path.replace(eligibility_path)
        logger.info(
            f"SESSION_ELIGIBILITY_FROZEN: eligibility_{trading_date}.json written, "
            f"eligible_count={eligible_count}, hash={source_matrix_hash or 'none'}"
        )
        return eligibility_path
    except Exception as e:
        logger.error(f"Failed to write eligibility file: {e}")
        if temp_path.exists():
            try:
                temp_path.unlink()
            except Exception:
                pass
        raise
