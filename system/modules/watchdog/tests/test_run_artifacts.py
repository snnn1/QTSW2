from __future__ import annotations

import sys
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[3]))

from modules.watchdog import run_artifacts


def _workspace_temp_dir() -> Path:
    base = Path.cwd() / "tmp" / "pytest_watchdog"
    base.mkdir(parents=True, exist_ok=True)
    path = base / uuid.uuid4().hex
    path.mkdir(parents=True, exist_ok=False)
    return path


def test_get_watchdog_project_root_prefers_repo_root_when_repo_has_runs(monkeypatch):
    parent_root = _workspace_temp_dir()
    repo_root = parent_root / "QTSW2"
    (repo_root / "runs").mkdir(parents=True)
    (repo_root / "data").mkdir(parents=True)
    (parent_root / "data").mkdir(parents=True)

    monkeypatch.delenv("WATCHDOG_PROJECT_ROOT", raising=False)
    monkeypatch.setattr(run_artifacts, "QTSW2_ROOT", repo_root)

    resolved = run_artifacts.get_watchdog_project_root()

    assert resolved == repo_root.resolve()
