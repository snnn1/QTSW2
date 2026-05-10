from __future__ import annotations

import copy
import json
import os
import re
import subprocess
from datetime import datetime, timedelta
from pathlib import Path
from typing import Any, Dict, Iterable, List, Optional, Tuple


NT_TIMESTAMP_RE = re.compile(
    r"^(?P<date>\d{4}-\d{2}-\d{2})[ T](?P<time>\d{2}:\d{2}:\d{2})[:.](?P<ms>\d{3})"
)

PLATFORM_CRASH_PATTERNS = (
    "unhandled exception",
    "not enough quota is available to process this command",
    "system.componentmodel.win32exception",
    "hwndtarget.updatewindowsettings",
    "hwndtarget.updatewindowpos",
)

WINDOWS_EVENT_PROVIDER_HINTS = (
    "application hang",
    "application error",
    "windows error reporting",
)

WINDOWS_EVENT_IDS = (1000, 1001, 1002)


def _candidate_nt_roots() -> List[Path]:
    roots: List[Path] = []
    env = os.environ.get("WATCHDOG_NINJATRADER_ROOT", "").strip()
    if env:
        roots.append(Path(env))
    home = Path.home()
    roots.append(home / "Documents" / "NinjaTrader 8")
    roots.append(home / "OneDrive" / "Documents" / "NinjaTrader 8")
    out: List[Path] = []
    seen: set[str] = set()
    for root in roots:
        key = str(root).lower()
        if key in seen:
            continue
        seen.add(key)
        out.append(root)
    return out


def _run_wall_window(run_root: Path) -> Optional[Tuple[datetime, datetime]]:
    candidates: List[float] = []
    direct = [
        run_root / "summary.json",
        run_root / "AUDIT_MANIFEST.json",
        run_root / "KEY_EVENTS.jsonl",
    ]
    for path in direct:
        try:
            if path.is_file():
                candidates.append(path.stat().st_mtime)
        except OSError:
            continue
    for pattern in (
        "logs/robot/*.jsonl",
        "state/stream_journals/*.json",
        "state/execution_journals/*.json",
    ):
        for path in run_root.glob(pattern):
            try:
                candidates.append(path.stat().st_mtime)
            except OSError:
                continue
    if not candidates:
        return None
    start = datetime.fromtimestamp(min(candidates)) - timedelta(minutes=20)
    end = datetime.fromtimestamp(max(candidates)) + timedelta(minutes=20)
    return start, end


def _parse_nt_timestamp(line: str) -> Optional[datetime]:
    match = NT_TIMESTAMP_RE.search(line)
    if not match:
        return None
    raw = f"{match.group('date')} {match.group('time')}.{match.group('ms')}"
    try:
        return datetime.strptime(raw, "%Y-%m-%d %H:%M:%S.%f")
    except ValueError:
        return None


def _line_matches_platform_crash(line: str) -> Optional[str]:
    lower = line.lower()
    for pattern in PLATFORM_CRASH_PATTERNS:
        if pattern in lower:
            return pattern.upper().replace(" ", "_")
    return None


def _classify_windows_event(row: Dict[str, Any]) -> Optional[str]:
    provider = str(row.get("ProviderName") or row.get("Provider") or "").strip().lower()
    message = str(row.get("Message") or "").strip().lower()
    try:
        event_id = int(row.get("Id") or row.get("EventId") or 0)
    except (TypeError, ValueError):
        event_id = 0

    combined = f"{provider}\n{message}"
    if "ninjatrader.exe" not in combined and "ninjatrader" not in combined:
        return None

    if event_id == 1002 or "application hang" in provider or "stopped interacting with windows" in message:
        return "WINDOWS_APPLICATION_HANG"
    if event_id == 1000 or "application error" in provider:
        return "WINDOWS_APPLICATION_ERROR"
    if event_id == 1001 or "windows error reporting" in provider:
        return "WINDOWS_ERROR_REPORTING"
    if any(hint in provider for hint in WINDOWS_EVENT_PROVIDER_HINTS):
        return "WINDOWS_PLATFORM_EVENT"
    return None


def _normalize_windows_event_rows(rows: Any, max_events: int) -> List[Dict[str, Any]]:
    if isinstance(rows, dict):
        rows = [rows]
    if not isinstance(rows, list):
        return []

    events: List[Dict[str, Any]] = []
    for row in rows:
        if not isinstance(row, dict):
            continue
        signal = _classify_windows_event(row)
        if not signal:
            continue
        events.append(
            {
                "source": "windows_event_log",
                "provider": str(row.get("ProviderName") or row.get("Provider") or ""),
                "event_id": row.get("Id") or row.get("EventId"),
                "record_id": row.get("RecordId"),
                "timestamp_local": str(row.get("TimeCreated") or ""),
                "signal": signal,
                "message": str(row.get("Message") or "").strip()[:500],
            }
        )
        if len(events) >= max_events:
            break
    return events


