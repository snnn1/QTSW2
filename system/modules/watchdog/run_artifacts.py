"""
Read-through helpers for engine run artifacts (summary.json, KEY_EVENTS.jsonl).
Does not recompute verdicts — reads files produced under the active persistence root.
"""
from __future__ import annotations

import json
import logging
import os
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

from .config import QTSW2_ROOT

logger = logging.getLogger(__name__)

SUMMARY_FILE = "summary.json"
KEY_EVENTS_FILE = "KEY_EVENTS.jsonl"


def get_watchdog_project_root() -> Path:
    """
    Directory that contains runs/ and data/ (engine project root).
    WATCHDOG_PROJECT_ROOT overrides. Otherwise prefer repo root (parent of modules/) when runs/ exists there.
    """
    env = os.environ.get("WATCHDOG_PROJECT_ROOT", "").strip()
    if env:
        p = Path(env)
        if p.is_dir():
            return p.resolve()
    # config.QTSW2_ROOT is parent of modules/ (often .../system); runs/ may live one level up
    parent = QTSW2_ROOT.parent
    if (parent / "runs").is_dir() or (parent / "data").is_dir():
        return parent.resolve()
    return QTSW2_ROOT.resolve()


def resolve_active_persistence_base() -> Optional[Path]:
    """
    Strict order:
    1) WATCHDOG_PERSISTENCE_BASE env if set and directory exists
    2) runs/LATEST_RUN.txt first line → path relative to QTSW2 root
    3) WATCHDOG_FALLBACK_ROOT env (default: QTSW2_ROOT)
    4) None if nothing resolves (caller returns unavailable)
    """
    proj = get_watchdog_project_root()

    env = os.environ.get("WATCHDOG_PERSISTENCE_BASE", "").strip()
    if env:
        p = Path(env)
        if p.is_dir():
            return p.resolve()

    latest = proj / "runs" / "LATEST_RUN.txt"
    if latest.is_file():
        try:
            line = latest.read_text(encoding="utf-8").strip().splitlines()[0].strip()
        except OSError as e:
            logger.debug("LATEST_RUN read failed: %s", e)
            line = ""
        if line:
            cand = (proj / line.replace("/", os.sep)).resolve()
            if cand.is_dir():
                return cand

    fb = os.environ.get("WATCHDOG_FALLBACK_ROOT", str(proj)).strip()
    if fb:
        p = Path(fb)
        if p.is_dir():
            return p.resolve()
    return None


def read_run_summary_json(root: Path) -> Optional[Dict[str, Any]]:
    path = root / SUMMARY_FILE
    if not path.is_file():
        return None
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except (OSError, json.JSONDecodeError) as e:
        logger.warning("read_run_summary_json: %s", e)
        return None


def read_key_events_tail(root: Path, limit: int = 50) -> List[Dict[str, Any]]:
    path = root / KEY_EVENTS_FILE
    if not path.is_file():
        return []
    try:
        text = path.read_text(encoding="utf-8")
    except OSError as e:
        logger.warning("read_key_events_tail: %s", e)
        return []
    lines = [ln for ln in text.splitlines() if ln.strip()]
    tail = lines[-limit:] if limit > 0 else lines
    out: List[Dict[str, Any]] = []
    for ln in tail:
        try:
            out.append(json.loads(ln))
        except json.JSONDecodeError:
            continue
    return out


def _summary_mtime(path: Path) -> int:
    try:
        return path.stat().st_mtime_ns
    except OSError:
        return 0


def discover_recent_run_summaries(limit: int = 5) -> List[Tuple[Path, Dict[str, Any]]]:
    """Newest summary.json files under known run locations (mtime desc)."""
    proj = get_watchdog_project_root()
    candidates: List[Tuple[int, Path]] = []

    playback = proj / "data" / "playback"
    if playback.is_dir():
        for d in playback.iterdir():
            if d.is_dir():
                s = d / SUMMARY_FILE
                if s.is_file():
                    candidates.append((_summary_mtime(s), s))

    runs_sub = proj / "runs"
    if runs_sub.is_dir():
        for d in runs_sub.iterdir():
            if d.is_dir():
                s = d / SUMMARY_FILE
                if s.is_file():
                    candidates.append((_summary_mtime(s), s))

    root_summary = proj / SUMMARY_FILE
    if root_summary.is_file():
        candidates.append((_summary_mtime(root_summary), root_summary))

    candidates.sort(key=lambda x: -x[0])
    result: List[Tuple[Path, Dict[str, Any]]] = []
    for _, spath in candidates[:limit]:
        try:
            with open(spath, encoding="utf-8") as f:
                data = json.load(f)
            result.append((spath, data))
        except (OSError, json.JSONDecodeError):
            continue
    return result


def compute_stability_label(summaries: List[Dict[str, Any]]) -> str:
    """
    Display-only: last N verdicts (engine summary.json only).
    STABLE: all OK
    DEGRADED: 1–2 WARN, no FAIL
    UNSTABLE: any FAIL or 3+ WARN (repeated warn pattern)
    """
    if not summaries:
        return "UNKNOWN"
    statuses = [str(s.get("status") or "").upper() for s in summaries[:5]]
    if any(s == "FAIL" for s in statuses):
        return "UNSTABLE"
    warns = sum(1 for s in statuses if s == "WARN")
    if warns >= 3:
        return "UNSTABLE"
    if 1 <= warns <= 2:
        return "DEGRADED"
    if all(s == "OK" for s in statuses):
        return "STABLE"
    return "DEGRADED"


def validate_peek_run_root(candidate: Optional[str]) -> Optional[Path]:
    """
    Optional UI peek: directory must resolve under runs/, data/playback/, or project root
    (for a root-level summary.json run).
    """
    if not candidate or not str(candidate).strip():
        return None
    proj = get_watchdog_project_root()
    try:
        p = Path(candidate).expanduser().resolve()
    except OSError:
        return None
    if not p.is_dir():
        return None
    proj_r = proj.resolve()
    allowed = [
        (proj_r / "runs").resolve(),
        (proj_r / "data" / "playback").resolve(),
        proj_r,
    ]
    for ar in allowed:
        try:
            p.relative_to(ar)
            return p
        except ValueError:
            continue
    return None
