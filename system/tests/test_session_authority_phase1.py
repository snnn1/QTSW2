"""Phase 1: SessionAuthority model + read-only observability."""

import json
from pathlib import Path

import pytest

from modules.session_authority.models import SessionAuthorityState
from modules.session_authority.observability import get_session_authority_observation, session_authority_path
from modules.session_authority.policy import NO_FILE_OBSERVABILITY_MODE


def test_session_authority_path_under_project(tmp_path: Path) -> None:
    p = session_authority_path(tmp_path)
    assert p.name == "session_authority.json"
    assert p.parent.name == "session"


def test_observation_synthetic_when_no_file(tmp_path: Path) -> None:
    obs = get_session_authority_observation(tmp_path)
    assert obs.observation_reason == "synthetic_no_file"
    assert obs.persisted is False
    assert obs.synthetic is True
    assert obs.authority_file_present is False
    assert obs.no_authority_file_policy == NO_FILE_OBSERVABILITY_MODE
    assert obs.state.mode == "auto"
    assert obs.state.reason == "not_persisted"
    assert obs.state.version == 0
    assert obs.state.session_trading_date == obs.canonical_cme_session


def test_observation_loads_valid_file(tmp_path: Path) -> None:
    path = session_authority_path(tmp_path)
    path.parent.mkdir(parents=True, exist_ok=True)
    doc = {
        "mode": "manual",
        "session_trading_date": "2026-04-01",
        "source": "user",
        "locked": True,
        "set_at_utc": "2026-04-01T12:00:00Z",
        "set_by": "test",
        "reason": "manual_generate_request",
        "version": 1,
    }
    path.write_text(json.dumps(doc), encoding="utf-8")

    obs = get_session_authority_observation(tmp_path)
    assert obs.observation_reason == "loaded"
    assert obs.persisted is True
    assert obs.synthetic is False
    assert obs.authority_file_present is True
    assert obs.state.session_trading_date == "2026-04-01"
    assert obs.state.mode == "manual"
    assert obs.state.locked is True


def test_observation_invalid_file_falls_back_to_synthetic(tmp_path: Path) -> None:
    path = session_authority_path(tmp_path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("{not json", encoding="utf-8")

    obs = get_session_authority_observation(tmp_path)
    assert obs.observation_reason == "synthetic_invalid_persisted"
    assert obs.persisted is False
    assert obs.synthetic is True
    assert obs.authority_file_present is True


def test_observation_expired_file_falls_back(tmp_path: Path) -> None:
    path = session_authority_path(tmp_path)
    path.parent.mkdir(parents=True, exist_ok=True)
    doc = {
        "mode": "manual",
        "session_trading_date": "2026-04-01",
        "source": "user",
        "locked": False,
        "set_at_utc": "2020-01-01T12:00:00Z",
        "set_by": "test",
        "reason": "test_expiry",
        "version": 2,
        "expires_at_utc": "2000-01-01T00:00:00Z",
    }
    path.write_text(json.dumps(doc), encoding="utf-8")

    obs = get_session_authority_observation(tmp_path)
    assert obs.observation_reason == "synthetic_expired"
    assert obs.persisted is False
    assert obs.synthetic is True
    assert obs.authority_file_present is True
    assert obs.state.reason == "authority_expired"


def test_model_rejects_bad_date() -> None:
    with pytest.raises(Exception):
        SessionAuthorityState(
            mode="auto",
            session_trading_date="04-01-2026",
            source="system",
            locked=False,
            set_by="x",
            reason="y",
            version=1,
        )
