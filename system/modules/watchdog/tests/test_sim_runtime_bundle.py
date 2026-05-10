from __future__ import annotations

import json
import sys
import uuid
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[4]))

from tools.sim_runtime_bundle import SimBundleOptions, create_sim_bundle


def _write_json(path: Path, obj: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(obj), encoding="utf-8")


def _workspace_temp_dir() -> Path:
    base = Path.cwd() / "tmp" / "pytest_sim_bundle"
    base.mkdir(parents=True, exist_ok=True)
    path = base / uuid.uuid4().hex
    path.mkdir(parents=True, exist_ok=False)
    return path


def _make_sim_root() -> Path:
    root = _workspace_temp_dir() / "qtsw2"
    _write_json(
        root / "data" / "session" / "session_authority.json",
        {
            "mode": "manual",
            "session_trading_date": "2026-05-06",
            "locked": True,
            "source": "user",
            "metadata": {"requested_replay": False},
        },
    )
    _write_json(
        root / "data" / "timetable" / "timetable_current.json",
        {
            "session_trading_date": "2026-05-06",
            "source": "matrix_ui",
            "timetable_hash": "abc123",
            "streams": [
                {"stream": "NQ1", "enabled": True},
                {"stream": "NG1", "enabled": False},
                {"stream": "RTY2", "enabled": True},
            ],
        },
    )
    _write_json(root / "configs" / "robot" / "kill_switch.json", {"enabled": False})
    _write_json(root / "configs" / "execution_policy.json", {"mode": "SIM"})
    _write_json(
        root / "state" / "stream_journals" / "2026-05-06_NQ1.json",
        {
            "TradingDate": "2026-05-06",
            "Stream": "NQ1",
            "LastState": "RANGE_LOCKED",
            "Committed": False,
            "EntryDetected": False,
        },
    )
    (root / "logs" / "robot").mkdir(parents=True, exist_ok=True)
    (root / "logs" / "robot" / "robot_ENGINE.jsonl").write_text(
        json.dumps(
            {
                "ts_utc": "2026-05-06T12:00:00+00:00",
                "level": "INFO",
                "event": "ROBOT_BUILD_SIGNATURE",
                "data": {"assembly_location": str(root / "missing" / "Robot.Core.dll")},
            }
        )
        + "\n"
        + json.dumps(
            {
                "ts_utc": "2026-05-06T12:05:00+00:00",
                "level": "INFO",
                "event": "BAR_REJECTION_SUMMARY",
                "message": "BAR_REJECTION_SUMMARY",
                "data": {"summary": {"MNQ": "total_rejected = 0"}},
            }
        )
        + "\n"
        + json.dumps(
            {
                "level": "ERROR",
                "event": "HISTORICAL_RESIDUE_WITHOUT_TIMESTAMP",
                "message": "old undated error should not count for this trading date",
            }
        )
        + "\n",
        encoding="utf-8",
    )
    (root / "runs").mkdir(parents=True, exist_ok=True)
    (root / "runs" / "LATEST_RUN.txt").write_text(".\n", encoding="utf-8")
    return root


def test_sim_runtime_bundle_creates_snapshot_and_latest_sim_pointer():
    root = _make_sim_root()

    result = create_sim_bundle(
        SimBundleOptions(
            project_root=root,
            output_root=root / "runs",
            run_id="sim_test_run",
            now_utc=datetime(2026, 5, 6, 13, 0, tzinfo=timezone.utc),
        )
    )

    run_root = Path(result["run_root"])
    summary = json.loads((run_root / "summary.json").read_text(encoding="utf-8"))
    manifest = json.loads((run_root / "SIM_BUNDLE_MANIFEST.json").read_text(encoding="utf-8"))

    assert summary["run_id"] == "sim_test_run"
    assert summary["mode"] == "SIM"
    assert summary["status"] == "WARN"
    assert summary["status_reason"] == "SIM_SNAPSHOT_NOT_FINAL"
    assert summary["instruments"] == ["NQ1", "RTY2"]
    assert summary["trades"] == 0
    assert summary["pnl"] is None
    assert summary["errors"] == 0
    assert summary["enabled_streams"] == ["NQ1", "RTY2"]
    assert summary["timetable"]["timetable_hash"] == "abc123"
    assert summary["key_counts"]["order_rejections"] == 0
    assert (run_root / "state" / "stream_journals" / "2026-05-06_NQ1.json").is_file()
    assert (run_root / "logs" / "robot" / "robot_ENGINE.jsonl").is_file()
    assert (root / "runs" / "LATEST_SIM_RUN.txt").read_text(encoding="utf-8").strip() == "runs/sim_test_run"
    assert (root / "runs" / "LATEST_RUN.txt").read_text(encoding="utf-8").strip() == "."
    assert manifest["latest_run_pointer_updated"] is False


def test_sim_runtime_bundle_finalize_can_update_latest_run_pointer():
    root = _make_sim_root()

    result = create_sim_bundle(
        SimBundleOptions(
            project_root=root,
            output_root=root / "runs",
            run_id="sim_final",
            finalize=True,
            now_utc=datetime(2026, 5, 6, 22, 0, tzinfo=timezone.utc),
        )
    )

    run_root = Path(result["run_root"])
    summary = json.loads((run_root / "summary.json").read_text(encoding="utf-8"))

    assert summary["status"] == "OK"
    assert summary["status_reason"] == "SIM_FINALIZED_SNAPSHOT"
    assert summary["finalized"] is True
    assert (root / "runs" / "LATEST_SIM_RUN.txt").read_text(encoding="utf-8").strip() == "runs/sim_final"
    assert (root / "runs" / "LATEST_RUN.txt").read_text(encoding="utf-8").strip() == "runs/sim_final"


def test_sim_runtime_bundle_dry_run_writes_nothing():
    root = _make_sim_root()

    result = create_sim_bundle(
        SimBundleOptions(
            project_root=root,
            output_root=root / "runs",
            run_id="sim_dry",
            dry_run=True,
            now_utc=datetime(2026, 5, 6, 14, 0, tzinfo=timezone.utc),
        )
    )

    assert result["dry_run"] is True
    assert not (root / "runs" / "sim_dry").exists()
    assert not (root / "runs" / "LATEST_SIM_RUN.txt").exists()


def test_sim_runtime_bundle_counts_real_order_rejection():
    root = _make_sim_root()
    with (root / "logs" / "robot" / "robot_ENGINE.jsonl").open("a", encoding="utf-8") as f:
        f.write(
            json.dumps(
                {
                    "ts_utc": "2026-05-06T12:10:00+00:00",
                    "level": "ERROR",
                    "event": "ORDER_REJECTED",
                    "message": "ORDER_REJECTED",
                }
            )
            + "\n"
        )

    result = create_sim_bundle(
        SimBundleOptions(
            project_root=root,
            output_root=root / "runs",
            run_id="sim_rejected",
            now_utc=datetime(2026, 5, 6, 14, 5, tzinfo=timezone.utc),
        )
    )

    summary = json.loads((Path(result["run_root"]) / "summary.json").read_text(encoding="utf-8"))

    assert summary["status"] == "FAIL"
    assert summary["status_reason"] == "SIM_EXECUTION_SAFETY_EVENT"
    assert summary["key_counts"]["order_rejections"] == 1
