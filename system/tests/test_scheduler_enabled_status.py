import logging
import sys
from pathlib import Path
import importlib.util


SYSTEM_ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(SYSTEM_ROOT))


class _DummyConfig:
    qtsw2_root = Path(r"C:\Users\jakej\QTSW2")


class _DummyOrchestrator:
    config = _DummyConfig()


def test_scheduler_is_disabled_when_windows_task_is_missing(monkeypatch):
    scheduler_path = SYSTEM_ROOT / "modules" / "orchestrator" / "scheduler.py"
    spec = importlib.util.spec_from_file_location("test_scheduler_module", scheduler_path)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(module)
    Scheduler = module.Scheduler

    scheduler = Scheduler(Path("configs/schedule.json"), _DummyOrchestrator(), logging.getLogger("test"))
    monkeypatch.setattr(
        scheduler,
        "_load_state",
        lambda: {"scheduler_enabled": True, "last_changed_timestamp": None, "last_changed_by": "dashboard"},
    )
    monkeypatch.setattr(
        scheduler,
        "_check_windows_task_status",
        lambda: {"exists": False, "enabled": False, "state": "NotFound"},
    )

    assert scheduler.is_enabled() is False


def test_scheduler_falls_back_to_state_when_windows_status_is_unknown(monkeypatch):
    scheduler_path = SYSTEM_ROOT / "modules" / "orchestrator" / "scheduler.py"
    spec = importlib.util.spec_from_file_location("test_scheduler_module", scheduler_path)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(module)
    Scheduler = module.Scheduler

    scheduler = Scheduler(Path("configs/schedule.json"), _DummyOrchestrator(), logging.getLogger("test"))
    monkeypatch.setattr(
        scheduler,
        "_load_state",
        lambda: {"scheduler_enabled": True, "last_changed_timestamp": None, "last_changed_by": "dashboard"},
    )
    monkeypatch.setattr(
        scheduler,
        "_check_windows_task_status",
        lambda: {"exists": None, "enabled": None, "state": "Error"},
    )

    assert scheduler.is_enabled() is True
