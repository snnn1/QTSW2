#!/usr/bin/env python3
"""
Robot log loop / churn audit (JSONL).

Steps implemented:
  1) Ingest: parse JSONL chronologically (all sources merged, sorted by ts_utc).
  2) Normalize: ts_utc, event (event_type / name), instrument, intent_id (top-level or data).
  3) Rules: TIGHT_LOOP, CHURN_LOOP, PING_PONG_LOOP, POST_IDLE_ACTIVITY,
     DUPLICATE_EXECUTION_CALLBACK; optional ENGINE_CPU_PROFILE correlation.
  4) Classification: harmless (expected) / suspicious / critical + subsystem guess + action hint.

Diagnostic only; does not modify code or runtime.

Usage:
  python tools/robot_log_loop_audit.py
  python tools/robot_log_loop_audit.py --log-dir path/to/logs/robot
  python tools/robot_log_loop_audit.py --include-archive
  python tools/robot_log_loop_audit.py --all-jsonl --include-harmless-detail --print-all-findings

Defaults: robot_*.jsonl only (excludes robot_skeleton.jsonl), detail capped (--max-detail), harmless hidden from detail.
"""
from __future__ import annotations

import argparse
import json
import os
from collections import defaultdict, deque
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, List, Optional, Sequence, Tuple

# Reuse normalization from log_audit
_TOOLS = Path(__file__).resolve().parent
import sys

if str(_TOOLS) not in sys.path:
    sys.path.insert(0, str(_TOOLS))

from log_audit import NormEvent, normalize_event, resolve_log_dir  # type: ignore


def _intent_id(obj: Dict[str, Any], data: Dict[str, Any]) -> str:
    v = obj.get("intent_id")
    if v is not None and str(v).strip():
        return str(v).strip()
    v = data.get("intent_id")
    if v is not None and str(v).strip():
        return str(v).strip()
    return ""


def _instrument(e: NormEvent) -> str:
    ins = (e.instrument or "").strip()
    if ins:
        return ins
    d = e.data if isinstance(e.data, dict) else {}
    for k in ("instrument", "instrument_key", "execution_instrument_key"):
        v = d.get(k)
        if v is not None and str(v).strip():
            return str(v).strip()
    return ""


def enrich_norm(e: NormEvent, raw: Dict[str, Any]) -> Tuple[NormEvent, str, str]:
    data = e.data if isinstance(e.data, dict) else {}
    iid = _intent_id(raw, data)
    inst = _instrument(e)
    return e, inst, iid


def iter_jsonl_files(log_dir: Path, include_archive: bool) -> List[Path]:
    files = sorted(log_dir.glob("*.jsonl"))
    if include_archive:
        arch = log_dir / "archive"
        if arch.is_dir():
            files.extend(sorted(arch.glob("*.jsonl")))
    return sorted(set(files), key=lambda p: p.stat().st_mtime if p.exists() else 0)


def load_events(files: Sequence[Path]) -> List[Tuple[NormEvent, str, str, str]]:
    """(event, instrument, intent_id, source_file) sorted by ts_utc."""
    out: List[Tuple[NormEvent, str, str, str]] = []
    for fp in files:
        fname = fp.name
        try:
            with fp.open("r", encoding="utf-8", errors="replace") as fh:
                for line in fh:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        obj = json.loads(line)
                        if not isinstance(obj, dict):
                            continue
                    except json.JSONDecodeError:
                        continue
                    ne = normalize_event(obj, fname)
                    if ne is None:
                        continue
                    ne2, inst, iid = enrich_norm(ne, obj)
                    out.append((ne2, inst, iid, fname))
        except OSError:
            continue
    out.sort(key=lambda x: x[0].ts_utc)
    return out


def cpu_samples(events: List[Tuple[NormEvent, str, str, str]]) -> List[Tuple[datetime, float, float]]:
    """(ts_utc, process_cpu_pct or -1, sum_subsystem_ratio approx) from ENGINE_CPU_PROFILE."""
    samples: List[Tuple[datetime, float, float]] = []
    for e, _, _, _ in events:
        if e.event != "ENGINE_CPU_PROFILE":
            continue
        d = e.data if isinstance(e.data, dict) else {}
        pct = d.get("process_cpu_pct")
        try:
            p = float(pct) if pct is not None else -1.0
        except (TypeError, ValueError):
            p = -1.0
        wall = d.get("wall_window_ms")
        sum_ms = d.get("sum_subsystem_ms")
        try:
            w = float(wall) if wall is not None else 0.0
            s = float(sum_ms) if sum_ms is not None else 0.0
            ratio = (s / w * 100.0) if w > 0 else -1.0
        except (TypeError, ValueError):
            ratio = -1.0
        samples.append((e.ts_utc, p, ratio))
    return samples


