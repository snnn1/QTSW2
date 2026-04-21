from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from typing import Optional

from .config import QTSW2_ROOT
from .run_artifacts import get_watchdog_project_root, resolve_active_persistence_base


@dataclass(frozen=True)
class WatchdogRunContext:
    project_root: Path
    persistence_base: Path
    run_id: Optional[str]
    is_run_scoped: bool
    robot_logs_dir: Path
    frontend_feed_file: Path
    slot_journals_dir: Path
    execution_journals_dir: Path
    execution_summaries_dir: Path

    def ranges_file(self, trading_date: str) -> Path:
        return self.robot_logs_dir / f"ranges_{trading_date}.jsonl"


def _extract_run_id_from_persistence_base(persistence_base: Path) -> Optional[str]:
    try:
        parts = persistence_base.resolve().parts
    except OSError:
        return None
    for idx, part in enumerate(parts[:-1]):
        if part.lower() == "runs":
            run_id = parts[idx + 1].strip()
            return run_id or None
    return None


def _prefer_existing_path(*candidates: Path) -> Path:
    for candidate in candidates:
        if candidate.exists():
            return candidate
    return candidates[0]


def build_run_context(persistence_base: Optional[Path] = None) -> WatchdogRunContext:
    project_root = get_watchdog_project_root().resolve()
    resolved_base = (persistence_base or resolve_active_persistence_base() or QTSW2_ROOT).resolve()
    run_id = _extract_run_id_from_persistence_base(resolved_base)
    robot_logs_dir = resolved_base / "logs" / "robot"
    slot_journals_dir = _prefer_existing_path(
        resolved_base / "state" / "stream_journals",
        robot_logs_dir / "journal",
    )
    execution_journals_dir = _prefer_existing_path(
        resolved_base / "state" / "execution_journals",
        resolved_base / "data" / "execution_journals",
    )
    execution_summaries_dir = _prefer_existing_path(
        resolved_base / "derived" / "execution_summaries",
        resolved_base / "data" / "execution_summaries",
    )
    return WatchdogRunContext(
        project_root=project_root,
        persistence_base=resolved_base,
        run_id=run_id,
        is_run_scoped=run_id is not None,
        robot_logs_dir=robot_logs_dir,
        frontend_feed_file=robot_logs_dir / "frontend_feed.jsonl",
        slot_journals_dir=slot_journals_dir,
        execution_journals_dir=execution_journals_dir,
        execution_summaries_dir=execution_summaries_dir,
    )


def resolve_active_run_context() -> WatchdogRunContext:
    return build_run_context()
