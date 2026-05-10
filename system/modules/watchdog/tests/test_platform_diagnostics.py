from __future__ import annotations

import json
import sys
import uuid
from datetime import datetime
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[3]))

from modules.watchdog.platform_diagnostics import (
    _normalize_windows_event_rows,
    augment_run_summary_with_platform_diagnostics,
)


def _workspace_temp_dir() -> Path:
    base = Path.cwd() / "tmp" / "pytest_watchdog"
    base.mkdir(parents=True, exist_ok=True)
    path = base / uuid.uuid4().hex
    path.mkdir(parents=True, exist_ok=False)
    return path


def test_platform_diagnostics_marks_summary_from_ninjatrader_trace_signal():
    root = _workspace_temp_dir()
    run_root = root / "runs" / "run_with_platform_freeze"
    (run_root / "logs" / "robot").mkdir(parents=True)
    (run_root / "summary.json").write_text(
        json.dumps(
            {
                "run_id": "run_with_platform_freeze",
                "date": "2026-04-29",
                "mode": "SIM",
                "status": "FAIL",
                "status_reason": "OPEN_EXPOSURE_AT_SHUTDOWN",
                "recommended_action": "STOP",
                "confidence": "MEDIUM",
                "key_counts": {},
                "flags": {"had_crash_or_freeze_signal": False},
            }
        ),
        encoding="utf-8",
    )
    (run_root / "logs" / "robot" / "robot_ENGINE.jsonl").write_text(
        json.dumps({"event": "ENGINE_TIMER_HEARTBEAT"}) + "\n",
        encoding="utf-8",
    )
    nt_root = root / "NinjaTrader 8"
    trace_dir = nt_root / "trace"
    trace_dir.mkdir(parents=True)
    now = datetime.now()
    trace_line = (
        f"{now:%Y-%m-%d %H:%M:%S}:704 *************** unhandled exception trapped ***************\n"
        f"{now:%Y-%m-%d %H:%M:%S}:705 System.ComponentModel.Win32Exception: Not enough quota is available to process this command\n"
    )
    (trace_dir / "trace.20260510.00003.txt").write_text(trace_line, encoding="utf-8")

    summary = json.loads((run_root / "summary.json").read_text(encoding="utf-8"))
    augmented = augment_run_summary_with_platform_diagnostics(summary, run_root, nt_root=nt_root)

    assert augmented["status"] == "FAIL"
    assert augmented["status_reason"] == "OPEN_EXPOSURE_AT_SHUTDOWN"
    assert augmented["recommended_action"] == "STOP"
    assert augmented["confidence"] == "HIGH"
    assert augmented["flags"]["had_crash_or_freeze_signal"] is True
    assert augmented["flags"]["had_ninjatrader_platform_exception"] is True
    assert augmented["watchdog_overlay"]["status_reason"] == "CRASH_OR_FREEZE_SIGNAL"
    assert augmented["watchdog_platform_diagnostics"]["had_platform_crash_or_freeze_signal"] is True
    assert augmented["watchdog_platform_diagnostics"]["events"]


def test_platform_diagnostics_normalizes_windows_application_hang():
    events = _normalize_windows_event_rows(
        [
            {
                "TimeCreated": "2026-05-10T04:53:55",
                "Id": 1002,
                "ProviderName": "Application Hang",
                "RecordId": 123,
                "Message": "The program NinjaTrader.exe version 8.1.6.3 stopped interacting with Windows and was closed.",
            }
        ],
        max_events=5,
    )

    assert len(events) == 1
    assert events[0]["source"] == "windows_event_log"
    assert events[0]["signal"] == "WINDOWS_APPLICATION_HANG"
    assert events[0]["event_id"] == 1002