def cpu_for_window(
    samples: Sequence[Tuple[datetime, float, float]],
    t0: datetime,
    t1: datetime,
) -> Tuple[Optional[float], Optional[float]]:
    """(avg process %, peak process %) in [t0, t1]; None if no samples."""
    pts = [p for ts, p, _ in samples if t0 <= ts <= t1 and p >= 0]
    if not pts:
        return None, None
    return sum(pts) / len(pts), max(pts)


@dataclass
class Finding:
    type: str
    instrument: str
    intent_id: str
    events: str
    time_window: str
    count: int
    cpu_avg: Optional[float]
    cpu_peak: Optional[float]
    classification: str
    reason: str
    file_hint: str = ""


def classify_loop(ftype: str, events_summary: str) -> Tuple[str, str]:
    up = events_summary.upper()
    if any(
        x in up
        for x in (
            "BAR_ADMISSION",
            "BAR_BUFFER",
            "BAR_",
            "TICK_RECEIVED",
            "RANGE_BUILDING",
            "STREAM_LOOP",
        )
    ):
        return "harmless (expected)", "Bar/tick/range stream events often arrive in bursts per instrument; not a control-plane loop."
    if "PROTECTIVE_AUDIT_CONTEXT" in events_summary.upper() or "STREAM_LOOP" == events_summary.upper():
        return "harmless (expected)", "High-frequency protective/stream audits are often timer-driven; verify only if paired with errors."
    if "TRADING_DAY_ROLLOVER" in events_summary.upper():
        return "harmless (expected)", "Rollover may emit bursts from stream lifecycle; not execution/reconciliation churn."
    if ftype == "TIGHT_LOOP" and "METRICS" in events_summary.upper():
        return "harmless (expected)", "Periodic METRICS ticks can burst in tests; verify event name is not error path."
    if ftype == "CHURN_LOOP" and "_METRICS" in events_summary:
        return "harmless (expected)", "Engine emits METRICS on a timer; high counts may be normal cadence."
    if ftype == "POST_IDLE_ACTIVITY":
        return "critical", "Activity that should quiet after flat persisted; often journal/registry mismatch or flatten loop."
    if ftype == "DUPLICATE_EXECUTION_CALLBACK":
        return "critical", "Same execution processed multiple times risks double accounting or thrash."
    if ftype == "TIGHT_LOOP":
        return "suspicious", "Very high frequency of same event may indicate hot-loop logging or fail-closed retry storm."
    if ftype == "CHURN_LOOP":
        return "suspicious", "Many repeats in short window may indicate reconciliation/flatten churn."
    if ftype == "PING_PONG_LOOP":
        return "suspicious", "Rapid alternation between subsystems suggests thrash between gates or competing paths."
    return "unknown", "Pattern matches rule; triage with surrounding context."


def subsystem_guess(f: Finding) -> str:
    s = (f.events + f.type).upper()
    if "FLATTEN" in s or "NT_FLATTEN" in s:
        return "flatten"
    if "RECONCIL" in s or "MISMATCH" in s or "ASSEMBLE_MISMATCH" in s or "STATE_CONSISTENCY" in s:
        return "reconciliation"
    if "RETRY" in s or "DEFERRED" in s or "RESOLUTION" in s or "THROTTLED" in s:
        return "retry"
    if "EXECUTION" in s:
        return "execution"
    return "unknown"


