from __future__ import annotations

import json
import sys
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[3]))

from modules.watchdog.aggregator import _derive_stream_state_reference_utc
from modules.watchdog import aggregator_main, state_manager as state_manager_module
from modules.watchdog.alert_ledger import AlertLedger
from modules.watchdog.event_processor import EventProcessor
from modules.watchdog.run_context import WatchdogRunContext
from modules.watchdog.run_artifacts import read_run_summary_json
from modules.watchdog.state_manager import CursorManager, StreamStateInfo, WatchdogStateManager, _stable_json_hash
from modules.watchdog.timetable_poller import TimetablePoller, resolve_watchdog_timetable_path


def _workspace_temp_dir() -> Path:
    base = Path.cwd() / "tmp" / "pytest_watchdog"
    base.mkdir(parents=True, exist_ok=True)
    path = base / uuid.uuid4().hex
    path.mkdir(parents=True, exist_ok=False)
    return path


def _watchdog_context_for_root(root: Path) -> WatchdogRunContext:
    return WatchdogRunContext(
        project_root=root,
        persistence_base=root,
        run_id=None,
        is_run_scoped=False,
        robot_logs_dir=root / "logs" / "robot",
        frontend_feed_file=root / "logs" / "robot" / "frontend_feed.jsonl",
        slot_journals_dir=root / "state" / "stream_journals",
        execution_journals_dir=root / "state" / "execution_journals",
        execution_summaries_dir=root / "derived" / "execution_summaries",
    )


def test_run_summary_reads_authority_shutdown_frame():
    temp_root = _workspace_temp_dir()
    run_root = temp_root / "runs" / "authority_frame"
    run_root.mkdir(parents=True)
    (run_root / "summary.json").write_text(
        json.dumps({"run_id": "authority_frame", "status": "OK"}),
        encoding="utf-8",
    )
    (run_root / "AUTHORITY_SHUTDOWN_FRAME.json").write_text(
        json.dumps(
            {
                "action": "SHUTDOWN_SAFE_VERDICT",
                "allowed": True,
                "is_clean_flat": True,
                "broker_position_qty": 0,
                "broker_working_orders_count": 0,
            }
        ),
        encoding="utf-8",
    )

    summary = read_run_summary_json(run_root)

    assert summary is not None
    assert summary["authority_shutdown_frame_available"] is True
    assert summary["authority_shutdown_frame"]["action"] == "SHUTDOWN_SAFE_VERDICT"
    assert summary["authority_shutdown_frame"]["is_clean_flat"] is True


def test_risk_latch_files_are_visible_as_active_instrument_blocks():
    temp_root = _workspace_temp_dir()
    latch_dir = temp_root / "data" / "risk_latches"
    latch_dir.mkdir(parents=True, exist_ok=True)
    latch_file = latch_dir / "risk_latch_DEMO4338364-2_MNG.json"
    latch_file.write_text(
        json.dumps(
            {
                "Account": "DEMO4338364-2",
                "Instrument": "MNG",
                "BlockedAtUtc": "2026-05-10T23:22:36.1744164+00:00",
                "Reason": "FORCED_CONVERGENCE_FAILED:position_qty_delta_2",
            }
        ),
        encoding="utf-8",
    )

    rows = aggregator_main._read_risk_latch_files(_watchdog_context_for_root(temp_root))
    matching_rows = [
        row
        for row in rows
        if Path(row.get("file_path", "")).resolve() == latch_file.resolve()
    ]

    assert len(matching_rows) == 1
    assert matching_rows[0]["instrument"] == "MNG"
    assert matching_rows[0]["reason"] == "FORCED_CONVERGENCE_FAILED:position_qty_delta_2"
    assert matching_rows[0]["clear_policy"] == "auto_clear_when_clean_flat"
    assert matching_rows[0]["blocks_entries"] is True
    assert matching_rows[0]["blocks_reentry"] is True
    assert matching_rows[0]["blocks_protectives"] is False
    assert matching_rows[0]["blocks_flatten"] is False

    payload = aggregator_main.WatchdogAggregator().get_risk_latches_for_context(_watchdog_context_for_root(temp_root))
    matching_payload = [
        row
        for row in payload.get("risk_latches", [])
        if Path(row.get("file_path", "")).resolve() == latch_file.resolve()
    ]
    assert len(matching_payload) == 1
    assert matching_payload[0]["clear_readiness"]["status"] == "awaiting_position_authority"
    assert matching_payload[0]["clear_readiness"]["can_watchdog_clear"] is False
    assert matching_payload[0]["clear_readiness"]["failed_predicates"] == ["position_authority_available"]


def test_risk_latch_clear_readiness_reports_failed_predicates():
    readiness = aggregator_main._build_risk_latch_clear_readiness(
        {"reason": "FORCED_CONVERGENCE_FAILED:position_qty_delta_2"},
        {
            "broker_qty": "2",
            "real_open_qty": "0",
            "recovery_open_qty": "0",
            "journal_open_qty": "2",
            "last_authority_ts_utc": "2026-05-12T15:47:17Z",
        },
    )

    assert readiness["status"] == "blocked_by_position_authority"
    assert readiness["robot_clear_candidate"] is False
    assert readiness["clear_allowed"] is False
    assert readiness["failed_predicates"] == ["broker_qty_flat", "journal_open_qty_flat"]


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


