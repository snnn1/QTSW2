#!/usr/bin/env python3
"""Audit robot run lifecycle truth without mutating run artifacts.

The tool cross-checks timetable streams, stream journals, execution journals,
summary counts, and robot log severity counts. It is intentionally read-only.
"""

from __future__ import annotations

import argparse
import json
import re
from collections import Counter, defaultdict
from pathlib import Path
from typing import Any


DATE_RE = re.compile(r"\d{4}-\d{2}-\d{2}")


def load_json(path: Path) -> dict[str, Any]:
    try:
        return json.loads(path.read_text(encoding="utf-8-sig"))
    except Exception:
        return {}


def iter_jsonl(path: Path):
    try:
        with path.open("r", encoding="utf-8-sig") as fh:
            for line_no, line in enumerate(fh, start=1):
                line = line.strip()
                if not line:
                    continue
                try:
                    yield line_no, json.loads(line)
                except Exception:
                    continue
    except Exception:
        return


def resolve_run(root: Path, run_arg: str | None) -> Path:
    if run_arg:
        p = Path(run_arg)
        return p if p.is_absolute() else (root / p)

    latest = root / "runs" / "LATEST_RUN.txt"
    value = latest.read_text(encoding="utf-8-sig").strip()
    p = Path(value)
    return p if p.is_absolute() else (root / p)


def stream_key_date(slot_instance_key: str | None) -> str:
    if not slot_instance_key:
        return ""
    matches = DATE_RE.findall(slot_instance_key)
    return matches[-1] if matches else ""


def remaining_qty(entry: dict[str, Any]) -> int:
    entry_qty = entry.get("EntryFilledQuantityTotal") or entry.get("FillQuantity") or 0
    exit_qty = entry.get("ExitFilledQuantityTotal") or 0
    try:
        return max(0, int(entry_qty) - int(exit_qty))
    except Exception:
        return 0


def is_filled(entry: dict[str, Any]) -> bool:
    return bool(entry.get("EntryFilled")) or int(entry.get("EntryFilledQuantityTotal") or 0) > 0


def is_completed_filled_trade(entry: dict[str, Any]) -> bool:
    return bool(entry.get("TradeCompleted")) and is_filled(entry)


def norm(value: Any) -> str:
    return str(value or "").strip()


def load_timetables(run_root: Path) -> dict[str, dict[str, Any]]:
    result: dict[str, dict[str, Any]] = {}
    table_dir = run_root / "playback_scenario" / "timetables"
    if not table_dir.exists():
        return result

    for path in sorted(table_dir.glob("timetable_*.json")):
        data = load_json(path)
        date = norm(data.get("trading_date") or data.get("session_trading_date"))
        if not date:
            date = path.stem.replace("timetable_", "")
        enabled = []
        for stream in data.get("streams") or []:
            if stream.get("enabled") is True:
                enabled.append(
                    {
                        "stream": norm(stream.get("stream")),
                        "slot_time": norm(stream.get("slot_time")),
                    }
                )
        result[date] = {"path": str(path), "enabled_streams": enabled}
    return result


def load_stream_journals(run_root: Path) -> dict[tuple[str, str], dict[str, Any]]:
    result: dict[tuple[str, str], dict[str, Any]] = {}
    journal_dir = run_root / "state" / "stream_journals"
    if not journal_dir.exists():
        return result

    for path in sorted(journal_dir.glob("*.json")):
        if path.name.startswith("_"):
            continue
        data = load_json(path)
        date = norm(data.get("TradingDate"))
        stream = norm(data.get("Stream"))
        if date and stream:
            data["_path"] = str(path)
            result[(date, stream)] = data
    return result


def load_execution_journals(run_root: Path) -> dict[tuple[str, str], list[dict[str, Any]]]:
    result: dict[tuple[str, str], list[dict[str, Any]]] = defaultdict(list)
    journal_dir = run_root / "state" / "execution_journals"
    if not journal_dir.exists():
        return result

    for path in sorted(journal_dir.glob("*.json")):
        data = load_json(path)
        date = norm(data.get("TradingDate"))
        stream = norm(data.get("Stream"))
        if date and stream:
            data["_path"] = str(path)
            result[(date, stream)].append(data)
    return result


def count_robot_log_severity(run_root: Path) -> Counter:
    counts: Counter = Counter()
    robot_dir = run_root / "logs" / "robot"
    if not robot_dir.exists():
        return counts

    for path in sorted(robot_dir.glob("robot_*.jsonl")):
        for _line_no, row in iter_jsonl(path):
            level = norm(row.get("level")).upper()
            if level:
                counts[level] += 1
    return counts


