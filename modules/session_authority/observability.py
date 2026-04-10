"""Load persisted SessionAuthority or synthesize read-only canonical observation (Phase 1)."""

from __future__ import annotations

import json
import logging
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, Optional

from modules.session_authority.models import SessionAuthorityObservation, SessionAuthorityState
from modules.session_authority.policy import NO_FILE_OBSERVABILITY_MODE

logger = logging.getLogger(__name__)

DEFAULT_RELATIVE_PATH = Path("data") / "session" / "session_authority.json"


def session_authority_path(project_root: Path) -> Path:
    return Path(project_root).resolve() / DEFAULT_RELATIVE_PATH


def _read_persisted_json(path: Path) -> Optional[dict]:
    if not path.is_file():
        return None
    try:
        raw = path.read_text(encoding="utf-8")
        data = json.loads(raw)
        return data if isinstance(data, dict) else None
    except (OSError, json.JSONDecodeError) as e:
        logger.warning("SESSION_AUTHORITY_LOAD_FAILED path=%s error=%s", path, e)
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
        logger.warning(
            "SESSION_AUTHORITY_EXPIRY_UNPARSEABLE expires_at_utc=%r — ignoring expiry",
            exp_raw,
        )
        return False
    return utc_now > exp


def _synthetic_state(
    *,
    canonical: str,
    utc_now: datetime,
    reason: str,
    metadata: Optional[Dict[str, Any]],
) -> SessionAuthorityState:
    base_meta: Dict[str, Any] = dict(metadata or {})
    return SessionAuthorityState(
        mode="auto",
        session_trading_date=canonical,
        source="system",
        locked=False,
        set_at_utc=utc_now.isoformat().replace("+00:00", "Z"),
        set_by="phase1_observability",
        reason=reason,
        version=0,
        expected_canonical_session=canonical,
        metadata=base_meta or None,
    )


def get_session_authority_observation(project_root: Path) -> SessionAuthorityObservation:
    """
    Phase 1: read-only observability.

    - Valid persisted file → observation_reason=loaded
    - Missing file → synthetic_no_file (canonical CME auto); **does not create file** (see policy.py)
    - Invalid JSON / schema → synthetic_invalid_persisted
    - Valid schema but expires_at_utc in the past → synthetic_expired
    """
    root = Path(project_root).resolve()
    path = session_authority_path(root)
    utc_now = datetime.now(timezone.utc)
    from modules.timetable.cme_session import get_cme_trading_date

    canonical = get_cme_trading_date(utc_now)
    file_present = path.is_file()

    raw = _read_persisted_json(path)
    if raw is not None:
        try:
            state = SessionAuthorityState.model_validate(raw)
            if _authority_expired(state, utc_now):
                logger.warning(
                    "SESSION_AUTHORITY_EXPIRED path=%s expires_at_utc=%s — falling back to synthetic",
                    path,
                    state.expires_at_utc,
                )
                st = _synthetic_state(
                    canonical=canonical,
                    utc_now=utc_now,
                    reason="authority_expired",
                    metadata={
                        "note": "Persisted authority expired; synthetic canonical shown.",
                        "expired_authority_session": state.session_trading_date,
                        "expires_at_utc": state.expires_at_utc,
                    },
                )
                obs = SessionAuthorityObservation(
                    observation_reason="synthetic_expired",
                    persisted=False,
                    synthetic=True,
                    authority_file_present=True,
                    no_authority_file_policy=NO_FILE_OBSERVABILITY_MODE,
                    canonical_cme_session=canonical,
                    authority_path=str(path),
                    state=st,
                )
                logger.info(
                    "SESSION_AUTHORITY_OBSERVED reason=synthetic_expired persisted=0 file_present=1 session=%s canonical=%s",
                    st.session_trading_date,
                    canonical,
                )
                return obs

            obs = SessionAuthorityObservation(
                observation_reason="loaded",
                persisted=True,
                synthetic=False,
                authority_file_present=True,
                no_authority_file_policy=NO_FILE_OBSERVABILITY_MODE,
                canonical_cme_session=canonical,
                authority_path=str(path),
                state=state,
            )
            logger.info(
                "SESSION_AUTHORITY_OBSERVED reason=loaded persisted=1 session=%s mode=%s version=%s path=%s",
                state.session_trading_date,
                state.mode,
                state.version,
                path,
            )
            return obs
        except Exception as e:
            logger.warning(
                "SESSION_AUTHORITY_INVALID persisted_file=1 error=%s path=%s — falling back to synthetic",
                e,
                path,
            )
            st = _synthetic_state(
                canonical=canonical,
                utc_now=utc_now,
                reason="invalid_persisted",
                metadata={
                    "note": "session_authority.json present but invalid; synthetic canonical shown.",
                    "error": str(e)[:500],
                },
            )
            obs = SessionAuthorityObservation(
                observation_reason="synthetic_invalid_persisted",
                persisted=False,
                synthetic=True,
                authority_file_present=file_present,
                no_authority_file_policy=NO_FILE_OBSERVABILITY_MODE,
                canonical_cme_session=canonical,
                authority_path=str(path),
                state=st,
            )
            logger.info(
                "SESSION_AUTHORITY_OBSERVED reason=synthetic_invalid_persisted persisted=0 file_present=%s session=%s",
                int(file_present),
                st.session_trading_date,
            )
            return obs

    # No readable dict (missing or JSON error)
    if not file_present:
        meta_note = "No session_authority.json yet; canonical session shown only."
        obs_reason = "synthetic_no_file"
    else:
        meta_note = "session_authority.json unreadable; canonical session shown only."
        obs_reason = "synthetic_invalid_persisted"

    st = _synthetic_state(
        canonical=canonical,
        utc_now=utc_now,
        reason="not_persisted",
        metadata={"note": meta_note},
    )
    obs = SessionAuthorityObservation(
        observation_reason=obs_reason,
        persisted=False,
        synthetic=True,
        authority_file_present=file_present,
        no_authority_file_policy=NO_FILE_OBSERVABILITY_MODE,
        canonical_cme_session=canonical,
        authority_path=str(path),
        state=st,
    )
    logger.info(
        "SESSION_AUTHORITY_OBSERVED reason=%s persisted=0 file_present=%s session=%s canonical=%s path=%s",
        obs.observation_reason,
        int(file_present),
        st.session_trading_date,
        canonical,
        path,
    )
    return obs