def test_watchdog_run_context_uses_scenario_manifest_timetable_before_global_replay(monkeypatch):
    temp_root = _workspace_temp_dir()
    timetable_dir = temp_root / "data" / "timetable"
    timetable_dir.mkdir(parents=True, exist_ok=True)
    (timetable_dir / "timetable_replay_current.json").write_text(
        json.dumps(
            {
                "session_trading_date": "2026-04-13",
                "metadata": {"replay": True},
                "streams": [{"stream": "OLD1", "enabled": True}],
            }
        ),
        encoding="utf-8",
    )

    run_root = temp_root / "runs" / "scenario123"
    scenario_dir = run_root / "playback_scenario"
    scenario_timetable = scenario_dir / "timetables" / "timetable_2026-04-29.json"
    scenario_timetable.parent.mkdir(parents=True, exist_ok=True)
    scenario_timetable.write_text(
        json.dumps(
            {
                "session_trading_date": "2026-04-29",
                "metadata": {"replay": True},
                "source": "playback_scenario",
                "streams": [{"stream": "NG2", "instrument": "NG", "session": "S2", "slot_time": "09:00", "enabled": True}],
            }
        ),
        encoding="utf-8",
    )
    (scenario_dir / "playback_scenario.json").write_text(
        json.dumps(
            {
                "run_id": "scenario123",
                "dates": ["2026-04-28", "2026-04-29"],
                "timetables": {
                    "2026-04-28": {"path": "timetables/timetable_2026-04-28.json"},
                    "2026-04-29": {"path": "timetables/timetable_2026-04-29.json"},
                },
            }
        ),
        encoding="utf-8",
    )
    (run_root / "summary.json").write_text(
        json.dumps({"run_id": "scenario123", "date": "2026-04-29"}),
        encoding="utf-8",
    )

    monkeypatch.setattr("modules.watchdog.timetable_poller.QTSW2_ROOT", temp_root)
    context = WatchdogRunContext(
        project_root=temp_root,
        persistence_base=run_root,
        run_id="scenario123",
        is_run_scoped=True,
        robot_logs_dir=run_root / "logs" / "robot",
        frontend_feed_file=run_root / "logs" / "robot" / "frontend_feed.jsonl",
        slot_journals_dir=run_root / "state" / "stream_journals",
        execution_journals_dir=run_root / "state" / "execution_journals",
        execution_summaries_dir=run_root / "derived" / "execution_summaries",
    )

    assert resolve_watchdog_timetable_path(context) == scenario_timetable.resolve()

    poller = TimetablePoller()
    monkeypatch.setattr(
        "modules.watchdog.timetable_poller.resolve_live_execution_session_trading_date",
        lambda session_str, utc_now, is_replay_document=False: (session_str, "match"),
    )

    trading_date, enabled_streams, _hash, metadata, source, ordered, _identity = poller.poll(context)

    assert trading_date == "2026-04-29"
    assert enabled_streams == {"NG2"}
    assert ordered == ["NG2"]
    assert source == "playback_scenario"
    assert metadata is not None
    assert metadata["NG2"]["instrument"] == "NG"


def test_watchdog_scenario_timetable_follows_robot_runtime_clock(monkeypatch):
    temp_root = _workspace_temp_dir()
    timetable_dir = temp_root / "data" / "timetable"
    timetable_dir.mkdir(parents=True, exist_ok=True)
    (timetable_dir / "timetable_replay_current.json").write_text(
        json.dumps(
            {
                "session_trading_date": "2026-04-13",
                "metadata": {"replay": True},
                "streams": [{"stream": "OLD1", "enabled": True}],
            }
        ),
        encoding="utf-8",
    )

    run_root = temp_root / "runs" / "scenario_clock"
    scenario_dir = run_root / "playback_scenario"
    scenario_timetable_28 = scenario_dir / "timetables" / "timetable_2026-04-28.json"
    scenario_timetable_29 = scenario_dir / "timetables" / "timetable_2026-04-29.json"
    scenario_timetable_28.parent.mkdir(parents=True, exist_ok=True)
    scenario_timetable_28.write_text(
        json.dumps(
            {
                "session_trading_date": "2026-04-28",
                "metadata": {"replay": True},
                "source": "playback_scenario",
                "streams": [{"stream": "NQ1", "instrument": "NQ", "enabled": True}],
            }
        ),
        encoding="utf-8",
    )
    scenario_timetable_29.write_text(
        json.dumps(
            {
                "session_trading_date": "2026-04-29",
                "metadata": {"replay": True},
                "source": "playback_scenario",
                "streams": [{"stream": "NG2", "instrument": "NG", "enabled": True}],
            }
        ),
        encoding="utf-8",
    )
    (scenario_dir / "playback_scenario.json").write_text(
        json.dumps(
            {
                "run_id": "scenario_clock",
                "dates": ["2026-04-28", "2026-04-29"],
                "timetables": {
                    "2026-04-28": {"path": "timetables/timetable_2026-04-28.json"},
                    "2026-04-29": {"path": "timetables/timetable_2026-04-29.json"},
                },
            }
        ),
        encoding="utf-8",
    )
    (run_root / "summary.json").write_text(
        json.dumps({"run_id": "scenario_clock", "date": "2026-04-28"}),
        encoding="utf-8",
    )
    runtime_clock = run_root / "state" / "runtime_clock.json"
    runtime_clock.parent.mkdir(parents=True, exist_ok=True)
    runtime_clock.write_text(
        json.dumps(
            {
                "schema": "qtsw2.playback_runtime_clock.v1",
                "source": "robot_playback_scenario",
                "run_id": "scenario_clock",
                "active_session_trading_date": "2026-04-29",
                "timetable_path": str(scenario_timetable_29),
            }
        ),
        encoding="utf-8",
    )

    monkeypatch.setattr("modules.watchdog.timetable_poller.QTSW2_ROOT", temp_root)
    context = WatchdogRunContext(
        project_root=temp_root,
        persistence_base=run_root,
        run_id="scenario_clock",
        is_run_scoped=True,
        robot_logs_dir=run_root / "logs" / "robot",
        frontend_feed_file=run_root / "logs" / "robot" / "frontend_feed.jsonl",
        slot_journals_dir=run_root / "state" / "stream_journals",
        execution_journals_dir=run_root / "state" / "execution_journals",
        execution_summaries_dir=run_root / "derived" / "execution_summaries",
    )

    assert resolve_watchdog_timetable_path(context) == scenario_timetable_29.resolve()

    poller = TimetablePoller()
    monkeypatch.setattr(
        "modules.watchdog.timetable_poller.resolve_live_execution_session_trading_date",
        lambda session_str, utc_now, is_replay_document=False: (session_str, "match"),
    )

    trading_date, enabled_streams, _hash, metadata, source, ordered, _identity = poller.poll(context)

    assert trading_date == "2026-04-29"
    assert enabled_streams == {"NG2"}
    assert ordered == ["NG2"]
    assert source == "playback_scenario"
    assert metadata is not None
    assert metadata["NG2"]["instrument"] == "NG"




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


