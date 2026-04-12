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

try:
    from zoneinfo import ZoneInfo
except ImportError:  # Python < 3.9
    ZoneInfo = None  # type: ignore

logger = logging.getLogger(__name__)

# Repo root: modules/timetable/eligibility_writer.py -> parents[2] == QTSW2
_REPO_ROOT = Path(__file__).resolve().parents[2]


def _append_trading_date_roll_journal(
    *,
    ts_utc: datetime,
    old_trading_date: Optional[str],
    new_trading_date: str,
    source: str,
) -> None:
    """Append one JSON line to logs/trading_date_roll_journal.jsonl (canonical audit)."""
    try:
        journal_path = _REPO_ROOT / "logs" / "trading_date_roll_journal.jsonl"
        journal_path.parent.mkdir(parents=True, exist_ok=True)
        if ts_utc.tzinfo is None:
            ts_utc = ts_utc.replace(tzinfo=timezone.utc)
        ts_utc_iso = ts_utc.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")
        if ZoneInfo is not None:
            ts_chicago = ts_utc.astimezone(ZoneInfo("America/Chicago")).isoformat()
        else:
            ts_chicago = ""
        line = {
            "event": "TRADING_DATE_ROLLED",
            "ts_utc": ts_utc_iso,
            "ts_chicago": ts_chicago,
            "old_trading_date": old_trading_date,
            "new_trading_date": new_trading_date,
            "source": source,
        }
        with open(journal_path, "a", encoding="utf-8") as f:
            f.write(json.dumps(line, ensure_ascii=False) + "\n")
    except Exception as ex:
        logger.warning("TRADING_DATE_ROLLED journal append failed: %s", ex)


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


class EligibilityWriteBlockedCmeMismatch(RuntimeError):
    """Live execution: eligibility session_trading_date must equal get_cme_trading_date(now)."""


def write_eligibility_file(
    streams: List[Dict],
    session_trading_date: str,
    output_dir: str = "data/timetable",
    source_matrix_hash: Optional[str] = None,
    overwrite: bool = False,
    *,
    bypass_session_immutability_guard: bool = False,
    bypass_audit_operator: Optional[str] = None,
    bypass_audit_source: Optional[str] = None,
    bypass_audit_reason: Optional[str] = None,
    enforce_cme_live_session: bool = False,
) -> Optional[Path]:
    """
    Write eligibility_<session_trading_date>.json.

    Args:
        streams: List of stream dicts with stream, enabled, block_reason (optional)
        session_trading_date: YYYY-MM-DD (CME session date)
        output_dir: Directory for eligibility files
        source_matrix_hash: Optional hash of source matrix
        overwrite: If True, overwrite existing file (e.g. when timetable save is source of truth).
                  If False, never overwrite (builder/resequence runs).
        bypass_session_immutability_guard: Emergency only; skips post-session-open overwrite block.
        bypass_audit_operator / bypass_audit_source /         bypass_audit_reason: Required for audit when bypass is used.
        enforce_cme_live_session: If True, session_trading_date must match get_cme_trading_date(UTC now).

    Returns:
        Path to written file, or None if file already existed and overwrite=False (skipped)
    """
    session_trading_date = (session_trading_date or "").strip()
    if enforce_cme_live_session:
        from modules.timetable.cme_session import get_cme_trading_date

        expected = get_cme_trading_date(datetime.now(timezone.utc))
        if session_trading_date != expected:
            logger.error(
                "ELIGIBILITY_WRITE_BLOCKED_CME_MISMATCH: session_trading_date=%s expected_cme=%s",
                session_trading_date,
                expected,
            )
            raise EligibilityWriteBlockedCmeMismatch(
                f"live execution eligibility session_trading_date must be {expected}, got {session_trading_date}"
            )

    output_path = Path(output_dir)
    output_path.mkdir(parents=True, exist_ok=True)
    eligibility_path = output_path / f"eligibility_{session_trading_date}.json"

    if overwrite and eligibility_path.exists():
        from modules.timetable.eligibility_session_policy import assert_eligibility_overwrite_allowed

        if bypass_session_immutability_guard:
            logger.critical(
                json.dumps(
                    {
                        "event": "ELIGIBILITY_IMMUTABILITY_BYPASS",
                        "severity": "CRITICAL",
                        "session_trading_date": session_trading_date,
                        "output_dir": str(output_path.resolve()),
                        "operator": bypass_audit_operator or "unspecified",
                        "source": bypass_audit_source or "unspecified",
                        "reason": bypass_audit_reason or "unspecified",
                        "note": "Session-open immutability guard skipped (bypass_session_immutability_guard=True)",
                    },
                    ensure_ascii=False,
                )
            )
        assert_eligibility_overwrite_allowed(
            session_trading_date,
            bypass_session_immutability_guard=bypass_session_immutability_guard,
        )

    if eligibility_path.exists() and not overwrite:
        logger.info(
            f"SESSION_ELIGIBILITY_SKIP: eligibility_{session_trading_date}.json already exists, "
            "never overwrite (immutable per session_trading_date)"
        )
        return None

    freeze_dt_utc = datetime.now(timezone.utc)
    freeze_time_utc = freeze_dt_utc.isoformat().replace("+00:00", "Z")
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
        "session_trading_date": session_trading_date,
        "freeze_time_utc": freeze_time_utc,
        "matrix_hash": source_matrix_hash,
        "source_matrix_hash": source_matrix_hash,
        "eligible_stream_count": eligible_count,
        "eligible_streams": eligible_streams,
    }

    temp_path = output_path / f"eligibility_{session_trading_date}.tmp"
    try:
        with open(temp_path, "w", encoding="utf-8") as f:
            json.dump(payload, f, indent=2, ensure_ascii=False)
        temp_path.replace(eligibility_path)
        logger.info(
            f"SESSION_ELIGIBILITY_FROZEN: eligibility_{session_trading_date}.json written, "
            f"eligible_count={eligible_count}, hash={source_matrix_hash or 'none'}"
        )
        _append_trading_date_roll_journal(
            ts_utc=freeze_dt_utc,
            old_trading_date=None,
            new_trading_date=session_trading_date,
            source="eligibility",
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