def detect_tight_churn(
    events: List[Tuple[NormEvent, str, str, str]],
    cpu_samples: Sequence[Tuple[datetime, float, float]],
) -> List[Finding]:
    findings: List[Finding] = []
    by_key_tight: Dict[Tuple[str, str, str], deque] = defaultdict(lambda: deque())
    by_key_churn: Dict[Tuple[str, str, str], deque] = defaultdict(lambda: deque())
    # Cooldown so one continuous burst does not emit thousands of findings
    last_tight_emit: Dict[Tuple[str, str, str], datetime] = {}
    last_churn_emit: Dict[Tuple[str, str, str], datetime] = {}

    for e, inst, iid, fname in events:
        k = (inst or "__none__", iid or "__none__", e.event or "__blank__")
        # A) 100 ms, same event >= 5
        dq = by_key_tight[k]
        dq.append(e.ts_utc)
        while dq and (e.ts_utc - dq[0]).total_seconds() > 0.100:
            dq.popleft()
        if len(dq) >= 5:
            le = last_tight_emit.get(k)
            if le is None or (e.ts_utc - le).total_seconds() >= 15.0:
                t0, t1 = dq[0], dq[-1]
                avg, peak = cpu_for_window(cpu_samples, t0, t1)
                cl, reason = classify_loop("TIGHT_LOOP", e.event)
                findings.append(
                    Finding(
                        type="TIGHT_LOOP",
                        instrument=inst,
                        intent_id=iid,
                        events=e.event,
                        time_window=f"{t0.isoformat()} - {t1.isoformat()}",
                        count=len(dq),
                        cpu_avg=avg,
                        cpu_peak=peak,
                        classification=cl,
                        reason=reason + ("; CPU_CORRELATED" if avg is not None and (avg > 30 or (peak or 0) > 50) else ""),
                        file_hint=fname,
                    )
                )
                last_tight_emit[k] = e.ts_utc
            dq.clear()

        # B) 10 s, same event >= 20
        dq10 = by_key_churn[k]
        dq10.append(e.ts_utc)
        while dq10 and (e.ts_utc - dq10[0]).total_seconds() > 10.0:
            dq10.popleft()
        if len(dq10) >= 20:
            le = last_churn_emit.get(k)
            if le is None or (e.ts_utc - le).total_seconds() >= 180.0:
                t0, t1 = dq10[0], dq10[-1]
                avg, peak = cpu_for_window(cpu_samples, t0, t1)
                cl, reason = classify_loop("CHURN_LOOP", e.event)
                findings.append(
                    Finding(
                        type="CHURN_LOOP",
                        instrument=inst,
                        intent_id=iid,
                        events=e.event,
                        time_window=f"{t0.isoformat()} - {t1.isoformat()}",
                        count=len(dq10),
                        cpu_avg=avg,
                        cpu_peak=peak,
                        classification=cl,
                        reason=reason + ("; CPU_CORRELATED" if avg is not None and (avg > 30 or (peak or 0) > 50) else ""),
                        file_hint=fname,
                    )
                )
                last_churn_emit[k] = e.ts_utc
            dq10.clear()
    return findings


def count_alternations(seq: List[str]) -> int:
    if len(seq) < 2:
        return 0
    alt = 0
    for i in range(len(seq) - 1):
        if seq[i] != seq[i + 1]:
            alt += 1
    return alt


def detect_ping_pong(events: List[Tuple[NormEvent, str, str, str]], cpu_samples) -> List[Finding]:
    findings: List[Finding] = []
    by_inst: Dict[str, List[Tuple[datetime, str, str, str]]] = defaultdict(list)
    for e, inst, iid, fname in events:
        by_inst[inst or "__engine__"].append((e.ts_utc, e.event or "", iid, fname))

    last_pp_emit: Dict[str, datetime] = {}
    for inst, rows in by_inst.items():
        dq: deque = deque()
        for ts, ev, iid_row, fname in rows:
            dq.append((ts, ev, iid_row, fname))
            while dq and (ts - dq[0][0]).total_seconds() > 2.0:
                dq.popleft()
            evs = [x[1] for x in dq]
            alts = count_alternations(evs)
            uniq = len(set(evs))
            if alts >= 10 and uniq <= 3 and uniq >= 2:
                t0, t1 = dq[0][0], dq[-1][0]
                le = last_pp_emit.get(inst)
                if le is None or (ts - le).total_seconds() >= 5.0:
                    avg, peak = cpu_for_window(cpu_samples, t0, t1)
                    evnames = sorted(set(evs))
                    cl, reason = classify_loop("PING_PONG_LOOP", ",".join(evnames))
                    iids = {x[2] for x in dq if x[2]}
                    findings.append(
                        Finding(
                            type="PING_PONG_LOOP",
                            instrument=inst if inst != "__engine__" else "",
                            intent_id=next(iter(iids)) if len(iids) == 1 else "(multiple)",
                            events=" | ".join(evnames),
                            time_window=f"{t0.isoformat()} - {t1.isoformat()}",
                            count=len(evs),
                            cpu_avg=avg,
                            cpu_peak=peak,
                            classification=cl,
                            reason=reason,
                            file_hint=fname,
                        )
                    )
                    last_pp_emit[inst] = ts
                dq.clear()
    return findings


