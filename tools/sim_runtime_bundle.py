#!/usr/bin/env python3
"""
Create a run-style evidence bundle from the normal SIM/runtime rolling state.

Playback already writes isolated runs/<run_id>/ artifacts from inside the robot.
Normal SIM intentionally writes to project-root state/log/data paths, so this tool
snapshots those rolling files into runs/sim_<date>_<timestamp>_<id>/ without
changing robot execution or persistence behavior.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import subprocess
import sys
import uuid
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, Iterable, List, Optional, Sequence


SUMMARY_FILE = "summary.json"
NOTES_FILE = "NOTES.md"
KEY_EVENTS_FILE = "KEY_EVENTS.jsonl"
AUDIT_MANIFEST_FILE = "AUDIT_MANIFEST.json"
SIM_BUNDLE_MANIFEST_FILE = "SIM_BUNDLE_MANIFEST.json"
LATEST_SIM_POINTER_FILE = "LATEST_SIM_RUN.txt"
LATEST_RUN_POINTER_FILE = "LATEST_RUN.txt"


def _utcnow() -> datetime:
    return datetime.now(timezone.utc)


def _json_dump(path: Path, obj: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(obj, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def _read_json_object(path: Path) -> Optional[Dict[str, Any]]:
    try:
        with path.open(encoding="utf-8") as f:
            obj = json.load(f)
    except (OSError, json.JSONDecodeError):
        return None
    return obj if isinstance(obj, dict) else None


def _sha256_file(path: Path) -> Optional[str]:
    try:
        h = hashlib.sha256()
        with path.open("rb") as f:
            for chunk in iter(lambda: f.read(1024 * 1024), b""):
                h.update(chunk)
        return h.hexdigest()
    except OSError:
        return None


def _safe_stat(path: Path) -> Dict[str, Any]:
    try:
        st = path.stat()
    except OSError as exc:
        return {"exists": False, "error": str(exc)}
    return {
        "exists": True,
        "size": st.st_size,
        "mtime_utc": datetime.fromtimestamp(st.st_mtime, timezone.utc).isoformat(),
    }


def _repo_root_from_tool() -> Path:
    return Path(__file__).resolve().parents[1]


def _run_git(project_root: Path, args: Sequence[str]) -> Optional[str]:
    try:
        completed = subprocess.run(
            ["git", *args],
            cwd=str(project_root),
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            text=True,
            check=False,
            timeout=5,
        )
    except (OSError, subprocess.TimeoutExpired):
        return None
    if completed.returncode != 0:
        return None
    value = completed.stdout.strip()
    return value or None


def _session_trading_date(project_root: Path) -> str:
    session = _read_json_object(project_root / "data" / "session" / "session_authority.json")
    if session:
        for key in ("session_trading_date", "trading_date", "date"):
            value = str(session.get(key) or "").strip()
            if value:
                return value
    timetable = _read_json_object(project_root / "data" / "timetable" / "timetable_current.json")
    if timetable:
        for key in ("session_trading_date", "trading_date", "date"):
            value = str(timetable.get(key) or "").strip()
            if value:
                return value
    return _utcnow().date().isoformat()


def _enabled_streams(project_root: Path) -> List[str]:
    timetable = _read_json_object(project_root / "data" / "timetable" / "timetable_current.json")
    streams = timetable.get("streams") if isinstance(timetable, dict) else None
    if not isinstance(streams, list):
        return []
    out: List[str] = []
    for row in streams:
        if not isinstance(row, dict) or row.get("enabled") is not True:
            continue
        name = str(row.get("stream") or row.get("stream_id") or "").strip()
        if name:
            out.append(name)
    return sorted(set(out))


def _iter_jsonl(path: Path) -> Iterable[Dict[str, Any]]:
    try:
        with path.open(encoding="utf-8") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    obj = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if isinstance(obj, dict):
                    yield obj
    except OSError:
        return


def _latest_robot_build_signature(project_root: Path, trading_date: str) -> Optional[Dict[str, Any]]:
    latest: Optional[Dict[str, Any]] = None
    for path in sorted((project_root / "logs" / "robot").glob("robot_ENGINE*.jsonl")):
        for row in _iter_jsonl(path):
            if row.get("event") != "ROBOT_BUILD_SIGNATURE":
                continue
            ts = str(row.get("ts_utc") or "")
            if trading_date and ts and not ts.startswith(trading_date):
                continue
            latest = row
    if latest is None:
        for path in sorted((project_root / "logs" / "robot").glob("robot_ENGINE*.jsonl")):
            for row in _iter_jsonl(path):
                if row.get("event") == "ROBOT_BUILD_SIGNATURE":
                    latest = row
    return latest


def _deployed_dll_probe(project_root: Path, build_signature: Optional[Dict[str, Any]]) -> Dict[str, Any]:
    data = build_signature.get("data") if isinstance(build_signature, dict) else None
    signature_path = str(data.get("assembly_location") or "").strip() if isinstance(data, dict) else ""
    candidates: List[Path] = []
    if signature_path:
        candidates.append(Path(signature_path))
    candidates.append(Path.home() / "Documents" / "NinjaTrader 8" / "bin" / "Custom" / "Robot.Core.dll")
    candidates.append(project_root / "system" / "RobotCore_For_NinjaTrader" / "bin" / "Release" / "net48" / "Robot.Core.dll")

    selected = next((p for p in candidates if p.is_file()), candidates[0])
    stat = _safe_stat(selected)
    result: Dict[str, Any] = {
        "path": str(selected),
        "exists": stat.get("exists") is True,
        "size": stat.get("size"),
        "last_write_utc": stat.get("mtime_utc"),
        "sha256": _sha256_file(selected) if stat.get("exists") is True else None,
        "robot_build_signature_event": build_signature,
    }
    if stat.get("error"):
        result["error"] = stat["error"]
    return result


def _timetable_hash(timetable: Dict[str, Any]) -> Optional[Any]:
    value = timetable.get("timetable_hash") or timetable.get("hash")
    if value:
        return value
    metadata = timetable.get("metadata")
    if isinstance(metadata, dict):
        return metadata.get("timetable_hash")
    return None


def _collect_stream_journals(project_root: Path, trading_date: str) -> List[Dict[str, Any]]:
    rows: List[Dict[str, Any]] = []
    for path in sorted((project_root / "state" / "stream_journals").glob(f"{trading_date}_*.json")):
        doc = _read_json_object(path)
        if not doc:
            continue
        rows.append(
            {
                "path": str(path.relative_to(project_root)),
                "stream": doc.get("Stream") or doc.get("stream"),
                "last_state": doc.get("LastState") or doc.get("last_state"),
                "committed": doc.get("Committed"),
                "terminal_state": doc.get("TerminalState"),
                "entry_detected": doc.get("EntryDetected"),
                "protection_submitted": doc.get("ProtectionSubmitted"),
                "protection_accepted": doc.get("ProtectionAccepted"),
                "forced_flatten_timestamp": doc.get("ForcedFlattenTimestamp"),
                "last_update_utc": doc.get("LastUpdateUtc"),
            }
        )
    return rows


def _count_execution_journals(project_root: Path, trading_date: str) -> int:
    paths = set()
    for base in (project_root / "state" / "execution_journals", project_root / "data" / "execution_journals"):
        for path in base.glob(f"{trading_date}_*.json"):
            paths.add(path.resolve())
    return len(paths)


def _execution_trade_count(project_root: Path, trading_date: str) -> int:
    count = 0
    paths = set()
    for base in (project_root / "state" / "execution_journals", project_root / "data" / "execution_journals"):
        for path in base.glob(f"{trading_date}_*.json"):
            paths.add(path.resolve())
    for path in paths:
        doc = _read_json_object(path)
        if not doc:
            continue
        if doc.get("TradeCompleted") is True or doc.get("EntryFilled") is True:
            count += 1
    return count


def _log_counts(project_root: Path, trading_date: str) -> Dict[str, int]:
    counts = {
        "robot_log_errors": 0,
        "robot_log_critical": 0,
        "robot_log_hard_critical": 0,
        "protective_failed": 0,
        "execution_blocked": 0,
        "order_rejections": 0,
        "diagnostic_contradictions": 0,
    }
    order_rejection_events = {
        "ORDER_REJECTED",
        "ENTRY_REJECTED",
        "ENTRY_ORDER_REJECTED_PRICE_INVALID",
        "FLATTEN_ORDER_REJECTED",
        "REENTRY_FAILED_SUBMIT_REJECTED",
        "REENTRY_PROTECTION_FAILED_SUBMIT_REJECTED",
        "INTENT_LOOKUP_MISS_AT_SUBMIT_REJECTED",
        "INTENT_ID_MISMATCH_REJECTED",
        "INTENT_REGISTRATION_REJECTED",
    }
    protective_rejection_events = {
        "PROTECTIVE_ORDER_REJECTED_FLATTENED",
        "PROTECTIVE_ORDER_REJECTED_INTENT_NOT_FOUND",
        "REENTRY_PROTECTION_FAILED_SUBMIT_REJECTED",
    }
    def matches_trading_date(row: Dict[str, Any]) -> bool:
        if not trading_date:
            return True
        ts = str(row.get("ts_utc") or row.get("timestamp_utc") or "").strip()
        if ts:
            return ts.startswith(trading_date)
        td = str(row.get("trading_date") or "").strip()
        if td == trading_date:
            return True
        data = row.get("data")
        if isinstance(data, dict):
            return str(data.get("trading_date") or "").strip() == trading_date
        return False

    for path in sorted((project_root / "logs" / "robot").glob("robot_*.jsonl")):
        for row in _iter_jsonl(path):
            if not matches_trading_date(row):
                continue
            level = str(row.get("level") or "").upper()
            event = str(row.get("event") or "").upper()
            message = str(row.get("message") or "").upper()
            if level == "ERROR":
                counts["robot_log_errors"] += 1
            if level == "CRITICAL":
                counts["robot_log_critical"] += 1
            if "HARD_CRITICAL" in event or "HARD CRITICAL" in message:
                counts["robot_log_hard_critical"] += 1
            if event in protective_rejection_events or ("PROTECTIVE" in event and "FAILED" in event):
                counts["protective_failed"] += 1
            if "EXECUTION_BLOCKED" in event:
                counts["execution_blocked"] += 1
            if event in order_rejection_events:
                counts["order_rejections"] += 1
            if "CONTRADICTION" in event or "CONTRADICTION" in message:
                counts["diagnostic_contradictions"] += 1
    return counts


def _copy_file(src: Path, dst: Path) -> Dict[str, Any]:
    record: Dict[str, Any] = {"source": str(src), "target": str(dst), "status": "missing"}
    if not src.is_file():
        return record
    try:
        dst.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(src, dst)
        record.update(
            {
                "status": "copied",
                "size": dst.stat().st_size,
                "sha256": _sha256_file(dst),
            }
        )
    except OSError as exc:
        record.update({"status": "failed", "error": str(exc)})
    return record


def _copy_glob(project_root: Path, run_root: Path, pattern: str, records: List[Dict[str, Any]]) -> None:
    for src in sorted(project_root.glob(pattern)):
        if not src.is_file():
            continue
        try:
            rel = src.relative_to(project_root)
        except ValueError:
            rel = Path("external") / src.name
        records.append(_copy_file(src, run_root / rel))


def _write_pointer(project_root: Path, pointer_name: str, run_root: Path) -> None:
    rel = run_root.relative_to(project_root).as_posix()
    pointer = project_root / "runs" / pointer_name
    pointer.parent.mkdir(parents=True, exist_ok=True)
    pointer.write_text(rel + "\n", encoding="utf-8")


@dataclass(frozen=True)
class SimBundleOptions:
    project_root: Path
    output_root: Path
    run_id: Optional[str] = None
    finalize: bool = False
    update_latest: bool = False
    now_utc: datetime = field(default_factory=_utcnow)
    dry_run: bool = False


def _make_run_id(trading_date: str, now_utc: datetime) -> str:
    ts = now_utc.strftime("%Y%m%dT%H%M%SZ")
    return f"sim_{trading_date}_{ts}_{uuid.uuid4().hex[:8]}"


def _status_from_counts(finalize: bool, log_counts: Dict[str, int], active_latches: int, kill_switch_enabled: bool) -> Dict[str, str]:
    if kill_switch_enabled or active_latches > 0 or log_counts.get("robot_log_critical", 0) > 0 or log_counts.get("robot_log_hard_critical", 0) > 0:
        return {
            "status": "FAIL",
            "status_reason": "SIM_RUNTIME_BLOCKER",
            "verdict_class": "OPERATOR_REVIEW",
            "recommended_action": "STOP",
            "confidence": "HIGH",
        }
    if log_counts.get("protective_failed", 0) > 0 or log_counts.get("order_rejections", 0) > 0:
        return {
            "status": "FAIL",
            "status_reason": "SIM_EXECUTION_SAFETY_EVENT",
            "verdict_class": "OPERATOR_REVIEW",
            "recommended_action": "STOP",
            "confidence": "HIGH",
        }
    if not finalize:
        return {
            "status": "WARN",
            "status_reason": "SIM_SNAPSHOT_NOT_FINAL",
            "verdict_class": "RUNTIME_SNAPSHOT",
            "recommended_action": "MONITOR",
            "confidence": "MEDIUM",
        }
    if log_counts.get("robot_log_errors", 0) > 0 or log_counts.get("execution_blocked", 0) > 0:
        return {
            "status": "WARN",
            "status_reason": "SIM_RUNTIME_WARNINGS",
            "verdict_class": "OPERATOR_REVIEW",
            "recommended_action": "INVESTIGATE",
            "confidence": "MEDIUM",
        }
    return {
        "status": "OK",
        "status_reason": "SIM_FINALIZED_SNAPSHOT",
        "verdict_class": "SIM_RUNTIME_PROOF_CANDIDATE",
        "recommended_action": "MONITOR",
        "confidence": "MEDIUM",
    }


def create_sim_bundle(options: SimBundleOptions) -> Dict[str, Any]:
    project_root = options.project_root.resolve()
    output_root = options.output_root.resolve()
    trading_date = _session_trading_date(project_root)
    run_id = options.run_id or _make_run_id(trading_date, options.now_utc)
    run_root = output_root / run_id

    session = _read_json_object(project_root / "data" / "session" / "session_authority.json") or {}
    timetable = _read_json_object(project_root / "data" / "timetable" / "timetable_current.json") or {}
    kill_switch = _read_json_object(project_root / "configs" / "robot" / "kill_switch.json") or {}
    active_latches = len([p for p in (project_root / "data" / "risk_latches").glob("*.json") if p.is_file()])
    build_signature = _latest_robot_build_signature(project_root, trading_date)
    dll = _deployed_dll_probe(project_root, build_signature)
    streams = _enabled_streams(project_root)
    stream_journals = _collect_stream_journals(project_root, trading_date)
    log_counts = _log_counts(project_root, trading_date)
    error_count = (
        log_counts.get("robot_log_errors", 0)
        + log_counts.get("robot_log_critical", 0)
        + log_counts.get("robot_log_hard_critical", 0)
    )
    status = _status_from_counts(
        options.finalize,
        log_counts,
        active_latches,
        bool(kill_switch.get("enabled") is True),
    )

    summary = {
        "run_id": run_id,
        "date": trading_date,
        "mode": "SIM",
        **status,
        "bundle_kind": "SIM_RUNTIME_SNAPSHOT",
        "finalized": options.finalize,
        "created_at_utc": options.now_utc.isoformat(),
        "instruments": streams,
        "trades": _execution_trade_count(project_root, trading_date),
        "pnl": None,
        "errors": error_count,
        "persistence_base": str(run_root),
        "source_persistence_base": str(project_root),
        "enabled_streams": streams,
        "stream_journals": stream_journals,
        "execution_journal_files": _count_execution_journals(project_root, trading_date),
        "deployed_runtime": dll,
        "session": {
            "session_trading_date": session.get("session_trading_date") or session.get("trading_date"),
            "mode": session.get("mode"),
            "locked": session.get("locked"),
            "source": session.get("source"),
            "metadata": session.get("metadata"),
        },
        "timetable": {
            "session_trading_date": timetable.get("session_trading_date") or timetable.get("trading_date"),
            "source": timetable.get("source"),
            "timetable_hash": _timetable_hash(timetable),
        },
        "key_counts": {
            **log_counts,
            "active_risk_latches": active_latches,
            "enabled_streams": len(streams),
            "stream_journals": len(stream_journals),
            "execution_journal_files": _count_execution_journals(project_root, trading_date),
        },
        "flags": {
            "kill_switch_enabled": kill_switch.get("enabled") is True,
            "has_active_risk_latches": active_latches > 0,
            "had_robot_log_error": log_counts.get("robot_log_errors", 0) > 0,
            "had_robot_log_critical": log_counts.get("robot_log_critical", 0) > 0,
            "had_robot_log_hard_critical": log_counts.get("robot_log_hard_critical", 0) > 0,
            "had_protective_failure": log_counts.get("protective_failed", 0) > 0,
            "had_execution_block": log_counts.get("execution_blocked", 0) > 0,
            "had_order_rejection": log_counts.get("order_rejections", 0) > 0,
            "had_diagnostic_contradiction": log_counts.get("diagnostic_contradictions", 0) > 0,
        },
    }

    audit_manifest = {
        "run_id": run_id,
        "trading_date": trading_date,
        "created_at_utc": options.now_utc.isoformat(),
        "persistence_base": str(run_root),
        "source_persistence_base": str(project_root),
        "isolated_playback": False,
        "audit_scope": "SIM_SNAPSHOT",
        "proof_level": "SIM/runtime snapshot after operator run; not a final shutdown proof unless finalized=true",
        "source_commit": _run_git(project_root, ["rev-parse", "HEAD"]),
        "source_dirty": _run_git(project_root, ["status", "--porcelain"]) not in (None, ""),
        "trusted_sources": {
            "engine_log": str(run_root / "logs" / "robot" / "robot_ENGINE.jsonl"),
            "instrument_logs_glob": str(run_root / "logs" / "robot" / "robot_*.jsonl"),
            "stream_journals": str(run_root / "state" / "stream_journals"),
            "execution_journals_state": str(run_root / "state" / "execution_journals"),
            "execution_journals_data": str(run_root / "data" / "execution_journals"),
            "execution_events": str(run_root / "events" / "execution_events" / trading_date),
            "ownership_events": str(run_root / "events" / "ownership_events" / trading_date),
            "ownership_snapshots": str(run_root / "events" / "ownership_snapshots" / trading_date),
            "orphan_fills": str(run_root / "events" / "orphan_fills" / trading_date),
            "deployed_robot_core_dll": str(run_root / "runtime" / "Robot.Core.dll"),
        },
        "deployed_runtime": dll,
    }

    copy_records: List[Dict[str, Any]] = []
    if options.dry_run:
        return {
            "dry_run": True,
            "run_id": run_id,
            "run_root": str(run_root),
            "summary": summary,
            "audit_manifest": audit_manifest,
        }

    run_root.mkdir(parents=True, exist_ok=False)
    _json_dump(run_root / SUMMARY_FILE, summary)
    _json_dump(run_root / AUDIT_MANIFEST_FILE, audit_manifest)
    (run_root / NOTES_FILE).write_text(
        "# SIM run notes\n\n"
        "Snapshot of normal SIM rolling state. Add operator notes before treating as final evidence.\n\n"
        "- \n",
        encoding="utf-8",
    )
    (run_root / KEY_EVENTS_FILE).write_text("", encoding="utf-8")

    for rel in (
        "data/session/session_authority.json",
        "data/timetable/timetable_current.json",
        "configs/execution_policy.json",
        "configs/robot/kill_switch.json",
        "runs/LATEST_RUN.txt",
    ):
        copy_records.append(_copy_file(project_root / rel, run_root / rel))

    patterns = [
        f"state/stream_journals/{trading_date}_*.json",
        f"state/execution_journals/{trading_date}_*.json",
        f"data/execution_journals/{trading_date}_*.json",
        f"events/execution_events/{trading_date}/**/*",
        f"events/ownership_events/{trading_date}/**/*",
        f"events/ownership_snapshots/{trading_date}/**/*",
        f"events/orphan_fills/{trading_date}/**/*",
        "data/risk_latches/*.json",
        "logs/robot/*.jsonl",
        "logs/health/*.jsonl",
        "logs/hydration/*.jsonl",
        "logs/ranges/*.jsonl",
        "logs/range_building/*.jsonl",
        "derived/execution_summaries/*.json",
        "data/execution_summaries/*.json",
    ]
    for pattern in patterns:
        _copy_glob(project_root, run_root, pattern, copy_records)

    dll_path = Path(str(dll.get("path") or ""))
    if dll_path.is_file():
        copy_records.append(_copy_file(dll_path, run_root / "runtime" / "Robot.Core.dll"))

    latest_run_pointer_updated = options.update_latest or options.finalize

    manifest = {
        "run_id": run_id,
        "trading_date": trading_date,
        "created_at_utc": options.now_utc.isoformat(),
        "project_root": str(project_root),
        "run_root": str(run_root),
        "finalized": options.finalize,
        "latest_sim_pointer": str(project_root / "runs" / LATEST_SIM_POINTER_FILE),
        "latest_run_pointer_updated": latest_run_pointer_updated,
        "copied": copy_records,
    }
    _json_dump(run_root / SIM_BUNDLE_MANIFEST_FILE, manifest)

    _write_pointer(project_root, LATEST_SIM_POINTER_FILE, run_root)
    if latest_run_pointer_updated:
        _write_pointer(project_root, LATEST_RUN_POINTER_FILE, run_root)

    return {
        "dry_run": False,
        "run_id": run_id,
        "run_root": str(run_root),
        "summary_path": str(run_root / SUMMARY_FILE),
        "audit_manifest_path": str(run_root / AUDIT_MANIFEST_FILE),
        "sim_bundle_manifest_path": str(run_root / SIM_BUNDLE_MANIFEST_FILE),
        "latest_sim_pointer": str(project_root / "runs" / LATEST_SIM_POINTER_FILE),
        "latest_run_pointer_updated": latest_run_pointer_updated,
        "summary": summary,
    }


def _parse_args(argv: Sequence[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project-root", type=Path, default=_repo_root_from_tool())
    parser.add_argument("--output-root", type=Path, default=None)
    parser.add_argument("--run-id", default=None)
    parser.add_argument("--finalize", action="store_true", help="Mark this as an end-of-session snapshot candidate.")
    parser.add_argument("--update-latest", action="store_true", help="Update runs/LATEST_RUN.txt as well as LATEST_SIM_RUN.txt. Finalized bundles always update it.")
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--json", action="store_true", help="Print JSON output.")
    return parser.parse_args(argv)


def main(argv: Sequence[str]) -> int:
    args = _parse_args(argv)
    project_root = args.project_root.resolve()
    output_root = (args.output_root or (project_root / "runs")).resolve()
    try:
        result = create_sim_bundle(
            SimBundleOptions(
                project_root=project_root,
                output_root=output_root,
                run_id=args.run_id,
                finalize=args.finalize,
                update_latest=args.update_latest,
                dry_run=args.dry_run,
            )
        )
    except Exception as exc:
        if args.json:
            print(json.dumps({"ok": False, "error": str(exc)}, indent=2))
        else:
            print(f"SIM bundle failed: {exc}", file=sys.stderr)
        return 1

    if args.json:
        print(json.dumps({"ok": True, **result}, indent=2))
    else:
        print(f"SIM bundle created: {result['run_root']}")
        print(f"summary: {result.get('summary_path', '(dry-run)')}")
        print(f"LATEST_SIM_RUN updated: {result.get('latest_sim_pointer', '(dry-run)')}")
        if result.get("latest_run_pointer_updated"):
            print("runs/LATEST_RUN.txt was updated")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
