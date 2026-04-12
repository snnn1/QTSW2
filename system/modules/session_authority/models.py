"""SessionAuthority control-plane state (Phase 1: model + validation only)."""

from __future__ import annotations

import re
from typing import Any, Dict, Literal, Optional

from pydantic import BaseModel, ConfigDict, Field, field_validator

_MODE = Literal["auto", "manual", "replay", "rollback_override"]
_SOURCE = Literal["system", "user", "replay", "rollback", "recovery"]

# Why this observation was produced (manual testing + drift tooling).
_OBSERVATION_REASON = Literal[
    "loaded",
    "synthetic_no_file",
    "synthetic_invalid_persisted",
    "synthetic_expired",
]

_YMD = re.compile(r"^\d{4}-\d{2}-\d{2}$")


def _validate_ymd(name: str, v: Optional[str]) -> Optional[str]:
    if v is None:
        return None
    s = str(v).strip()
    if not s:
        return None
    if not _YMD.match(s):
        raise ValueError(f"{name} must be YYYY-MM-DD, got {v!r}")
    return s


class SessionAuthorityState(BaseModel):
    """
    Authoritative session state (persisted when Phase 2+ writers exist).
    Phase 1: used for validation and read-only GET; synthetic rows use version=0.
    """

    model_config = ConfigDict(extra="ignore")

    mode: _MODE
    session_trading_date: str = Field(..., description="Active CME session calendar day YYYY-MM-DD")
    source: _SOURCE
    locked: bool
    set_at_utc: Optional[str] = Field(None, description="ISO-8601 UTC when state was established")
    set_by: str = Field(..., description="Actor or subsystem name")
    reason: str = Field(..., description="machine-oriented reason key")
    version: int = Field(0, ge=0, description="Monotonic revision")
    expires_at_utc: Optional[str] = Field(None, description="Optional expiry for temporary overrides")
    expected_canonical_session: Optional[str] = Field(
        None,
        description="Optional expected canonical CME session for drift monitoring",
    )
    metadata: Optional[Dict[str, Any]] = Field(None, description="Additional structured context")

    @field_validator("session_trading_date", "expected_canonical_session")
    @classmethod
    def _ymd(cls, v: Optional[str]) -> Optional[str]:
        if v is None:
            return None
        out = _validate_ymd("session_trading_date", v)
        assert out is not None
        return out


class SessionAuthorityObservation(BaseModel):
    """Read-only view returned by GET /api/session/authority (Phase 1)."""

    observation_reason: _OBSERVATION_REASON = Field(
        ...,
        description="How this observation was produced (loaded vs synthetic fallback)",
    )
    persisted: bool = Field(
        ...,
        description="True if persisted JSON was successfully validated and used for state",
    )
    synthetic: bool = Field(
        ...,
        description="True if state was synthesized (fallback or no file)",
    )
    authority_file_present: bool = Field(
        ...,
        description="True if data/session/session_authority.json exists on disk (even if invalid/expired)",
    )
    no_authority_file_policy: str = Field(
        ...,
        description="When file is absent: synthetic_auto_canonical (see modules.session_authority.policy)",
    )
    canonical_cme_session: str = Field(..., description="get_cme_trading_date(utc_now) at request time")
    authority_path: str = Field(..., description="Absolute path to the persistence file")
    state: SessionAuthorityState