def _query_windows_event_log(window: Optional[Tuple[datetime, datetime]], max_events: int) -> Tuple[List[Dict[str, Any]], Optional[str]]:
    if os.name != "nt" or window is None or max_events <= 0:
        return [], None

    start = window[0].strftime("%Y-%m-%dT%H:%M:%S")
    end = window[1].strftime("%Y-%m-%dT%H:%M:%S")
    ids = ",".join(str(event_id) for event_id in WINDOWS_EVENT_IDS)
    script = f"""
$events = Get-WinEvent -FilterHashtable @{{LogName='Application'; StartTime=[datetime]'{start}'; EndTime=[datetime]'{end}'; Id={ids}}} -ErrorAction SilentlyContinue |
    Where-Object {{ ($_.ProviderName -match 'Application Hang|Application Error|Windows Error Reporting') -and ($_.Message -match 'NinjaTrader|NinjaTrader.exe') }} |
    Select-Object TimeCreated, Id, ProviderName, RecordId, Message
if ($null -eq $events) {{
    '[]'
}} else {{
    $events | ConvertTo-Json -Depth 3 -Compress
}}
"""
    try:
        completed = subprocess.run(
            ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", script],
            check=False,
            capture_output=True,
            text=True,
            timeout=8,
        )
    except Exception as exc:
        return [], f"{type(exc).__name__}: {exc}"

    if completed.returncode != 0:
        err = (completed.stderr or completed.stdout or "").strip()
        return [], err[:500] if err else f"powershell_exit_{completed.returncode}"

    raw = (completed.stdout or "").strip()
    if not raw:
        return [], None
    try:
        rows = json.loads(raw)
    except json.JSONDecodeError as exc:
        return [], f"json_decode_error: {exc}"
    return _normalize_windows_event_rows(rows, max_events), None


def _iter_nt_files(root: Path) -> Iterable[Path]:
    for subdir, glob in (("log", "log.*.txt"), ("trace", "trace.*.txt")):
        base = root / subdir
        if not base.is_dir():
            continue
        for path in sorted(base.glob(glob)):
            if path.is_file():
                yield path


def detect_ninjatrader_platform_signals(
    run_root: Path,
    nt_root: Optional[Path] = None,
    max_events: int = 20,
) -> Dict[str, Any]:
    """
    Detect NinjaTrader platform-level crash/freeze evidence near a run's wall-clock window.

    Robot summary.json only sees robot log events. A WPF/platform exception can freeze or
    terminate NinjaTrader while the robot itself remains clean. This detector is read-only
    and uses NinjaTrader log/trace files as operator evidence.
    """
    run_root = run_root.resolve()
    window = _run_wall_window(run_root)
    roots = [nt_root] if nt_root is not None else _candidate_nt_roots()
    roots = [root.resolve() for root in roots if root is not None]

    events: List[Dict[str, Any]] = []
    files_scanned: List[str] = []
    for root in roots:
        for path in _iter_nt_files(root):
            try:
                stat = path.stat()
            except OSError:
                continue
            if window is not None:
                mtime = datetime.fromtimestamp(stat.st_mtime)
                if mtime < window[0] or mtime > window[1]:
                    continue
            files_scanned.append(str(path))
            try:
                with path.open(encoding="utf-8", errors="replace") as f:
                    for line_no, raw_line in enumerate(f, 1):
                        signal = _line_matches_platform_crash(raw_line)
                        if not signal:
                            continue
                        ts = _parse_nt_timestamp(raw_line)
                        if window is not None and ts is not None:
                            if ts < window[0] or ts > window[1]:
                                continue
                        events.append(
                            {
                                "source": "ninjatrader_platform_log",
                                "path": str(path),
                                "line": line_no,
                                "timestamp_local": ts.isoformat() if ts else None,
                                "signal": signal,
                                "message": raw_line.strip()[:500],
                            }
                        )
                        if len(events) >= max_events:
                            break
            except OSError:
                continue
            if len(events) >= max_events:
                break
        if len(events) >= max_events:
            break

    windows_event_log_error: Optional[str] = None
    if len(events) < max_events:
        windows_events, windows_event_log_error = _query_windows_event_log(window, max_events - len(events))
        events.extend(windows_events)

    evidence_level = "platform-log-proven"
    if any(e.get("source") == "windows_event_log" for e in events):
        evidence_level = "windows-event-log-proven"

    return {
        "available": True,
        "evidence_level": evidence_level,
        "had_platform_crash_or_freeze_signal": bool(events),
        "run_wall_window_local": {
            "start": window[0].isoformat() if window else None,
            "end": window[1].isoformat() if window else None,
        },
        "nt_roots_scanned": [str(root) for root in roots],
        "files_scanned": files_scanned,
        "windows_event_log_scanned": os.name == "nt" and window is not None,
        "windows_event_log_error": windows_event_log_error,
        "events": events,
    }


def augment_run_summary_with_platform_diagnostics(
    summary: Dict[str, Any],
    run_root: Path,
    nt_root: Optional[Path] = None,
) -> Dict[str, Any]:
    data = copy.deepcopy(summary)
    diagnostics = detect_ninjatrader_platform_signals(run_root, nt_root=nt_root)
    data["watchdog_platform_diagnostics"] = diagnostics
    if not diagnostics.get("had_platform_crash_or_freeze_signal"):
        return data

    flags = data.setdefault("flags", {})
    if isinstance(flags, dict):
        flags["had_crash_or_freeze_signal"] = True
        flags["had_ninjatrader_platform_exception"] = True

    key_counts = data.setdefault("key_counts", {})
    if isinstance(key_counts, dict):
        key_counts["ninjatrader_platform_exception_events"] = len(diagnostics.get("events") or [])

    data["watchdog_overlay"] = {
        "status": "FAIL",
        "status_reason": "CRASH_OR_FREEZE_SIGNAL",
        "recommended_action": "STOP",
        "confidence": "HIGH",
        "proof_level": diagnostics.get("evidence_level") or "platform-log-proven",
    }

    if str(data.get("status") or "").upper() != "FAIL":
        data["status"] = "FAIL"
        data["status_reason"] = "CRASH_OR_FREEZE_SIGNAL"
        data["verdict_class"] = "CRASH_OR_FREEZE"
        data["recommended_action"] = "STOP"
        data["confidence"] = "HIGH"
    else:
        data["recommended_action"] = "STOP"
        data["confidence"] = "HIGH"
    return data