def test_execution_policy_failure_accepts_string_payloads():
    sm = WatchdogStateManager()

    sm.record_execution_policy_failure(
        errors="policy hash mismatch",
        execution_instruments="MNG 06-26",
        timestamp_utc=datetime(2026, 5, 5, 21, 6, tzinfo=timezone.utc),
        note="robot payload used scalar strings",
    )

    status = sm.compute_watchdog_status()
    assert status["execution_policy_failures_count"] == 1
    failure = status["execution_policy_failures"][0]
    assert failure["errors"] == ["policy hash mismatch"]
    assert failure["execution_instruments"] == ["MNG 06-26"]


def test_identity_invariants_accept_scalar_violation_payloads():
    sm = WatchdogStateManager()

    sm.update_identity_invariants(
        pass_value=False,
        violations="canonical/execution mismatch",
        canonical_instrument="NG",
        execution_instrument="MNG 06-26",
        stream_ids="NG1",
        checked_at_utc=datetime(2026, 5, 6, 10, 15, tzinfo=timezone.utc),
    )

    status = sm.compute_watchdog_status()
    assert status["last_identity_violations"] == ["canonical/execution mismatch"]


def test_watchdog_closes_stale_intent_only_on_clean_flat_authority():
    sm = WatchdogStateManager()
    sm.update_intent_exposure(
        "intent-flat",
        "RTY2",
        "M2K",
        "Long",
        entry_filled_qty=2,
        exit_filled_qty=0,
        trading_date="2026-05-06",
    )
    sm.record_position_authority_evaluated(
        {
            "instrument": "M2K",
            "broker_qty": "0",
            "real_open_qty": "0",
            "journal_open_qty": "0",
            "recovery_open_qty": None,
            "authority_state": "REAL_DOMINANT",
        },
        datetime(2026, 5, 7, 13, 45, tzinfo=timezone.utc),
    )

    assert sm.reconcile_intent_exposures_with_position_authority() == 1
    assert sm.get_intent_exposures()["intent-flat"].state == "CLOSED"
    assert sm.get_intent_exposures()["intent-flat"].exit_filled_qty == 2


def test_watchdog_does_not_close_gross_open_intent_on_broker_net_flat():
    sm = WatchdogStateManager()
    sm.update_intent_exposure(
        "intent-gross-open",
        "RTY2",
        "M2K",
        "Long",
        entry_filled_qty=2,
        exit_filled_qty=0,
        trading_date="2026-05-06",
    )
    sm.record_position_authority_evaluated(
        {
            "instrument": "M2K",
            "broker_qty": "0",
            "real_open_qty": "2",
            "journal_open_qty": "2",
            "recovery_open_qty": "0",
            "authority_state": "REAL_DOMINANT",
        },
        datetime(2026, 5, 7, 13, 45, tzinfo=timezone.utc),
    )

    assert sm.reconcile_intent_exposures_with_position_authority() == 0
    assert sm.get_intent_exposures()["intent-gross-open"].state == "ACTIVE"


def test_completed_trade_journal_resolves_order_stuck_alert():
    temp_root = _workspace_temp_dir()
    execution_journals = temp_root / "state" / "execution_journals"
    execution_journals.mkdir(parents=True, exist_ok=True)
    (execution_journals / "2026-05-07_GC1_intent-done.json").write_text(
        json.dumps(
            {
                "IntentId": "intent-done",
                "TradingDate": "2026-05-07",
                "Stream": "GC1",
                "Instrument": "MGC",
                "EntryFilled": True,
                "TradeCompleted": True,
                "CompletionReason": "STOP",
            }
        ),
        encoding="utf-8",
    )

    ledger = AlertLedger(temp_root / "alert_ledger.jsonl")
    ledger.append_alert(
        "alert-1",
        "ORDER_STUCK_DETECTED",
        "medium",
        {"sample": {"intent_id": "intent-done", "broker_order_id": "order-1"}},
        "ORDER_STUCK_DETECTED_BATCH",
    )

    class DummyNotificationService:
        def __init__(self, alert_ledger):
            self._ledger = alert_ledger

        def get_ledger(self):
            return self._ledger

        def resolve_alert(self, dedupe_key: str) -> None:
            self._ledger.resolve_alert(dedupe_key)

    agg = aggregator_main.WatchdogAggregator.__new__(aggregator_main.WatchdogAggregator)
    agg._snapshot_mode = True
    agg._notification_service = DummyNotificationService(ledger)
    agg._run_context = WatchdogRunContext(
        project_root=temp_root,
        persistence_base=temp_root,
        run_id="test",
        is_run_scoped=True,
        robot_logs_dir=temp_root / "logs" / "robot",
        frontend_feed_file=temp_root / "logs" / "robot" / "frontend_feed.jsonl",
        slot_journals_dir=temp_root / "state" / "stream_journals",
        execution_journals_dir=execution_journals,
        execution_summaries_dir=temp_root / "derived" / "execution_summaries",
    )

    agg._resolve_completed_order_stuck_alerts()

    assert ledger.get_active_alerts() == []


