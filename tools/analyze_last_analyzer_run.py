#!/usr/bin/env python3
"""
Analyze the last pipeline run that included the analyzer stage.
Extracts timing, file counts, and per-instrument duration to diagnose slow runs.
"""

import json
import sys
from pathlib import Path
from datetime import datetime
from collections import defaultdict

QTSW2_ROOT = Path(__file__).parent.parent
EVENTS_DIR = QTSW2_ROOT / "automation" / "logs" / "events"
PIPELINE_LOGS = QTSW2_ROOT / "automation" / "logs"
RUNS_JSONL = QTSW2_ROOT / "automation" / "logs" / "runs" / "runs.jsonl"


def find_pipeline_event_files():
    """Find pipeline_*.jsonl event files, sorted by modification time (newest first)."""
    if not EVENTS_DIR.exists():
        return []
    files = list(EVENTS_DIR.glob("pipeline_*.jsonl"))
    return sorted(files, key=lambda p: p.stat().st_mtime, reverse=True)


def load_events(filepath: Path):
    """Load JSONL events from file."""
    events = []
    with open(filepath, "r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                events.append(json.loads(line))
            except json.JSONDecodeError:
                pass
    return events


def analyze_run(events: list, run_id: str):
    """Extract analyzer timing from events."""
    analyzer_start = None
    analyzer_success = None
    analyzer_failure = None
    file_starts = {}   # instrument -> timestamp
    file_finishes = {}  # instrument -> (timestamp, duration_sec, status)

    for e in events:
        if e.get("run_id") != run_id:
            continue
        stage = e.get("stage", "")
        event = e.get("event", "")
        ts = e.get("timestamp", "")
        data = e.get("data") or {}

        if stage == "analyzer":
            if event == "start":
                analyzer_start = ts
            elif event == "success":
                analyzer_success = ts
            elif event == "failure":
                analyzer_failure = ts
            elif event == "file_start":
                inst = data.get("instrument", "")
                if inst:
                    file_starts[inst] = ts
            elif event == "file_finish":
                inst = data.get("instrument", "")
                if inst:
                    duration = data.get("duration_seconds")
                    status = data.get("status", "unknown")
                    file_finishes[inst] = (ts, duration, status)

    return {
        "analyzer_start": analyzer_start,
        "analyzer_success": analyzer_success,
        "analyzer_failure": analyzer_failure,
        "file_starts": file_starts,
        "file_finishes": file_finishes,
    }


def parse_ts(ts: str):
    """Parse ISO timestamp to datetime for duration calc."""
    if not ts:
        return None
    try:
        # Handle both with and without Z
        ts = ts.replace("Z", "+00:00")
        return datetime.fromisoformat(ts)
    except Exception:
        return None


def find_matching_pipeline_log(run_id: str):
    """Find pipeline log file that contains this run_id."""
    short_id = run_id[:8] if run_id else ""
    for log in PIPELINE_LOGS.glob("pipeline_*.log"):
        if log.name == "pipeline_standalone.log":
            continue
        try:
            content = log.read_text(encoding="utf-8", errors="ignore")
            if run_id in content or short_id in content:
                return log
        except Exception:
            pass
    return None


def extract_from_log(log_path: Path, run_id: str):
    """Extract analyzer stats from pipeline log."""
    info = {
        "processed_files": None,
        "instruments": None,
        "analyzer_start_line": None,
        "analyzer_end_line": None,
        "log_excerpt": [],
    }
    short_id = run_id[:8] if run_id else ""
    in_analyzer = False
    lines = []

    with open(log_path, "r", encoding="utf-8", errors="ignore") as f:
        for i, line in enumerate(f, 1):
            if "Starting analyzer stage" in line and (short_id in line or run_id in line):
                in_analyzer = True
                info["analyzer_start_line"] = i
            if in_analyzer:
                lines.append((i, line.rstrip()))
                if "Found " in line and " processed file" in line:
                    parts = line.split("Found ")[1].split(" processed")[0]
                    try:
                        info["processed_files"] = int(parts.replace(",", "").strip())
                    except (ValueError, IndexError):
                        pass
                if "Running parallel analyzer for " in line:
                    rest = line.split("instrument(s):")[-1].strip()
                    info["instruments"] = rest
                if "Analyzer completed" in line or ("merger" in line.lower() and "Starting" in line):
                    info["analyzer_end_line"] = i
                    break
                if "SUCCESS" in line and "Translator" not in line and "Analyzer" in line:
                    info["analyzer_end_line"] = i

    info["log_excerpt"] = lines[-30:] if len(lines) > 30 else lines
    return info


def main():
    print("=" * 70)
    print("Last Analyzer Run Analysis")
    print("=" * 70)

    event_files = find_pipeline_event_files()
    if not event_files:
        print("\nNo pipeline event files found in", EVENTS_DIR)
        return 1

    # Find most recent run that had analyzer
    best_run = None
    best_events = None
    best_file = None

    for ef in event_files:
        events = load_events(ef)
        run_id = None
        for e in events:
            if e.get("stage") == "analyzer" and e.get("event") == "start":
                run_id = e.get("run_id")
                break
        if run_id:
            best_run = run_id
            best_events = events
            best_file = ef
            break

    if not best_run:
        print("\nNo pipeline run with analyzer stage found in event files.")
        print("Recent runs may have skipped analyzer (no new translated data).")
        return 0

    print(f"\nRun ID: {best_run}")
    print(f"Event file: {best_file.name}")

    analysis = analyze_run(best_events, best_run)

    # Timing summary
    print("\n--- Analyzer Stage Timing ---")
    print(f"  Start:  {analysis['analyzer_start']}")
    print(f"  End:    {analysis['analyzer_success'] or analysis['analyzer_failure'] or '(incomplete - no file_finish events)'}")

    if analysis["file_finishes"]:
        print("\n--- Per-Instrument Duration ---")
        total_duration = 0
        for inst, (ts, dur, status) in sorted(analysis["file_finishes"].items()):
            dur_str = f"{dur:.1f}s" if dur is not None else "?"
            print(f"  {inst}: {dur_str} ({status})")
            if dur is not None:
                total_duration = max(total_duration, dur)  # Parallel, so wall clock = max
        if total_duration > 0:
            print(f"\n  Wall-clock (longest instrument): {total_duration:.1f}s ({total_duration/60:.1f} min)")
    else:
        print("\n  No file_finish events - run may have been interrupted or still in progress.")

    # Log file analysis
    log_info = {}
    log_path = find_matching_pipeline_log(best_run)
    if log_path:
        log_info = extract_from_log(log_path, best_run)
        print("\n--- From Pipeline Log ---")
        print(f"  Log file: {log_path.name}")
        if log_info["processed_files"]:
            print(f"  Processed files: {log_info['processed_files']:,}")
        if log_info["instruments"]:
            print(f"  Instruments: {log_info['instruments']}")
        if log_info["analyzer_start_line"]:
            print(f"  Analyzer start at line: {log_info['analyzer_start_line']}")
        if log_info["analyzer_end_line"]:
            print(f"  Analyzer end at line: {log_info['analyzer_end_line']}")
        else:
            print("  Analyzer end: not found (run may be incomplete)")

        if log_info["log_excerpt"]:
            print("\n--- Last lines of analyzer section ---")
            for ln, text in log_info["log_excerpt"][-15:]:
                print(f"  {ln}: {text[:100]}")

    # Diagnosis
    print("\n--- Possible Causes of Slow Analyzer ---")
    if analysis["file_finishes"]:
        max_dur = max((d for _, d, _ in analysis["file_finishes"].values() if d is not None), default=0)
        if max_dur > 300:  # 5 min
            print(f"  - Longest instrument took {max_dur/60:.1f} min - consider reducing date range or slots")
    processed = log_info.get("processed_files") or 0
    if processed > 10000:
        n_inst = len(analysis.get("file_finishes") or analysis.get("file_starts") or {1})
        n_inst = max(n_inst, 1)
        print(f"  - {processed:,} files total - each instrument processes ~{processed//n_inst:,.0f} files")
        print("  - Consider: incremental analysis, date range limits, or fewer slots")
    if not analysis["file_finishes"]:
        print("  - Run appears incomplete - check for process kill, timeout, or disk space")

    print("\n" + "=" * 70)
    return 0


if __name__ == "__main__":
    sys.exit(main())
