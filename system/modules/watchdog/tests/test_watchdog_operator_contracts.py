from __future__ import annotations

import json
import sys
import uuid
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[3]))

from modules.watchdog.run_context import WatchdogRunContext
from modules.watchdog.state_manager import CursorManager, WatchdogStateManager, _stable_json_hash
from modules.watchdog.timetable_poller import TimetablePoller, resolve_watchdog_timetable_path


def _workspace_temp_dir() -> Path:
    base = Path.cwd() / "tmp" / "pytest_watchdog"
    base.mkdir(parents=True, exist_ok=True)
    path = base / uuid.uuid4().hex
    path.mkdir(parents=True, exist_ok=False)
    return path


def test_timetable_poller_keeps_disabled_stream_diagnostics(monkeypatch):
    temp_root = _workspace_temp_dir()
    timetable_path = temp_root / "timetable_current.json"
    timetable_path.write_text(
        json.dumps(
            {
                "session_trading_date": "2026-04-21",
                "streams": [
                    {"stream": "ES1", "instrument": "ES", "session": "S1", "slot_time": "07:30", "enabled": True},
                    {
                        "stream": "ES2",
                        "instrument": "ES",
                        "session": "S2",
                        "slot_time": "09:30",
                        "decision_time": "09:30",
                        "enabled": False,
                        "block_reason": "matrix_filter_blocked:scf_s2_blocked(>=0.5)",
                    },
                ],
            }
        ),
        encoding="utf-8",
    )

    poller = TimetablePoller()
    monkeypatch.setattr(poller, "_timetable_path", timetable_path)
    monkeypatch.setattr(
        "modules.watchdog.timetable_poller.resolve_live_execution_session_trading_date",
        lambda session_str, utc_now, is_replay_document=False: (session_str, "match"),
    )

    trading_date, enabled_streams, _hash, metadata, _source, ordered, _identity = poller.poll()

    assert trading_date == "2026-04-21"
    assert enabled_streams == {"ES1"}
    assert ordered == ["ES1"]
    assert metadata is not None
    assert metadata["ES1"]["enabled"] is True
    assert metadata["ES2"]["enabled"] is False
    assert metadata["ES2"]["block_reason"] == "matrix_filter_blocked:scf_s2_blocked(>=0.5)"
    assert metadata["ES2"]["decision_time"] == "09:30"


def test_watchdog_prefers_replay_timetable_for_playback_run_context(monkeypatch):
    temp_root = _workspace_temp_dir()
    timetable_dir = temp_root / "data" / "timetable"
    timetable_dir.mkdir(parents=True, exist_ok=True)

    (timetable_dir / "timetable_current.json").write_text(
        json.dumps(
            {
                "session_trading_date": "2026-04-21",
                "streams": [{"stream": "ES1", "enabled": True}],
            }
        ),
        encoding="utf-8",
    )
    replay_path = timetable_dir / "timetable_replay_current.json"
    replay_path.write_text(
        json.dumps(
            {
                "session_trading_date": "2026-04-13",
                "metadata": {"replay": True},
                "streams": [{"stream": "NQ1", "enabled": True}],
            }
        ),
        encoding="utf-8",
    )

    monkeypatch.setattr("modules.watchdog.timetable_poller.QTSW2_ROOT", temp_root)

    context = WatchdogRunContext(
        project_root=temp_root,
        persistence_base=(temp_root / "runs" / "playback123"),
        run_id="playback123",
        is_run_scoped=True,
        robot_logs_dir=temp_root / "runs" / "playback123" / "logs" / "robot",
        frontend_feed_file=temp_root / "runs" / "playback123" / "logs" / "robot" / "frontend_feed.jsonl",
        slot_journals_dir=temp_root / "runs" / "playback123" / "state" / "stream_journals",
        execution_journals_dir=temp_root / "runs" / "playback123" / "state" / "execution_journals",
        execution_summaries_dir=temp_root / "runs" / "playback123" / "derived" / "execution_summaries",
    )

    assert resolve_watchdog_timetable_path(context) == replay_path

    poller = TimetablePoller()
    monkeypatch.setattr(
        "modules.watchdog.timetable_poller.resolve_live_execution_session_trading_date",
        lambda session_str, utc_now, is_replay_document=False: (session_str, "match"),
    )

    trading_date, enabled_streams, _hash, metadata, _source, ordered, _identity = poller.poll(context)

    assert trading_date == "2026-04-13"
    assert enabled_streams == {"NQ1"}
    assert ordered == ["NQ1"]
    assert metadata is not None
    assert metadata["NQ1"]["enabled"] is True