def is_flat_signal(e: NormEvent) -> bool:
    name = e.event or ""
    if name in ("FLATTEN_BROKER_FLAT_CONFIRMED", "FLATTEN_SKIPPED_ACCOUNT_FLAT", "FLATTEN_VERIFY_PASS"):
        return True
    d = e.data if isinstance(e.data, dict) else {}
    if name == "RECONCILIATION_CONTEXT":
        try:
            aq = int(d.get("account_qty", -1))
            jq = int(d.get("journal_qty", -1))
            if aq == 0 and jq == 0:
                return True
        except (TypeError, ValueError):
            pass
    if name == "TRADE_RECONCILED":
        return True
    return False


def forbidden_post_idle(e: NormEvent) -> bool:
    name = e.event or ""
    if name.startswith("RECONCILIATION_") and "METRICS" not in name:
        return True
    if name.startswith("FLATTEN_") and name not in ("FLATTEN_BROKER_FLAT_CONFIRMED", "FLATTEN_SKIPPED_ACCOUNT_FLAT"):
        return True
    if "RETRY" in name or name.startswith("EXECUTION_DEFERRED"):
        return True
    # Narrow execution noise: fills / unowned / duplicates more indicative than every EXECUTION_ tick
    if name in (
        "EXECUTION_FILLED",
        "EXECUTION_PARTIAL_FILL",
        "EXECUTION_UNOWNED",
        "EXECUTION_DUPLICATE_DETECTED",
    ):
        return True
    return False


def clears_idle_state(e: NormEvent) -> bool:
    """After flat, new exposure invalidates 'idle' for POST_IDLE rule."""
    name = e.event or ""
    d = e.data if isinstance(e.data, dict) else {}
    if name == "RECONCILIATION_CONTEXT":
        try:
            if int(d.get("account_qty", 0) or 0) > 0 or int(d.get("journal_qty", 0) or 0) > 0:
                return True
        except (TypeError, ValueError):
            pass
    if name == "RECONCILIATION_QTY_MISMATCH":
        return True
    if name == "EXECUTION_FILLED":
        pe = str(d.get("position_effect") or "").upper()
        mapped = str(d.get("mapped", "")).lower()
        if pe == "OPEN" and mapped == "true":
            return True
    return False


def detect_post_idle(events: List[Tuple[NormEvent, str, str, str]], cpu_samples) -> List[Finding]:
    findings: List[Finding] = []
    last_flat: Optional[datetime] = None
    cluster: List[Tuple[datetime, NormEvent, str, str, str]] = []
    idle_stale_seconds = 900.0  # flat marker older than this is ignored for POST_IDLE

    def flush_cluster():
        nonlocal cluster
        if not cluster or last_flat is None:
            cluster = []
            return
        t0, e0, inst0, iid0, fn0 = cluster[0]
        t1, _, _, _, fn1 = cluster[-1]
        if t0 <= last_flat or t1 <= last_flat:
            cluster = []
            return
        if (t1 - t0).total_seconds() < 5.0:
            cluster = []
            return
        names = [x[1].event for x in cluster]
        evs = " | ".join(sorted(set(names))[:20])
        avg, peak = cpu_for_window(cpu_samples, t0, t1)
        cl, reason = classify_loop("POST_IDLE_ACTIVITY", evs)
        findings.append(
            Finding(
                type="POST_IDLE_ACTIVITY",
                instrument=inst0,
                intent_id=iid0,
                events=evs,
                time_window=f"{t0.isoformat()} - {t1.isoformat()}",
                count=len(cluster),
                cpu_avg=avg,
                cpu_peak=peak,
                classification=cl,
                reason=reason,
                file_hint=fn0,
            )
        )
        cluster = []

    for e, inst, iid, fname in events:
        if clears_idle_state(e):
            flush_cluster()
            last_flat = None
        if is_flat_signal(e):
            flush_cluster()
            last_flat = e.ts_utc
            continue
        if last_flat is None or e.ts_utc <= last_flat:
            continue
        if (e.ts_utc - last_flat).total_seconds() > idle_stale_seconds:
            flush_cluster()
            last_flat = None
            continue
        if forbidden_post_idle(e):
            if not cluster:
                cluster.append((e.ts_utc, e, inst, iid, fname))
            elif (e.ts_utc - cluster[-1][0]).total_seconds() <= 3.0:
                cluster.append((e.ts_utc, e, inst, iid, fname))
            else:
                flush_cluster()
                cluster.append((e.ts_utc, e, inst, iid, fname))
            if (
                len(cluster) >= 2
                and (cluster[-1][0] - cluster[0][0]).total_seconds() >= 5.0
            ):
                flush_cluster()
        else:
            flush_cluster()

    flush_cluster()
    return findings


