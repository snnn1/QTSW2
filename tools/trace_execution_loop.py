#!/usr/bin/env python3
"""
Analyze logs/robot/execution_trace.jsonl for adapter-level callback loops.

Reads EXECUTION_TRACE and EXECUTION_TRACE_LOOP_DETECTED lines emitted by ExecutionTraceWriter.
"""

from __future__ import annotations

import argparse
import json
import sys
from collections import defaultdict
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


def parse_ts(s: str | None) -> datetime | None:
    if not s:
        return None
    s = s.strip().replace("Z", "+00:00")
    if s.endswith("+00:00"):
        s = s[:-6]
    for fmt in (
        "%Y-%m-%dT%H:%M:%S.%f",
        "%Y-%m-%dT%H:%M:%S",
    ):
        try:
            return datetime.strptime(s[:26], fmt).replace(tzinfo=timezone.utc)
        except ValueError:
            continue
    try:
        return datetime.fromisoformat(s.replace("Z", "+00:00"))
    except ValueError:
        return None


def load_events(path: Path) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    if not path.is_file():
        return rows
    with path.open(encoding="utf-8", errors="replace") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                rows.append(json.loads(line))
            except json.JSONDecodeError:
                continue
    return rows


def event_key(ev: dict[str, Any]) -> str:
    return "|".join(
        [
            str(ev.get("source") or ""),
            str(ev.get("call_stage") or ""),
            str(ev.get("order_state") or ""),
            str(ev.get("fill_qty") or 0),
        ]
    )


def sliding_window_max_same(
    events: list[dict[str, Any]], window_ms: float, same_fn: Any
) -> tuple[int, float, str] | None:
    """Max count of events matching same_fn in any window of width window_ms; returns (count, span_ms, label)."""
    if not events:
        return None
    times = []
    for e in events:
        t = parse_ts(str(e.get("ts_utc") or ""))
        if t is None:
            continue
        times.append((t.timestamp() * 1000.0, e))
    if not times:
        return None
    times.sort(key=lambda x: x[0])
    best: tuple[int, float, str] | None = None
    i = 0
    for j in range(len(times)):
        tj = times[j][0]
        while tj - times[i][0] > window_ms:
            i += 1
        window = times[i : j + 1]
        by_key: dict[str, list[int]] = defaultdict(list)
        for tm, ev in window:
            k = same_fn(ev)
            by_key[k].append(tm)
        for k, tms in by_key.items():
            if len(tms) < 3:
                continue
            span = max(tms) - min(tms)
            cand = (len(tms), span, k)
            if best is None or cand[0] > best[0]:
                best = cand
    return best


def detect_ping_pong(events: list[dict[str, Any]], window_ms: float = 50.0) -> list[str]:
    """Alternation OnOrderUpdate <-> OnExecutionUpdate raw_callback in short window."""
    findings: list[str] = []
    times = []
    for e in events:
        if str(e.get("event")) != "EXECUTION_TRACE":
            continue
        if str(e.get("call_stage")) != "raw_callback":
            continue
        t = parse_ts(str(e.get("ts_utc") or ""))
        if t is None:
            continue
        src = str(e.get("source") or "")
        if src not in ("OnOrderUpdate", "OnExecutionUpdate"):
            continue
        times.append((t.timestamp() * 1000.0, src))
    times.sort(key=lambda x: x[0])
    i = 0
    for j in range(1, len(times)):
        while times[j][0] - times[i][0] > window_ms:
            i += 1
        chunk = times[i : j + 1]
        if len(chunk) < 4:
            continue
        alts = 0
        for k in range(len(chunk) - 1):
            a, b = chunk[k][1], chunk[k + 1][1]
            if {a, b} == {"OnOrderUpdate", "OnExecutionUpdate"}:
                alts += 1
        if alts >= 3:
            findings.append(
                f"ping_pong raw_callback alternation count>={alts} in {window_ms:.0f}ms window"
            )
            break
    return findings