def test_cursor_manager_scopes_saved_state_by_run_context(monkeypatch):
    temp_root = _workspace_temp_dir()
    cursor_file = temp_root / "frontend_cursor.json"
    monkeypatch.setattr("modules.watchdog.state_manager.FRONTEND_CURSOR_FILE", cursor_file)

    class StubContext:
        def __init__(self, persistence_base: Path):
            self.persistence_base = persistence_base

    current = {"value": StubContext(temp_root / "runs" / "run_a")}
    monkeypatch.setattr(
        "modules.watchdog.run_context.resolve_active_run_context",
        lambda: current["value"],
    )

    manager = CursorManager()
    assert manager.save_cursor({"run_a": 11}) is True

    current["value"] = StubContext(temp_root / "runs" / "run_b")
    assert manager.load_cursor() == {}
    assert manager.save_cursor({"run_b": 22}) is True

    current["value"] = StubContext(temp_root / "runs" / "run_a")
    assert manager.load_cursor() == {"run_a": 11}

    raw = json.loads(cursor_file.read_text(encoding="utf-8"))
    assert "__contexts__" in raw
    assert len(raw["__contexts__"]) == 2


def test_watchdog_status_reports_session_authority_and_policy_hash(monkeypatch):
    temp_root = _workspace_temp_dir()
    (temp_root / "configs").mkdir(parents=True, exist_ok=True)
    (temp_root / "data" / "session").mkdir(parents=True, exist_ok=True)

    policy = {
        "schema": "qtsw2.execution_policy",
        "canonical_markets": {
            "ES": {
                "execution_instruments": {
                    "MES": {"enabled": True}
                }
            }
        },
    }
    (temp_root / "configs" / "execution_policy.json").write_text(
        json.dumps(policy),
        encoding="utf-8",
    )
    (temp_root / "data" / "session" / "session_authority.json").write_text(
        json.dumps(
            {
                "mode": "auto",
                "source": "matrix",
                "session_trading_date": "2026-04-21",
            }
        ),
        encoding="utf-8",
    )

    monkeypatch.setattr("modules.watchdog.state_manager.QTSW2_ROOT", temp_root)

    sm = WatchdogStateManager()
    sm._trading_date = "2026-04-21"
    sm._last_robot_execution_policy_hash = _stable_json_hash(policy)
    sm._last_robot_execution_policy_hash_utc = None
    sm.update_timetable_streams(
        {"ES1"},
        "2026-04-21",
        "content-hash",
        utc_now=datetime.now(timezone.utc),
        enabled_streams_metadata={
            "ES1": {
                "instrument": "ES",
                "session": "S1",
                "slot_time": "07:30",
                "enabled": True,
            },
            "ES2": {
                "instrument": "ES",
                "session": "S2",
                "slot_time": "09:30",
                "enabled": False,
                "block_reason": "matrix_filter_blocked:scf_s2_blocked(>=0.5)",
            },
        },
    )

    status = sm.compute_watchdog_status()

    assert status["session_authority"]["file_present"] is True
    assert status["session_authority"]["mode"] == "auto"
    assert status["session_authority"]["matches_timetable"] is True
    assert status["local_execution_policy_hash"] == sm._last_robot_execution_policy_hash
    assert status["execution_policy_hash_match"] is True
    diag = {row["stream"]: row for row in status["timetable_stream_diagnostics"]}
    assert diag["ES2"]["enabled"] is False
    assert diag["ES2"]["block_reason"] == "matrix_filter_blocked:scf_s2_blocked(>=0.5)"