def test_unfilled_resting_entry_journal_resolves_order_stuck_noise_alert():
    temp_root = _workspace_temp_dir()
    execution_journals = temp_root / "state" / "execution_journals"
    execution_journals.mkdir(parents=True, exist_ok=True)
    (execution_journals / "2026-05-12_YM1_intent-resting.json").write_text(
        json.dumps(
            {
                "IntentId": "intent-resting",
                "TradingDate": "2026-05-12",
                "Stream": "YM1",
                "Instrument": "MYM",
                "EntrySubmitted": True,
                "EntryFilled": False,
                "TradeCompleted": False,
                "EntryOrderType": "ENTRY_STOP",
            }
        ),
        encoding="utf-8",
    )

    ledger = AlertLedger(temp_root / "alert_ledger.jsonl")
    ledger.append_alert(
        "alert-resting",
        "ORDER_STUCK_DETECTED",
        "medium",
        {
            "broker_order_id": "entry-stop-order",
            "intent_id": "intent-resting",
            "role": "entry",
        },
        "ORDER_STUCK_DETECTED:entry-stop-order",
    )

    class DummyNotificationService:
        def __init__(self, alert_ledger):
            self._ledger = alert_ledger

        def get_ledger(self):
            return self._ledger

        def resolve_alert(self, dedupe_key: str) -> None:
            self._ledger.resolve_alert(dedupe_key)

    agg = aggregator_main.WatchdogAggregator.__new__(aggregator_main.WatchdogAggregator)
    agg._snapshot_mode = True
    agg._notification_service = DummyNotificationService(ledger)
    agg._run_context = WatchdogRunContext(
        project_root=temp_root,
        persistence_base=temp_root,
        run_id="test",
        is_run_scoped=True,
        robot_logs_dir=temp_root / "logs" / "robot",
        frontend_feed_file=temp_root / "logs" / "robot" / "frontend_feed.jsonl",
        slot_journals_dir=temp_root / "state" / "stream_journals",
        execution_journals_dir=execution_journals,
        execution_summaries_dir=temp_root / "derived" / "execution_summaries",
    )

    agg._resolve_completed_order_stuck_alerts()

    assert ledger.get_active_alerts() == []


def test_completed_incident_alert_is_a_notification_not_active_latch():
    temp_root = _workspace_temp_dir()
    ledger_path = temp_root / "alert_ledger.jsonl"
    ledger = AlertLedger(ledger_path)

    ledger.append_alert(
        "alert-incident",
        "INCIDENT_RECONCILIATION_GATE_FAIL_CLOSED",
        "critical",
        {
            "incident_id": "incident-1",
            "start_ts": "2026-05-10T23:22:33Z",
            "end_ts": "2026-05-11T09:37:06Z",
        },
        "INCIDENT_RECONCILIATION_GATE_FAIL_CLOSED_incident-1",
    )

    assert ledger.get_active_alerts() == []
    rehydrated = AlertLedger(ledger_path)
    assert rehydrated.get_active_alerts() == []


def test_order_submit_success_entry_stop_does_not_acknowledge_protection():
    sm = WatchdogStateManager()
    processor = EventProcessor(sm)
    sm.update_intent_exposure(
        "intent-entry-stop",
        "NG2",
        "MNG",
        "Long",
        entry_filled_qty=2,
        entry_filled_at_utc=datetime.now(timezone.utc) - timedelta(seconds=30),
        trading_date="2026-05-07",
    )

    processor.process_event(
        {
            "event_type": "ORDER_SUBMIT_SUCCESS",
            "timestamp_utc": "2026-05-07T16:01:04+00:00",
            "data": {
                "intent_id": "intent-entry-stop",
                "broker_order_id": "entry-order",
                "order_type": "ENTRY_STOP",
            },
        }
    )

    assert sm.get_active_intents_missing_protective_ack() == {"intent-entry-stop"}
    assert sm.compute_unprotected_positions()


def test_watchdog_reports_untracked_broker_exposure_from_position_authority():
    sm = WatchdogStateManager()
    sm.record_position_authority_evaluated(
        {
            "instrument": "MNG",
            "authority_state": "RECOVERY_REQUIRED",
            "broker_qty": 2,
            "real_open_qty": 0,
            "recovery_open_qty": 0,
            "journal_open_qty": 0,
        },
        datetime.now(timezone.utc),
    )

    positions = sm.compute_unprotected_positions()

    assert positions
    assert positions[0]["classification"] == "UNTRACKED_BROKER_EXPOSURE"
    assert positions[0]["reason"] == "broker_position_without_active_journal_or_intent"
    assert positions[0]["broker_qty"] == 2