def detect_duplicate_execution(events: List[Tuple[NormEvent, str, str, str]], cpu_samples) -> List[Finding]:
    findings: List[Finding] = []
    # key (exec_id, fill_qty, intent) -> list of ts
    by_ex: Dict[Tuple[str, str, str], List[datetime]] = defaultdict(list)

    for e, inst, iid, fname in events:
        if not (e.event or "").startswith("EXECUTION_"):
            continue
        d = e.data if isinstance(e.data, dict) else {}
        ex = str(d.get("execution_id") or d.get("broker_execution_id") or d.get("broker_exec_id") or "").strip()
        if not ex:
            oid = str(d.get("broker_order_id") or d.get("order_id") or "").strip()
            fg = str(d.get("fill_group_id") or "").strip()
            if oid and fg:
                ex = f"fill_group:{fg}|order:{oid}"
            elif fg:
                ex = f"fill_group:{fg}"
            else:
                continue
        fq = str(d.get("fill_quantity") or d.get("fill_qty") or "").strip()
        k = (ex, fq, iid or "__none__")
        by_ex[k].append(e.ts_utc)

    for (ex, fq, iid), ts_list in by_ex.items():
        if len(ts_list) < 3:
            continue
        ts_list.sort()
        t0, t1 = ts_list[0], ts_list[-1]
        if (t1 - t0).total_seconds() > 5.0:
            continue
        avg, peak = cpu_for_window(cpu_samples, t0, t1)
        cl, reason = classify_loop("DUPLICATE_EXECUTION_CALLBACK", "EXECUTION_*")
        findings.append(
            Finding(
                type="DUPLICATE_EXECUTION_CALLBACK",
                instrument="",
                intent_id=iid if iid != "__none__" else "",
                events=f"execution_id={ex} fill_qty={fq}",
                time_window=f"{t0.isoformat()} - {t1.isoformat()}",
                count=len(ts_list),
                cpu_avg=avg,
                cpu_peak=peak,
                classification=cl,
                reason=reason,
                file_hint="",
            )
        )
    return findings


def severity_score(f: Finding) -> int:
    if f.classification.startswith("critical"):
        return 4
    if f.classification.startswith("suspicious"):
        return 3
    if "harmless" in f.classification:
        return 1
    return 2