def detect_notify_chain(events: list[dict[str, Any]], window_ms: float = 30.0) -> list[str]:
    """
    Heuristic: tight sequence OnExecutionUpdate -> NotifyExecutionTrigger before -> after in same ms band.
    """
    findings: list[str] = []
    by_t: list[tuple[float, str]] = []
    for e in events:
        if str(e.get("event")) != "EXECUTION_TRACE":
            continue
        t = parse_ts(str(e.get("ts_utc") or ""))
        if t is None:
            continue
        src = str(e.get("source") or "")
        st = str(e.get("call_stage") or "")
        by_t.append((t.timestamp() * 1000.0, f"{src}:{st}"))
    by_t.sort(key=lambda x: x[0])
    for j in range(len(by_t)):
        t0 = by_t[j][0]
        seq: list[str] = []
        for k in range(j, len(by_t)):
            if by_t[k][0] - t0 > window_ms:
                break
            seq.append(by_t[k][1])
        s = " -> ".join(seq[:12])
        if (
            "OnExecutionUpdate:raw_callback" in s
            and "NotifyExecutionTrigger:before_notify" in s
            and "NotifyExecutionTrigger:after_notify" in s
            and seq.count("OnExecutionUpdate:raw_callback") >= 2
        ):
            findings.append(f"notify_chain burst within {window_ms:.0f}ms: {s[:200]}")
            break
    return findings


def format_report(
    instrument: str,
    intent_id: str,
    pattern: str,
    n_events: int,
    window_ms: float,
    source: str,
    conclusion: str,
) -> str:
    return (
        "LOOP DETECTED\n"
        "-------------\n"
        f"instrument: {instrument}\n"
        f"intent_id: {intent_id}\n"
        "\n"
        f"pattern:\n{pattern}\n"
        "\n"
        f"frequency:\n{n_events} events in {window_ms:.2f} ms\n"
        "\n"
        f"source:\n{source}\n"
        "\n"
        f"conclusion:\n{conclusion}\n"
    )


def analyze_group(key: str, events: list[dict[str, Any]]) -> list[str]:
    inst, iid = key.split("\t", 1) if "\t" in key else (key, "")
    out: list[str] = []
    traces = [e for e in events if str(e.get("event")) == "EXECUTION_TRACE"]
    if not traces:
        return out

    # A: same composite key spam in 20ms
    sw = sliding_window_max_same(traces, 20.0, event_key)
    if sw and sw[0] >= 10:
        out.append(
            format_report(
                inst,
                iid,
                sw[2],
                sw[0],
                sw[1],
                "OnOrderUpdate / OnExecutionUpdate / Fill / NotifyExecutionTrigger (see pattern)",
                "Same adapter trace signature repeated rapidly — likely duplicate callbacks or state churn.",
            )
        )

    # B: ping-pong
    for p in detect_ping_pong(traces):
        out.append(
            format_report(
                inst,
                iid,
                "OnOrderUpdate:raw_callback <-> OnExecutionUpdate:raw_callback",
                0,
                50.0,
                "OnOrderUpdate / OnExecutionUpdate",
                p,
            )
        )

    # C: notify chain
    for p in detect_notify_chain(traces):
        out.append(
            format_report(
                inst,
                iid,
                "OnExecutionUpdate -> NotifyExecutionTrigger (before/after)",
                0,
                30.0,
                "NotifyExecutionTrigger",
                p,
            )
        )

    return out


def main() -> int:
    ap = argparse.ArgumentParser(description="Analyze execution_trace.jsonl for loops.")
    ap.add_argument(
        "--trace",
        type=Path,
        default=Path("logs/robot/execution_trace.jsonl"),
        help="Path to execution_trace.jsonl (default: logs/robot/execution_trace.jsonl)",
    )
    args = ap.parse_args()
    path: Path = args.trace
    if not path.is_file():
        print(f"No file: {path.resolve()}", file=sys.stderr)
        return 2

    events = load_events(path)
    if not events:
        print(f"Empty or unreadable: {path}", file=sys.stderr)
        return 1

    # Pre-surface embedded loop detections from C#
    for ev in events:
        if str(ev.get("event")) == "EXECUTION_TRACE_LOOP_DETECTED":
            print(
                format_report(
                    str(ev.get("instrument") or ""),
                    str(ev.get("intent_id") or ""),
                    str(ev.get("event_pattern") or ""),
                    int(ev.get("repetition_count") or 0),
                    float(ev.get("time_window_ms") or 0),
                    str(ev.get("source") or ""),
                    "Runtime burst detector (ExecutionTraceWriter) fired for this pattern.",
                )
            )
            print()

    grouped: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for ev in events:
        if str(ev.get("event")) != "EXECUTION_TRACE":
            continue
        inst = str(ev.get("instrument") or "_")
        iid = str(ev.get("intent_id") or "_")
        grouped[f"{inst}\t{iid}"].append(ev)

    printed = 0
    for key, grp in sorted(grouped.items()):
        grp.sort(key=lambda e: str(e.get("ts_utc") or ""))
        for block in analyze_group(key, grp):
            print(block)
            print()
            printed += 1

    if not printed and not any(str(e.get("event")) == "EXECUTION_TRACE_LOOP_DETECTED" for e in events):
        print("No loop patterns detected (thresholds may not be met).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