def test_watchdog_tail_position_authority_refresh_keeps_newest_snapshot():
    cutoff = datetime(2026, 5, 11, 20, 54, tzinfo=timezone.utc)
    events = [
        {
            "event_type": "POSITION_AUTHORITY_EVALUATED",
            "timestamp_utc": "2026-05-11T20:53:00Z",
            "data": {
                "instrument": "MES",
                "authority_state": "REAL_DOMINANT",
                "broker_qty": 2,
                "real_open_qty": 2,
                "recovery_open_qty": 0,
                "journal_open_qty": 2,
            },
        },
        {
            "event_type": "POSITION_AUTHORITY_EVALUATED",
            "timestamp_utc": "2026-05-11T20:55:40Z",
            "data": {
                "instrument": "MES",
                "authority_state": "REAL_DOMINANT",
                "broker_qty": 0,
                "real_open_qty": 0,
                "recovery_open_qty": 0,
                "journal_open_qty": 0,
            },
        },
        {
            "event_type": "POSITION_AUTHORITY_EVALUATED",
            "timestamp_utc": "2026-05-11T20:56:00Z",
            "data": {
                "instrument": "MYM",
                "authority_state": "REAL_DOMINANT",
                "broker_qty": 0,
                "real_open_qty": 0,
                "recovery_open_qty": 0,
                "journal_open_qty": 0,
            },
        },
    ]

    snapshots = aggregator_main._latest_position_authority_tail_payloads(
        events,
        reject_at_or_before=cutoff,
    )

    by_instrument = {payload["instrument"]: payload for _ts, payload in snapshots}
    assert by_instrument["MES"]["broker_qty"] == 0
    assert by_instrument["MES"]["journal_open_qty"] == 0
    assert by_instrument["MYM"]["broker_qty"] == 0


def test_watchdog_ingests_release_readiness_working_order_counts():
    sm = WatchdogStateManager()
    processor = EventProcessor(sm)

    processor.process_event(
        {
            "event_type": "RELEASE_READINESS_INPUT_AUDIT",
            "timestamp_utc": "2026-05-13T04:08:08Z",
            "data": {
                "instrument": "MYM",
                "broker_position_qty": "4",
                "broker_working_count": "4",
                "iea_trusted_working_count": "4",
                "journal_open_qty": "4",
            },
        }
    )

    snapshots = sm.get_position_authority_snapshots()
    assert snapshots["MYM"]["broker_qty"] == "4"
    assert snapshots["MYM"]["broker_working_count"] == "4"
    assert snapshots["MYM"]["iea_trusted_working_count"] == "4"
    assert snapshots["MYM"]["source_event"] == "RELEASE_READINESS_INPUT_AUDIT"


def test_watchdog_does_not_report_untracked_broker_exposure_when_intent_is_active():
    sm = WatchdogStateManager()
    sm.update_intent_exposure(
        "intent-carryover",
        "NG2",
        "MNG",
        "Long",
        entry_filled_qty=2,
        exit_filled_qty=0,
        entry_filled_at_utc=datetime.now(timezone.utc),
        trading_date="2026-05-07",
    )
    sm.record_position_authority_evaluated(
        {
            "instrument": "MNG",
            "authority_state": "OK",
            "broker_qty": 2,
            "real_open_qty": 2,
            "recovery_open_qty": 0,
            "journal_open_qty": 2,
        },
        datetime.now(timezone.utc),
    )

    positions = sm.compute_unprotected_positions()

    assert not any(p.get("classification") == "UNTRACKED_BROKER_EXPOSURE" for p in positions)


def test_watchdog_does_not_report_untracked_broker_exposure_when_authority_tracks_journal_open():
    sm = WatchdogStateManager()
    sm.record_position_authority_evaluated(
        {
            "instrument": "MES",
            "authority_state": "REAL_DOMINANT",
            "broker_qty": 2,
            "real_open_qty": 2,
            "recovery_open_qty": 0,
            "journal_open_qty": 2,
        },
        datetime.now(timezone.utc),
    )

    positions = sm.compute_unprotected_positions()

    assert not any(p.get("classification") == "UNTRACKED_BROKER_EXPOSURE" for p in positions)


def test_watchdog_journal_hydration_reactivates_preexisting_closed_intent_shell():
    temp_root = _workspace_temp_dir()
    execution_journals = temp_root / "state" / "execution_journals"
    execution_journals.mkdir(parents=True, exist_ok=True)
    (execution_journals / "2026-05-11_ES2_intent-open.json").write_text(
        json.dumps(
            {
                "IntentId": "intent-open",
                "TradingDate": "2026-05-11",
                "Stream": "ES2",
                "Instrument": "MES",
                "Direction": "Long",
                "EntryFilled": True,
                "TradeCompleted": False,
                "EntryFilledQuantityTotal": 2,
                "ExitFilledQuantityTotal": 0,
                "EntryFilledAtUtc": "2026-05-11T16:25:10Z",
            }
        ),
        encoding="utf-8",
    )

    sm = WatchdogStateManager()
    sm.update_intent_exposure(
        "intent-open",
        "ES2",
        "MES",
        "Long",
        entry_filled_qty=0,
        exit_filled_qty=0,
        state="CLOSED",
        trading_date="2026-05-11",
    )

    assert sm.hydrate_intent_exposures_from_journals(execution_journals) == 0
    exposure = sm.get_intent_exposures()["intent-open"]
    assert exposure.state == "ACTIVE"
    assert exposure.entry_filled_qty == 2
    assert exposure.exit_filled_qty == 0