def print_report(
    findings: List[Finding],
    cpu_present: bool,
    *,
    hide_harmless: bool,
    max_detail: int,
    print_all: bool,
) -> None:
    by_type: Dict[str, int] = defaultdict(int)
    for f in findings:
        by_type[f.type] += 1

    harmless_n = sum(1 for f in findings if "harmless" in f.classification)
    detail_list = findings if print_all else findings
    if hide_harmless:
        detail_list = [f for f in detail_list if "harmless" not in f.classification]
    detail_list = sorted(detail_list, key=severity_score, reverse=True)
    if not print_all and max_detail > 0 and len(detail_list) > max_detail:
        truncated = len(detail_list) - max_detail
        detail_list = detail_list[:max_detail]
    else:
        truncated = 0

    print("=" * 72)
    print("ROBOT LOG LOOP AUDIT (diagnostic)")
    print("=" * 72)
    if harmless_n and hide_harmless:
        print(f"(Suppressed {harmless_n} harmless-classified rows from detail; see SUMMARY counts.)")
    print()
    for f in detail_list:
        cpu_line = ""
        if f.cpu_avg is not None or f.cpu_peak is not None:
            cpu_line = f"CPU: avg={f.cpu_avg or 0:.1f}% peak={f.cpu_peak or 0:.1f}%"
            if f.cpu_avg is not None and (f.cpu_avg > 30 or (f.cpu_peak or 0) > 50):
                cpu_line += " CPU_CORRELATED=yes"
        elif not cpu_present:
            cpu_line = "CPU: (no ENGINE_CPU_PROFILE samples in ingest)"
        else:
            cpu_line = "CPU: (no sample in window)"
        print()
        print(f"TYPE: {f.type}")
        print(f"INSTRUMENT: {f.instrument or '(empty)'}")
        print(f"INTENT_ID: {f.intent_id or '(empty)'}")
        print(f"EVENT(S): {f.events}")
        print(f"TIME WINDOW: {f.time_window}")
        print(f"COUNT: {f.count}")
        print(cpu_line)
        print(f"CLASSIFICATION: {f.classification}")
        print(f"REASON: {f.reason}")
        if f.file_hint:
            print(f"SOURCE_FILE: {f.file_hint}")

    if truncated > 0:
        print()
        print(f"… {truncated} more findings not shown (raise --max-detail or use --print-all-findings)")

    print()
    print("=" * 72)
    print("SUMMARY")
    print("=" * 72)
    non_harmless = len(findings) - harmless_n
    crit_n = sum(1 for f in findings if f.classification.startswith("critical"))
    susp_n = sum(1 for f in findings if f.classification.startswith("suspicious"))
    print(f"1. Total findings: {len(findings)} (harmless: {harmless_n}, non-harmless: {non_harmless}; critical: {crit_n}, suspicious: {susp_n})")
    print(f"   By type: {dict(by_type)}")
    ranked = sorted(findings, key=severity_score, reverse=True)
    print(f"2. Top findings by severity (up to 5):")
    for i, f in enumerate(ranked[:5], 1):
        print(f"   {i}. [{f.type}] {f.classification} | {f.events[:80]} | {f.time_window}")

    subs = defaultdict(int)
    for f in findings:
        subs[subsystem_guess(f)] += 1
    print("3. Likely root subsystem (by finding count):")
    for k, v in sorted(subs.items(), key=lambda x: -x[1]):
        print(f"   {k}: {v}")

    # aggregate recommendation
    nh = [f for f in findings if "harmless" not in f.classification]
    if any(severity_score(f) >= 4 for f in nh):
        rec = "investigate"
    elif any(severity_score(f) >= 3 for f in nh):
        rec = "monitor"
    elif not nh:
        rec = "ignore"
    else:
        rec = "monitor"
    print(f"4. Recommended next action: {rec}")


def main() -> int:
    ap = argparse.ArgumentParser(description="Audit robot JSONL logs for pathological loops.")
    ap.add_argument("--log-dir", default=None, help="Override logs/robot directory")
    ap.add_argument("--include-archive", action="store_true", help="Include logs/robot/archive/*.jsonl")
    ap.add_argument(
        "--all-jsonl",
        action="store_true",
        help="Include every *.jsonl (hydration, frontend_feed, …). Default: robot_*.jsonl only.",
    )
    ap.add_argument(
        "--print-all-findings",
        action="store_true",
        help="Print every finding (can be huge). Default: cap detail rows with --max-detail.",
    )
    ap.add_argument(
        "--max-detail",
        type=int,
        default=80,
        help="Max detail rows when not using --print-all-findings (default 80).",
    )
    ap.add_argument(
        "--include-harmless-detail",
        action="store_true",
        help="Include harmless (expected) rows in detail section.",
    )
    args = ap.parse_args()

    log_dir = resolve_log_dir(args.log_dir)
    if not log_dir.is_dir():
        print(f"Log dir not found: {log_dir}", file=sys.stderr)
        return 1

    files = iter_jsonl_files(log_dir, args.include_archive)
    if not args.all_jsonl:
        files = [f for f in files if f.name.startswith("robot_")]
        # Harness / skeleton streams are not production robot logs
        files = [f for f in files if f.name.lower() != "robot_skeleton.jsonl"]

    if not files:
        print(f"No matching jsonl files under {log_dir}", file=sys.stderr)
        return 1

    events = load_events(files)
    cpus = cpu_samples(events)
    cpu_present = bool(cpus)

    findings: List[Finding] = []
    findings.extend(detect_tight_churn(events, cpus))
    findings.extend(detect_ping_pong(events, cpus))
    findings.extend(detect_post_idle(events, cpus))
    findings.extend(detect_duplicate_execution(events, cpus))

    # de-dupe identical windows (rough)
    seen = set()
    uniq: List[Finding] = []
    for f in findings:
        key = (f.type, f.time_window, f.events, f.instrument, f.intent_id)
        if key in seen:
            continue
        seen.add(key)
        uniq.append(f)

    print_report(
        uniq,
        cpu_present,
        hide_harmless=not args.include_harmless_detail,
        max_detail=args.max_detail,
        print_all=args.print_all_findings,
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
