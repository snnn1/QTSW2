"""
Read-through helpers for engine run artifacts (summary.json, KEY_EVENTS.jsonl).
Does not recompute verdicts — reads files produced under the active persistence root.
"""
from __future__ import annotations

import json
import logging
import os
import hashlib
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

from .config import QTSW2_ROOT
from .platform_diagnostics import augment_run_summary_with_platform_diagnostics

logger = logging.getLogger(__name__)

SUMMARY_FILE = "summary.json"
AUTHORITY_SHUTDOWN_FRAME_FILE = "AUTHORITY_SHUTDOWN_FRAME.json"
KEY_EVENTS_FILE = "KEY_EVENTS.jsonl"
PLAYBACK_SCENARIO_POINTER_FILE = Path("configs") / "robot" / "playback_scenario_current.json"


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
    root = QTSW2_ROOT.resolve()
    if (root / "runs").is_dir() or (root / "data").is_dir():
        return root

    # Backward-compat fallback for deployments where the actual engine root sits one level up.
    parent = QTSW2_ROOT.parent
    if (parent / "runs").is_dir() or (parent / "data").is_dir():
        return parent.resolve()
    return root


def resolve_active_persistence_base() -> Optional[Path]:
    """
    Strict order:
    1) WATCHDOG_PERSISTENCE_BASE env if set and directory exists
    2) live non-replay session authority + matching root timetable → project root
    3) runs/LATEST_RUN.txt first line → path relative to QTSW2 root
    4) WATCHDOG_FALLBACK_ROOT env (default: QTSW2_ROOT)
    5) None if nothing resolves (caller returns unavailable)
    """
    proj = get_watchdog_project_root()

    env = os.environ.get("WATCHDOG_PERSISTENCE_BASE", "").strip()
    if env:
        p = Path(env)
        if p.is_dir():
            return p.resolve()

    scenario_base = resolve_active_playback_scenario_persistence_base(proj)
    if scenario_base is not None:
        return scenario_base

    if _root_live_session_active(proj):
        return proj.resolve()

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


def _read_json_object(path: Path) -> Optional[Dict[str, Any]]:
    if not path.is_file():
        return None
    try:
        with open(path, encoding="utf-8") as f:
            data = json.load(f)
        return data if isinstance(data, dict) else None
    except (OSError, json.JSONDecodeError):
        return None


def _sha256_file(path: Path) -> Optional[str]:
    try:
        h = hashlib.sha256()
        with path.open("rb") as f:
            for chunk in iter(lambda: f.read(1024 * 1024), b""):
                h.update(chunk)
        return h.hexdigest()
    except OSError:
        return None


def _resolve_manifest_path(proj: Path, raw: object) -> Optional[Path]:
    if raw is None:
        return None
    value = str(raw).strip()
    if not value:
        return None
    path = Path(value)
    if not path.is_absolute():
        path = proj / path
    try:
        return path.resolve()
    except OSError:
        return None


def resolve_active_playback_scenario_persistence_base(project_root: Optional[Path] = None) -> Optional[Path]:
    """
    Resolve the operator-selected multi-day Playback scenario run root.

    This follows the config pointer the robot reads at startup. If the pointer is
    stale, missing, or hash-invalid, callers fall back to normal live/LATEST_RUN
    resolution rather than inventing a run.
    """
    proj = (project_root or get_watchdog_project_root()).resolve()
    pointer = _read_json_object(proj / PLAYBACK_SCENARIO_POINTER_FILE)
    if not pointer:
        return None
    if str(pointer.get("mode") or "").strip().lower() != "multi_day_carryover":
        return None

    manifest_path = _resolve_manifest_path(proj, pointer.get("manifest_path"))
    if manifest_path is None or not manifest_path.is_file():
        return None

    expected_hash = str(pointer.get("manifest_sha256") or "").strip().lower()
    if expected_hash:
        actual_hash = (_sha256_file(manifest_path) or "").lower()
        if actual_hash != expected_hash:
            logger.warning(
                "WATCHDOG_PLAYBACK_SCENARIO_POINTER_HASH_MISMATCH expected=%s actual=%s manifest=%s",
                expected_hash,
                actual_hash or "unavailable",
                manifest_path,
            )
            return None

    manifest = _read_json_object(manifest_path) or {}
    run_id = str(pointer.get("run_id") or manifest.get("run_id") or "").strip()
    try:
        run_root = manifest_path.parent.parent.resolve()
        if run_id:
            expected_root = (proj / "runs" / run_id).resolve()
            if expected_root == run_root and expected_root.is_dir():
                return expected_root
        if run_root.is_dir() and run_root.parent.name.lower() == "runs":
            return run_root
    except OSError:
        return None
    return None


def _is_replay_doc(doc: Optional[Dict[str, Any]]) -> bool:
    if not isinstance(doc, dict):
        return False
    if doc.get("replay") is True:
        return True
    meta = doc.get("metadata")
    return isinstance(meta, dict) and meta.get("replay") is True


def _root_live_session_active(proj: Path) -> bool:
    """
    Prefer root live logs when the operator has published a non-replay live session.

    Playback runs keep ``runs/LATEST_RUN.txt`` pointed at the last inspected playback
    artifact. During SIM this stale pointer must not pull the watchdog back into a
    run-scoped replay context while root ``data/session`` and ``data/timetable`` are
    explicitly live.
    """
    session = _read_json_object(proj / "data" / "session" / "session_authority.json")
    timetable = _read_json_object(proj / "data" / "timetable" / "timetable_current.json")
    if not session or not timetable:
        return False

    session_date = str(session.get("session_trading_date") or session.get("trading_date") or "").strip()
    timetable_date = str(timetable.get("session_trading_date") or timetable.get("trading_date") or "").strip()
    if not session_date or not timetable_date or session_date != timetable_date:
        return False

    if _is_replay_doc(session) or _is_replay_doc(timetable):
        return False

    meta = session.get("metadata")
    if isinstance(meta, dict) and meta.get("requested_replay") is True:
        return False

    return True


def read_run_summary_json(root: Path, *, augment_platform: bool = False) -> Optional[Dict[str, Any]]:
    path = root / SUMMARY_FILE
    if not path.is_file():
        return None
    try:
        with open(path, encoding="utf-8") as f:
            data = json.load(f)
        if augment_platform and isinstance(data, dict):
            data = augment_run_summary_with_platform_diagnostics(data, root)
        if isinstance(data, dict):
            authority_frame = read_authority_shutdown_frame_json(root)
            if authority_frame is not None:
                data = dict(data)
                data["authority_shutdown_frame_available"] = True
                data["authority_shutdown_frame"] = authority_frame
            elif "authority_shutdown_frame_available" not in data:
                data = dict(data)
                data["authority_shutdown_frame_available"] = False
        return data
    except (OSError, json.JSONDecodeError) as e:
        logger.warning("read_run_summary_json: %s", e)
        return None


def read_authority_shutdown_frame_json(root: Path) -> Optional[Dict[str, Any]]:
    path = root / AUTHORITY_SHUTDOWN_FRAME_FILE
    if not path.is_file():
        return None
    try:
        with open(path, encoding="utf-8") as f:
            data = json.load(f)
        return data if isinstance(data, dict) else None
    except (OSError, json.JSONDecodeError) as e:
        logger.warning("read_authority_shutdown_frame_json: %s", e)
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