def test_watchdog_stuck_order_detector_ignores_working_protective_orders():
    sm = WatchdogStateManager()
    submitted_at = datetime.now(timezone.utc) - timedelta(minutes=10)
    sm.record_order_submitted(
        "stop-order",
        submitted_at,
        intent_id="intent-protected",
        instrument="MES",
        role="stop",
    )
    sm.record_order_submitted(
        "target-order",
        submitted_at,
        intent_id="intent-protected",
        instrument="MES",
        role="target",
    )

    assert sm.check_stuck_orders(datetime.now(timezone.utc)) == []


def test_watchdog_stuck_order_detector_ignores_working_resting_entry_brackets():
    sm = WatchdogStateManager()
    submitted_at = datetime.now(timezone.utc) - timedelta(minutes=10)
    sm.record_order_submitted(
        "entry-stop-order",
        submitted_at,
        intent_id="intent-resting-entry",
        instrument="MYM",
        role="entry_stop",
        order_type="ENTRY_STOP",
    )

    assert sm.check_stuck_orders(datetime.now(timezone.utc)) == []


def test_watchdog_stuck_order_detector_still_flags_market_entry_commands():
    sm = WatchdogStateManager()
    submitted_at = datetime.now(timezone.utc) - timedelta(minutes=10)
    sm.record_order_submitted(
        "market-entry-order",
        submitted_at,
        intent_id="intent-market-entry",
        instrument="MNG",
        role="market_entry",
        order_type="MARKET_REENTRY",
    )

    stuck = sm.check_stuck_orders(datetime.now(timezone.utc))

    assert len(stuck) == 1
    assert stuck[0]["broker_order_id"] == "market-entry-order"
    assert stuck[0]["role"] == "market_entry"
    assert stuck[0]["order_type"] == "MARKET_REENTRY"


def test_event_processor_classifies_entry_stop_as_resting_order_not_stuck():
    sm = WatchdogStateManager()
    processor = EventProcessor(sm)
    submitted_at = datetime.now(timezone.utc) - timedelta(minutes=10)

    processor.process_event(
        {
            "event_type": "ORDER_SUBMIT_SUCCESS",
            "timestamp_utc": submitted_at.isoformat(),
            "data": {
                "intent_id": "intent-resting-entry",
                "broker_order_id": "entry-stop-order",
                "instrument": "MYM",
                "order_type": "ENTRY_STOP",
            },
        }
    )

    assert sm.check_stuck_orders(datetime.now(timezone.utc)) == []


def test_watchdog_stream_states_surface_prior_day_carried_lifecycle():
    sm = WatchdogStateManager()
    now = datetime.now(timezone.utc)
    sm.update_timetable_streams(
        {"NG1", "NG2"},
        "2026-05-06",
        "hash-current",
        now,
        enabled_streams_metadata={
            "NG1": {"instrument": "NG", "session": "S2", "slot_time": "09:00"},
            "NG2": {"instrument": "NG", "session": "S2", "slot_time": "11:00"},
        },
        enabled_streams_ordered=["NG1", "NG2"],
    )
    sm._stream_states[("2026-05-05", "NG2")] = StreamStateInfo(
        trading_date="2026-05-05",
        stream="NG2",
        state="RANGE_LOCKED",
        committed=False,
        state_entry_time_utc=now - timedelta(hours=3),
        execution_instrument="MNG 06-26",
    )
    sm._stream_states[("2026-05-05", "NG2")].instrument = "NG"
    sm._stream_states[("2026-05-05", "NG2")].session = "S2"
    sm._stream_states[("2026-05-05", "NG2")].slot_time_chicago = "11:00"

    agg = aggregator_main.WatchdogAggregator.__new__(aggregator_main.WatchdogAggregator)
    agg._state_manager = sm
    agg._session_flatten_tracker = None
    agg._last_slot_journal_hydrate_utc = now

    payload = agg.get_stream_states()

    assert [row["stream"] for row in payload["streams"]] == ["NG1", "NG2"]
    carried = payload["carried_active_lifecycles"]
    assert len(carried) == 1
    assert carried[0]["stream"] == "NG2"
    assert carried[0]["trading_date"] == "2026-05-05"
    assert carried[0]["current_timetable_lane_present"] is True
    assert carried[0]["same_stream_deferred_reason"] == "PRIOR_LIFECYCLE_ACTIVE"
    assert carried[0]["operator_classification"] == "TRACKED_CARRIED_STREAM"


