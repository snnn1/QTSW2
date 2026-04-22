"""
Persisted SessionAuthority (Model A: strict — publishers require a valid on-disk file).

Synthetic GET observation does not satisfy publish requirements.
"""

from __future__ import annotations

import json
import logging
import re
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, Optional

from modules.session_authority.models import SessionAuthorityState
from modules.session_authority.observability import session_authority_path

logger = logging.getLogger(__name__)

_YMD = re.compile(r"^\d{4}-\d{2}-\d{2}$")


class SessionAuthorityRequiredError(RuntimeError):
    """Raised when live publish requires persisted authority and none is valid."""


def _read_json(path: Path) -> Optional[dict]:
    try:
        raw = path.read_text(encoding="utf-8")
        data = json.loads(raw)
        return data if isinstance(data, dict) else None
    except (OSError, json.JSONDecodeError):
        return None


def _parse_iso_utc(s: str) -> Optional[datetime]:
    raw = (s or "").strip()
    if not raw:
        return None
    if raw.endswith("Z"):
        raw = raw[:-1] + "+00:00"
    try:
        dt = datetime.fromisoformat(raw)
        if dt.tzinfo is None:
            dt = dt.replace(tzinfo=timezone.utc)
        return dt.astimezone(timezone.utc)
    except Exception:
        return None


def _authority_expired(state: SessionAuthorityState, utc_now: datetime) -> bool:
    exp_raw = state.expires_at_utc
    if not exp_raw or not str(exp_raw).strip():
        return False
    exp = _parse_iso_utc(str(exp_raw))
    if exp is None:
        return False
    return utc_now > exp


def read_next_version(project_root: Path) -> int:
    path = session_authority_path(project_root)
    raw = _read_json(path)
    if not raw:
        return 1
    v = raw.get("version")
    if isinstance(v, int) and v >= 0:
        return v + 1
    return 1


def load_persisted_strict(project_root: Path) -> SessionAuthorityState:
    """
    Load valid, non-expired SessionAuthority from disk.

    Raises SessionAuthorityRequiredError if missing, invalid JSON, schema-invalid, or expired.
    """
    path = session_authority_path(project_root)
    utc_now = datetime.now(timezone.utc)
    if not path.is_file():
        raise SessionAuthorityRequiredError(
            "SESSION_AUTHORITY_REQUIRED: no persisted data/session/session_authority.json. "
            "Call POST /api/session/authority/initialize_auto or set authority before publish."
        )
    raw = _read_json(path)
    if raw is None:
        raise SessionAuthorityRequiredError(
            f"SESSION_AUTHORITY_REQUIRED: unreadable or invalid JSON at {path}"
        )
    try:
        state = SessionAuthorityState.model_validate(raw)
    except Exception as e:
        raise SessionAuthorityRequiredError(
            f"SESSION_AUTHORITY_REQUIRED: invalid session authority document: {e}"
        ) from e
    if _authority_expired(state, utc_now):
        raise SessionAuthorityRequiredError(
            "SESSION_AUTHORITY_REQUIRED: persisted session authority has expired (expires_at_utc). "
            "Re-initialize or set a new authority."
        )
    return state


def save_authority(project_root: Path, state: SessionAuthorityState) -> None:
    """Atomic write of session authority JSON."""
    path = session_authority_path(project_root)
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(".tmp")
    payload = state.model_dump(mode="json", exclude_none=True)
    with open(tmp, "w", encoding="utf-8") as wf:
        json.dump(payload, wf, indent=2, ensure_ascii=False)
    tmp.replace(path)
    logger.info(
        "SESSION_AUTHORITY_PERSISTED mode=%s session=%s version=%s path=%s",
        state.mode,
        state.session_trading_date,
        state.version,
        path,
    )


def build_persisted_authority_state(
    *,
    project_root: Path,
    mode: str,
    session_trading_date: str,
    source: str,
    locked: bool,
    set_by: str,
    reason: str,
    expected_canonical_session: Optional[str] = None,
    metadata: Optional[Dict[str, Any]] = None,
    expires_at_utc: Optional[str] = None,
) -> SessionAuthorityState:
    """Build a new persisted authority state with the next monotonic version."""
    utc_now = datetime.now(timezone.utc)
    return SessionAuthorityState(
        mode=mode,
        session_trading_date=session_trading_date,
        source=source,
        locked=locked,
        set_at_utc=utc_now.isoformat().replace("+00:00", "Z"),
        set_by=set_by,
        reason=reason,
        version=read_next_version(project_root),
        expires_at_utc=expires_at_utc,
        expected_canonical_session=expected_canonical_session,
        metadata=dict(metadata) if metadata else None,
    )


def build_auto_authority_from_canonical(
    *,
    project_root: Path,
    set_by: str,
    reason: str,
) -> SessionAuthorityState:
    from modules.timetable.cme_session import get_cme_trading_date

    utc_now = datetime.now(timezone.utc)
    canonical = get_cme_trading_date(utc_now)
    return build_persisted_authority_state(
        project_root=project_root,
        mode="auto",
        session_trading_date=canonical,
        source="system",
        locked=False,
        set_by=set_by,
        reason=reason,
        expected_canonical_session=canonical,
        metadata=None,
    )


def initialize_auto_authority(project_root: Path, *, set_by: str = "POST /api/session/authority/initialize_auto") -> SessionAuthorityState:
    """Write persisted auto mode from canonical CME (explicit bootstrap)."""
    st = build_auto_authority_from_canonical(
        project_root=project_root,
        set_by=set_by,
        reason="initialize_auto",
    )
    save_authority(project_root, st)
    logger.info("SESSION_AUTHORITY_INITIALIZED_AUTO session=%s", st.session_trading_date)
    return st