def audit_run(run_root: Path) -> dict[str, Any]:
    summary = load_json(run_root / "summary.json")
    timetables = load_timetables(run_root)
    streams = load_stream_journals(run_root)
    executions = load_execution_journals(run_root)
    severities = count_robot_log_severity(run_root)

    findings: list[dict[str, Any]] = []
    per_stream: list[dict[str, Any]] = []

    for date, table in timetables.items():
        for expected in table["enabled_streams"]:
            stream = expected["stream"]
            sj = streams.get((date, stream))
            rows = executions.get((date, stream), [])
            completed_filled = [r for r in rows if is_completed_filled_trade(r)]
            open_filled = [r for r in rows if is_filled(r) and not r.get("TradeCompleted") and remaining_qty(r) > 0]
            submitted_open = [r for r in rows if r.get("EntrySubmitted") and not r.get("Rejected") and not r.get("TradeCompleted") and not is_filled(r)]

            slot_date = stream_key_date(norm(sj.get("SlotInstanceKey")) if sj else "")
            row = {
                "date": date,
                "stream": stream,
                "slot_time": expected["slot_time"],
                "journal_exists": sj is not None,
                "committed": bool(sj.get("Committed")) if sj else False,
                "commit_reason": sj.get("CommitReason") if sj else None,
                "slot_status": sj.get("SlotStatus") if sj else None,
                "last_state": sj.get("LastState") if sj else None,
                "slot_instance_key": sj.get("SlotInstanceKey") if sj else None,
                "slot_instance_date": slot_date,
                "prior_journal_key": sj.get("PriorJournalKey") if sj else None,
                "completed_filled_trades": len(completed_filled),
                "open_filled_trades": len(open_filled),
                "submitted_open_intents": len(submitted_open),
                "open_qty": sum(remaining_qty(r) for r in open_filled),
            }
            per_stream.append(row)

            if sj is None:
                findings.append({"type": "enabled_stream_missing_journal", "date": date, "stream": stream})
                continue

            if slot_date and slot_date != date:
                findings.append(
                    {
                        "type": "enabled_stream_occupied_by_prior_slot",
                        "date": date,
                        "stream": stream,
                        "slot_instance_key": sj.get("SlotInstanceKey"),
                        "prior_journal_key": sj.get("PriorJournalKey"),
                        "committed": bool(sj.get("Committed")),
                        "commit_reason": sj.get("CommitReason"),
                    }
                )

            if completed_filled and not sj.get("Committed"):
                findings.append(
                    {
                        "type": "completed_trade_stream_not_committed",
                        "date": date,
                        "stream": stream,
                        "completed_intents": [r.get("IntentId") for r in completed_filled],
                        "last_state": sj.get("LastState"),
                        "slot_status": sj.get("SlotStatus"),
                    }
                )

            if open_filled and sj.get("Committed"):
                findings.append(
                    {
                        "type": "committed_stream_has_open_filled_trade",
                        "date": date,
                        "stream": stream,
                        "open_intents": [r.get("IntentId") for r in open_filled],
                        "open_qty": sum(remaining_qty(r) for r in open_filled),
                        "commit_reason": sj.get("CommitReason"),
                    }
                )

    key_counts = summary.get("key_counts") or {}
    summary_errors = int(key_counts.get("robot_log_errors") or 0)
    summary_critical = int(key_counts.get("robot_log_critical") or 0)
    if severities.get("ERROR", 0) != summary_errors or severities.get("CRITICAL", 0) != summary_critical:
        findings.append(
            {
                "type": "summary_robot_log_severity_mismatch",
                "summary_error": summary_errors,
                "summary_critical": summary_critical,
                "actual_error": severities.get("ERROR", 0),
                "actual_critical": severities.get("CRITICAL", 0),
            }
        )

    return {
        "run_root": str(run_root),
        "summary": {
            "status": summary.get("status"),
            "status_reason": summary.get("status_reason"),
            "verdict_class": summary.get("verdict_class"),
            "recommended_action": summary.get("recommended_action"),
            "confidence": summary.get("confidence"),
            "trades": summary.get("trades"),
            "errors": summary.get("errors"),
            "key_counts": key_counts,
        },
        "counts": {
            "timetable_dates": len(timetables),
            "stream_journals": len(streams),
            "execution_stream_keys": len(executions),
            "findings": len(findings),
            "robot_log_errors": severities.get("ERROR", 0),
            "robot_log_critical": severities.get("CRITICAL", 0),
        },
        "findings": findings,
        "per_stream": per_stream,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Audit QTSW2 run lifecycle truth")
    parser.add_argument("--run", help="Run root path. Defaults to runs/LATEST_RUN.txt")
    parser.add_argument("--pretty", action="store_true", help="Pretty-print JSON")
    args = parser.parse_args()

    root = Path.cwd()
    run_root = resolve_run(root, args.run).resolve()
    result = audit_run(run_root)
    print(json.dumps(result, indent=2 if args.pretty else None, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