def test_stream_feed_uses_open_execution_journal_over_stale_terminal_stream_state():
    temp_root = _workspace_temp_dir()
    execution_journals = temp_root / "state" / "execution_journals"
    execution_journals.mkdir(parents=True, exist_ok=True)
    (execution_journals / "2026-05-12_RTY2_intent-open.json").write_text(
        json.dumps(
            {
                "IntentId": "intent-open",
                "TradingDate": "2026-05-12",
                "Stream": "RTY2",
                "Instrument": "M2K",
                "Direction": "Short",
                "EntrySubmitted": True,
                "EntryFilled": True,
                "EntryOrderType": "ENTRY_STOP",
                "EntryFilledQuantityTotal": 2,
                "ExitFilledQuantityTotal": 0,
                "TradeCompleted": False,
            }
        ),
        encoding="utf-8",
    )

    sm = WatchdogStateManager()
    now = datetime.now(timezone.utc)
    sm.update_timetable_streams(
        {"RTY2"},
        "2026-05-12",
        "hash-current",
        now,
        enabled_streams_metadata={
            "RTY2": {"instrument": "RTY", "session": "S2", "slot_time": "11:00"},
        },
        enabled_streams_ordered=["RTY2"],
    )
    sm._stream_states[("2026-05-12", "RTY2")] = StreamStateInfo(
        trading_date="2026-05-12",
        stream="RTY2",
        state="DONE",
        committed=True,
        commit_reason="STREAM_STAND_DOWN",
        state_entry_time_utc=now,
        execution_instrument="M2K",
    )
    sm._stream_states[("2026-05-12", "RTY2")].instrument = "RTY"
    sm._stream_states[("2026-05-12", "RTY2")].session = "S2"

    agg = aggregator_main.WatchdogAggregator.__new__(aggregator_main.WatchdogAggregator)
    agg._state_manager = sm
    agg._session_flatten_tracker = None
    agg._last_slot_journal_hydrate_utc = now
    agg._run_context = _watchdog_context_for_root(temp_root)
    agg._snapshot_mode = True

    payload = agg.get_stream_states()

    assert payload["execution_expectation_gaps"] == []
    row = payload["streams"][0]
    assert row["state"] == "OPEN_TRACKED"
    assert row["watchdog_state"] == "DONE"
    assert row["watchdog_commit_reason"] == "STREAM_STAND_DOWN"
    assert row["committed"] is False
    assert row["operator_classification"] == "OPEN_TRACKED_EXPOSURE"
    assert row["open_qty"] == 2
    assert row["open_intent_count"] == 1


def test_stream_feed_labels_unfilled_entry_brackets_and_suppresses_slot_gap():
    temp_root = _workspace_temp_dir()
    execution_journals = temp_root / "state" / "execution_journals"
    execution_journals.mkdir(parents=True, exist_ok=True)
    (execution_journals / "2026-05-12_ES2_intent-resting.json").write_text(
        json.dumps(
            {
                "IntentId": "intent-resting",
                "TradingDate": "2026-05-12",
                "Stream": "ES2",
                "Instrument": "MES",
                "Direction": "Long",
                "EntrySubmitted": True,
                "EntryFilled": False,
                "EntryOrderType": "ENTRY_STOP",
                "TradeCompleted": False,
                "Rejected": False,
            }
        ),
        encoding="utf-8",
    )

    sm = WatchdogStateManager()
    now = datetime.now(timezone.utc)
    sm.update_timetable_streams(
        {"ES2"},
        "2026-05-12",
        "hash-current",
        now,
        enabled_streams_metadata={
            "ES2": {"instrument": "ES", "session": "S2", "slot_time": "00:01"},
        },
        enabled_streams_ordered=["ES2"],
    )
    sm._stream_states[("2026-05-12", "ES2")] = StreamStateInfo(
        trading_date="2026-05-12",
        stream="ES2",
        state="RANGE_LOCKED",
        committed=False,
        state_entry_time_utc=now,
        execution_instrument="MES",
    )
    sm._stream_states[("2026-05-12", "ES2")].instrument = "ES"
    sm._stream_states[("2026-05-12", "ES2")].session = "S2"

    agg = aggregator_main.WatchdogAggregator.__new__(aggregator_main.WatchdogAggregator)
    agg._state_manager = sm
    agg._session_flatten_tracker = None
    agg._last_slot_journal_hydrate_utc = now
    agg._run_context = _watchdog_context_for_root(temp_root)
    agg._snapshot_mode = True

    payload = agg.get_stream_states()

    assert payload["execution_expectation_gaps"] == []
    row = payload["streams"][0]
    assert row["state"] == "ENTRY_BRACKETS_WORKING"
    assert row["watchdog_state"] == "RANGE_LOCKED"
    assert row["operator_classification"] == "RESTING_ENTRY_UNFILLED"
    assert row["resting_entry_count"] == 1


def test_stream_pnl_falls_back_to_execution_journals_when_ledger_invariant_fails(monkeypatch):
    temp_root = _workspace_temp_dir()
    execution_journals = temp_root / "state" / "execution_journals"
    execution_journals.mkdir(parents=True, exist_ok=True)
    (execution_journals / "2026-05-12_CL1_intent-open.json").write_text(
        json.dumps(
            {
                "IntentId": "intent-open",
                "TradingDate": "2026-05-12",
                "Stream": "CL1",
                "Instrument": "MCL",
                "Direction": "Long",
                "EntrySubmitted": True,
                "EntryFilled": True,
                "EntryOrderType": "ENTRY_STOP",
                "FillPrice": 102.11,
                "EntryPrice": 102.06,
                "EntryFilledQuantityTotal": 2,
                "ExitFilledQuantityTotal": 0,
                "TradeCompleted": False,
            }
        ),
        encoding="utf-8",
    )
    (execution_journals / "2026-05-12_ES2_intent-resting.json").write_text(
        json.dumps(
            {
                "IntentId": "intent-resting",
                "TradingDate": "2026-05-12",
                "Stream": "ES2",
                "Instrument": "MES",
                "Direction": "Long",
                "EntrySubmitted": True,
                "EntryFilled": False,
                "EntryOrderType": "ENTRY_STOP",
                "EntryPrice": 7421.0,
                "TradeCompleted": False,
            }
        ),
        encoding="utf-8",
    )

    from modules.watchdog.pnl.ledger_builder import LedgerBuilder, LedgerInvariantViolation

    def raise_invariant(self, trading_date, stream=None, skip_invalid_intents=False):
        raise LedgerInvariantViolation("duplicate sequence", {"invariant": "execution_sequence"})

    monkeypatch.setattr(LedgerBuilder, "build_ledger_rows", raise_invariant)

    sm = WatchdogStateManager()
    now = datetime.now(timezone.utc)
    sm.update_timetable_streams(
        {"CL1", "ES2"},
        "2026-05-12",
        "hash-current",
        now,
        enabled_streams_metadata={},
        enabled_streams_ordered=["CL1", "ES2"],
    )

    agg = aggregator_main.WatchdogAggregator.__new__(aggregator_main.WatchdogAggregator)
    agg._state_manager = sm
    agg._run_context = _watchdog_context_for_root(temp_root)
    agg._snapshot_mode = True

    payload = agg.get_stream_pnl("2026-05-12")
    by_stream = {row["stream"]: row for row in payload["streams"]}

    assert by_stream["CL1"]["source"] == "execution_journal_fallback"
    assert by_stream["CL1"]["open_positions"] == 2
    assert by_stream["CL1"]["open_count"] == 1
    assert by_stream["CL1"]["entry_price"] == 102.11
    assert by_stream["ES2"]["source"] == "execution_journal_fallback"
    assert by_stream["ES2"]["open_positions"] == 0
    assert by_stream["ES2"]["entry_price"] == 7421.0


def test_watchdog_hydrates_stop_protection_for_active_journal_intent_from_feed():
    temp_root = _workspace_temp_dir()
    feed_file = temp_root / "logs" / "robot" / "frontend_feed.jsonl"
    feed_file.parent.mkdir(parents=True, exist_ok=True)
    feed_file.write_text(
        "\n".join(
            [
                json.dumps(
                    {
                        "event_type": "ORDER_SUBMIT_SUCCESS",
                        "timestamp_utc": "2026-05-07T16:01:04+00:00",
                        "data": {
                            "intent_id": "intent-protected",
                            "broker_order_id": "entry-order",
                            "order_type": "ENTRY_STOP",
                        },
                    }
                ),
                json.dumps(
                    {
                        "event_type": "ORDER_SUBMIT_SUCCESS",
                        "timestamp_utc": "2026-05-07T16:17:06+00:00",
                        "data": {
                            "intent_id": "intent-protected",
                            "broker_order_id": "target-order",
                            "order_type": "TARGET",
                        },
                    }
                ),
                json.dumps(
                    {
                        "event_type": "ORDER_SUBMIT_SUCCESS",
                        "timestamp_utc": "2026-05-07T16:17:06+00:00",
                        "data": {
                            "intent_id": "intent-protected",
                            "broker_order_id": "stop-order",
                            "order_type": "PROTECTIVE_STOP",
                        },
                    }
                ),
            ]
        )
        + "\n",
        encoding="utf-8",
    )

    sm = WatchdogStateManager()
    sm.update_intent_exposure(
        "intent-protected",
        "NG2",
        "MNG",
        "Long",
        entry_filled_qty=2,
        entry_filled_at_utc=datetime.now(timezone.utc) - timedelta(seconds=30),
        trading_date="2026-05-07",
    )

    agg = aggregator_main.WatchdogAggregator.__new__(aggregator_main.WatchdogAggregator)
    agg._snapshot_mode = True
    agg._state_manager = sm
    agg._run_context = WatchdogRunContext(
        project_root=temp_root,
        persistence_base=temp_root,
        run_id="test",
        is_run_scoped=True,
        robot_logs_dir=feed_file.parent,
        frontend_feed_file=feed_file,
        slot_journals_dir=temp_root / "state" / "stream_journals",
        execution_journals_dir=temp_root / "state" / "execution_journals",
        execution_summaries_dir=temp_root / "derived" / "execution_summaries",
    )

    assert sm.compute_unprotected_positions()
    assert agg._hydrate_protective_acknowledgements_from_feed() == 1
    assert sm.compute_unprotected_positions() == []


def test_execution_policy_lookup_uses_repo_root_configs(monkeypatch):
    temp_root = _workspace_temp_dir()
    config_dir = temp_root / "configs"
    config_dir.mkdir(parents=True, exist_ok=True)
    (config_dir / "execution_policy.json").write_text(
        json.dumps(
            {
                "canonical_markets": {
                    "NG": {"execution_instruments": {"NG": {"enabled": False}, "MNG": {"enabled": True}}},
                    "RTY": {"execution_instruments": {"RTY": {"enabled": False}, "M2K": {"enabled": True}}},
                }
            }
        ),
        encoding="utf-8",
    )

    monkeypatch.setattr(state_manager_module, "QTSW2_ROOT", temp_root)
    monkeypatch.setattr(state_manager_module, "_execution_instrument_cache", None)
    monkeypatch.setattr(aggregator_main, "QTSW2_ROOT", temp_root)
    monkeypatch.setattr(aggregator_main, "_execution_instrument_cache", None)

    assert state_manager_module._get_execution_instrument_for_canonical("NG") == "MNG"
    assert aggregator_main._get_execution_instrument_for_canonical("RTY2") == "M2K"


def test_historical_stream_reference_ignores_wall_clock_placeholder_state_time():
    reference = _derive_stream_state_reference_utc(
        [
            {
                "trading_date": "2026-04-27",
                "stream": "NG1",
                "state": "RANGE_BUILDING",
                "state_entry_time_utc": "2026-05-05T20:58:54+00:00",
                "slot_time_utc": "2026-04-27T14:00:00+00:00",
            },
            {
                "trading_date": "2026-04-27",
                "stream": "ES1",
                "state": "",
                "state_entry_time_utc": "2026-05-05T20:59:00+00:00",
                "slot_time_utc": "2026-04-27T13:00:00+00:00",
            },
        ],
        "2026-04-27",
    )

    assert reference == "2026-04-27T14:00:00+00:00"
