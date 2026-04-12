"""
One-off: scan logs/robot robot_*.jsonl (incl. archive) for residual unexplained_working / IEA / adoption signals.
"""
from __future__ import annotations

import glob
import json
import os
import re
import sys
from collections import defaultdict
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Any

_PAYLOAD_INST = re.compile(r"instrument\s*=\s*([A-Z][A-Z0-9]*)", re.I)
_UWC_POS = re.compile(r'"unexplained_working_count"\s*:\s*"([0-9]+)"')


def raw_line_positive_uwc(line: str) -> bool:
    m = _UWC_POS.search(line)
    if not m:
        return False
    try:
        return int(m.group(1)) > 0
    except ValueError:
        return False

ROOT = os.path.join(os.path.dirname(__file__), "..", "logs", "robot")
GAP_SEC = 600


def parse_ts(s: str | None):
    if not s:
        return None
    try:
        if s.endswith("Z"):
            s = s[:-1] + "+00:00"
        return datetime.fromisoformat(s.replace("Z", "+00:00"))
    except Exception:
        return None


def to_int(v: Any) -> int:
    if v is None:
        return 0
    try:
        return int(float(str(v)))
    except Exception:
        return 0


# Bucket E — operator / foreign-session style
EXTERNAL_MANUAL_EVENTS = frozenset({
    "MANUAL_OR_EXTERNAL_ORDER_DETECTED",
    "MANUAL_FLATTEN_REQUIRED",
    "FOREIGN_INSTRUMENT_QTSW2_ORDER_SKIPPED",
})

# Bucket D — adoption lag / unowned (robot-side ownership gap)
UNOWNED_ADOPTION_EVENTS = frozenset({
    "UNOWNED_LIVE_ORDER_DETECTED",
    "EXECUTION_UNOWNED",
    "ORDER_REGISTRY_RECOVERABLE_UNOWNED_DETECTED",
    "ADOPTION_GRACE_EXPIRED_UNOWNED",
})


@dataclass
class Row:
    ts: datetime
    instrument: str
    run_id: str
    event: str
    uwc: int
    flags: dict[str, bool]


def cheap_lower_text(obj: dict, data: dict, event: str) -> str:
    parts = [event or "", str(obj.get("message") or "")]
    for k in ("readiness_summary", "readiness_contradictions", "reconciliation_outcome", "note", "reason"):
        v = data.get(k)
        if v is not None:
            parts.append(str(v))
    return " ".join(parts).lower()


def extract_flags(obj: dict, data: dict, event: str, dl: str) -> dict[str, bool]:
    iea_unav = str(data.get("iea_unavailable", "")).lower() in ("true", "1")
    rsum = str(data.get("readiness_summary") or "").lower()
    # Strict: numeric pending or escalation events. readiness_summary alone is too noisy (most gate lines).
    pending_strict = to_int(data.get("pending_adoption_count")) > 0
    readiness_pending_candidates = "pending_adoption_candidates" in rsum

    return {
        "uwc_pos": to_int(data.get("unexplained_working_count")) > 0,
        "iea_unav": iea_unav or "iea_unavailable" in dl,
        "iea_mismatch": event == "ORDER_REGISTRY_LOOKUP_IEA_MISMATCH",
        "unexpl_ord": event == "UNEXPLAINED_WORKING_ORDER",
        "fail_closed": event == "ORDER_REGISTRY_RECOVERY_DEFERRED_FAIL_CLOSED",
        "adopt_esc": event == "ADOPTION_NON_CONVERGENCE_ESCALATED",
        "adopt_skip": event == "IEA_ADOPTION_SCAN_SKIPPED_NO_PROGRESS",
        "pending_adopt": pending_strict,
        "readiness_pending_candidates": readiness_pending_candidates,
        "unowned_adopt": event in UNOWNED_ADOPTION_EVENTS
            or ("unowned" in event.lower() and event not in EXTERNAL_MANUAL_EVENTS),
        "ext_manual_evt": event in EXTERNAL_MANUAL_EVENTS,
        "trusted_hint": "trusted" in dl and ("working" in dl or "iea" in dl or "mismatch" in dl),
        "skew_hint": _instrument_skew(data, (data.get("instrument") or obj.get("instrument") or "")),
    }


def _instrument_skew(data: dict, log_instrument: str) -> bool:
    canon = data.get("canonical_instrument") or data.get("CanonicalInstrument")
    exec_i = data.get("execution_instrument") or data.get("ExecutionInstrument")
    inst = data.get("instrument") or log_instrument
    if canon and exec_i and str(canon) != str(exec_i):
        return True
    if canon and inst and str(canon) != str(inst):
        # often same family MES vs ES
        if str(canon).strip().upper() != str(inst).strip().upper():
            return True
    return False


def effective_instrument(obj: dict, data: dict) -> str:
    inst = data.get("instrument") or obj.get("instrument") or ""
    if inst:
        return str(inst)
    pl = data.get("payload")
    if isinstance(pl, str):
        m = _PAYLOAD_INST.search(pl)
        if m:
            return m.group(1).upper()
    return ""


LINE_HINTS = (
    "iea_unavailable",
    "ORDER_REGISTRY_LOOKUP_IEA_MISMATCH",
    "UNEXPLAINED_WORKING_ORDER",
    "ORDER_REGISTRY_RECOVERY_DEFERRED_FAIL_CLOSED",
    "ADOPTION_NON_CONVERGENCE_ESCALATED",
    "IEA_ADOPTION_SCAN_SKIPPED_NO_PROGRESS",
    "pending_adoption_candidates",
    "pending_adoption_count",
    "MANUAL_OR_EXTERNAL_ORDER_DETECTED",
    "UNOWNED_LIVE_ORDER_DETECTED",
    "EXECUTION_UNOWNED",
    "ORDER_REGISTRY_RECOVERABLE_UNOWNED_DETECTED",
    "ADOPTION_GRACE_EXPIRED_UNOWNED",
    "MANUAL_FLATTEN_REQUIRED",
    "FOREIGN_INSTRUMENT_QTSW2_ORDER_SKIPPED",
    "canonical_instrument",
    "execution_instrument",
)


def iter_rows(files: list[str]):
    for fp in sorted(files):
        try:
            with open(fp, encoding="utf-8", errors="replace") as f:
                for line in f:
                    line = line.strip()
                    if not line:
                        continue
                    if not raw_line_positive_uwc(line) and not any(h in line for h in LINE_HINTS):
                        continue
                    try:
                        obj = json.loads(line)
                    except json.JSONDecodeError:
                        continue
                    data = obj.get("data") or {}
                    if not isinstance(data, dict):
                        data = {}
                    ev = obj.get("event") or ""
                    inst = effective_instrument(obj, data)
                    ts = parse_ts(obj.get("ts_utc"))
                    if ts is None:
                        continue
                    dl = cheap_lower_text(obj, data, ev)
                    flags = extract_flags(obj, data, ev, dl)
                    uwc = to_int(data.get("unexplained_working_count"))
                    in_scope = (
                        flags["uwc_pos"]
                        or flags["iea_unav"]
                        or flags["iea_mismatch"]
                        or flags["unexpl_ord"]
                        or flags["fail_closed"]
                        or flags["adopt_esc"]
                        or flags["adopt_skip"]
                        or flags["pending_adopt"]
                        or flags["unowned_adopt"]
                        or flags["ext_manual_evt"]
                        or flags["skew_hint"]
                    )
                    if not in_scope:
                        continue
                    yield Row(
                        ts=ts,
                        instrument=inst or "?",
                        run_id=str(obj.get("run_id") or ""),
                        event=ev,
                        uwc=uwc,
                        flags=flags,
                    )
        except OSError as e:
            print(f"skip {fp}: {e}", file=sys.stderr)


def primary_bucket(ep_flags: dict[str, int], max_uwc: int) -> str:
    """Return single letter A-G using first-hit priority (severity-ish)."""
    f = ep_flags
    if f.get("iea_mismatch", 0) > 0:
        return "C"
    if f.get("iea_unav", 0) > 0:
        return "B"
    if f.get("ext_manual_evt", 0) > 0:
        return "E"
    if f.get("skew_hint", 0) > 0:
        return "F"
    if (
        f.get("adopt_esc", 0) > 0
        or f.get("adopt_skip", 0) > 0
        or f.get("pending_adopt", 0) > 0
        or f.get("unexpl_ord", 0) > 0
        or f.get("unowned_adopt", 0) > 0
    ):
        return "D"
    if max_uwc > 0 and f.get("trusted_hint", 0) > 0:
        return "A"
    if max_uwc > 0:
        return "A"
    return "G"


def scan_corpus_line_hits(
    files: list[str], event_names: list[str], substrings: list[str]
) -> tuple[dict[str, int], dict[str, int]]:
    needles = {n: f'"event":"{n}"' for n in event_names}
    totals = {n: 0 for n in event_names}
    subs = {s: 0 for s in substrings}
    for fp in files:
        try:
            with open(fp, encoding="utf-8", errors="replace") as f:
                for line in f:
                    if '"event":"' in line:
                        for n, nd in needles.items():
                            if nd in line:
                                totals[n] += 1
                    for s in substrings:
                        if s in line:
                            subs[s] += 1
        except OSError:
            pass
    return totals, subs


def main():
    pattern = os.path.join(ROOT, "robot_*.jsonl")
    arch = os.path.join(ROOT, "archive", "robot_*.jsonl")
    files = glob.glob(pattern) + glob.glob(arch)
    if not files:
        print("No robot_*.jsonl under", ROOT)
        return

    key_events = [
        "ORDER_REGISTRY_LOOKUP_IEA_MISMATCH",
        "UNEXPLAINED_WORKING_ORDER",
        "ORDER_REGISTRY_RECOVERY_DEFERRED_FAIL_CLOSED",
        "ADOPTION_NON_CONVERGENCE_ESCALATED",
        "IEA_ADOPTION_SCAN_SKIPPED_NO_PROGRESS",
        "STATE_CONSISTENCY_GATE_RECONCILIATION_RESULT",
        "RECONCILIATION_CYCLE_STATE",
        "ORDER_REGISTRY_RECOVERABLE_UNOWNED_DETECTED",
        "MANUAL_OR_EXTERNAL_ORDER_DETECTED",
    ]
    corpus_totals, corpus_subs = scan_corpus_line_hits(
        files,
        key_events,
        ["iea_unavailable", "pending_adoption_candidates"],
    )

    rows = list(iter_rows(files))
    rows.sort(key=lambda r: (r.ts, r.instrument, r.run_id))

    # Global line counts for key events
    event_hits = defaultdict(int)
    uwc_lines = 0
    for r in rows:
        event_hits[r.event] += 1
        if r.uwc > 0:
            uwc_lines += 1

    # Episodes: same instrument, gap <= GAP_SEC
    episodes: list[dict] = []
    cur: dict | None = None

    def flush():
        nonlocal cur
        if cur:
            episodes.append(cur)
            cur = None

    for r in rows:
        if cur is None or cur["instrument"] != r.instrument:
            flush()
            cur = _new_ep(r)
            continue
        gap = (r.ts - cur["last_ts"]).total_seconds()
        if gap > GAP_SEC:
            flush()
            cur = _new_ep(r)
            continue
        _add_row(cur, r)
    flush()

    # Episodes with residual unexplained (max_uwc > 0) — core metric
    def has_uwc(e):
        return e["max_uwc"] > 0

    uwc_eps = [e for e in episodes if has_uwc(e)]
    bucket_stats: dict[str, list] = defaultdict(list)

    for e in uwc_eps:
        b = primary_bucket(e["flags"], e["max_uwc"])
        bucket_stats[b].append(e)

    with_adopt_esc = sum(1 for e in uwc_eps if e["flags"].get("adopt_esc", 0) > 0)
    with_readiness_pending = sum(
        1 for e in uwc_eps if e["flags"].get("readiness_pending_candidates", 0) > 0
    )

    # Reporting
    print("=== Residual risk log audit ===")
    print("Files:", len(files), " In-scope lines:", len(rows))
    print("Lines with numeric unexplained_working_count > 0 in data:", uwc_lines)
    print(
        "Lines with iea_unavailable flag (parsed in-scope):",
        sum(1 for r in rows if r.flags.get("iea_unav")),
    )
    print(
        "Lines with pending_adoption_count>0 (parsed in-scope, strict):",
        sum(1 for r in rows if r.flags.get("pending_adopt")),
    )
    print(
        "Corpus lines containing iea_unavailable (raw):",
        corpus_subs.get("iea_unavailable", 0),
    )
    print(
        "Corpus lines containing pending_adoption_candidates (raw):",
        corpus_subs.get("pending_adoption_candidates", 0),
    )
    print()

    print("=== Event line counts (full corpus, raw substring) ===")
    for k in key_events:
        print(f"  {k}: {corpus_totals.get(k, 0)}")
    print()
    print("=== Event line counts (parsed in-scope lines only) ===")
    for k in key_events:
        print(f"  {k}: {event_hits.get(k, 0)}")
    print()

    print("=== Episodes with max unexplained_working_count > 0 ===")
    print("Episode definition: instrument + <=10min gap between in-scope lines")
    print("Total such episodes:", len(uwc_eps))
    print(
        "Of those, episodes containing ADOPTION_NON_CONVERGENCE_ESCALATED:",
        with_adopt_esc,
    )
    print(
        "Of those, episodes containing readiness_pending_candidates on any line:",
        with_readiness_pending,
    )
    print()

    order = ["A", "B", "C", "D", "E", "F", "G"]
    for b in order:
        eps = bucket_stats.get(b, [])
        if not eps:
            continue
        dur_sec = [(e["last_ts"] - e["start"]).total_seconds() for e in eps]
        insts = {e["instrument"] for e in eps}
        fc = sum(1 for e in eps if e["fail_closed"])
        print(f"--- Bucket {b} ({len(eps)} episodes) ---")
        print(f"  instruments: {len(insts)} unique - {sorted(insts)[:20]}{' ...' if len(insts)>20 else ''}")
        print(f"  duration sec: avg={sum(dur_sec)/len(dur_sec):.1f} worst={max(dur_sec):.1f}")
        print(f"  fail_closed episodes: {fc} / {len(eps)}")
        # representative: longest duration
        rep = max(eps, key=lambda e: (e["last_ts"] - e["start"]).total_seconds())
        print("  representative timeline (longest in bucket):")
        for ts, ev, uwc in rep["events"][:12]:
            print(f"    {ts.isoformat()}  uwc={uwc}  {ev}")
        if len(rep["events"]) > 12:
            print(f"    ... +{len(rep['events'])-12} more lines")
        print()

    # Rank buckets by episode count
    ranked = sorted(((b, len(bucket_stats[b])) for b in bucket_stats), key=lambda x: -x[1])
    print("=== Bucket ranking by episode count (uwc>0 episodes) ===")
    for b, n in ranked:
        print(f"  {b}: {n}")

    print()
    print("=== Interpretation (automated) ===")
    if ranked:
        common = ranked[0][0]
        # dangerous: fail-closed rate within bucket, then escalation mass
        danger_scores = []
        for b in order:
            eps = bucket_stats.get(b, [])
            if not eps:
                continue
            fc = sum(1 for e in eps if e["fail_closed"])
            esc = sum(e["flags"].get("adopt_esc", 0) for e in eps)
            rate = fc / len(eps)
            danger_scores.append((rate, fc, esc, len(eps), b))
        danger_scores.sort(reverse=True)
        dangerous = danger_scores[0][4] if danger_scores else "?"

        def bucket_label(b: str) -> str:
            return {
                "A": "trusted undercount / generic uwc without stronger signal",
                "B": "IEA unavailable/incoherent",
                "C": "wrong/ambiguous IEA (registry lookup mismatch)",
                "D": "adoption lag / UNEXPLAINED_WORKING_ORDER / pending adoption",
                "E": "external/manual order detection",
                "F": "instrument key skew (canonical vs execution)",
                "G": "other",
            }[b]

        print(f"Most common residual bucket: {common} - {bucket_label(common)}")
        print(f"Most dangerous bucket (heuristic: fail_closed + escalation weight): {dangerous} - {bucket_label(dangerous)}")
        # next fix: highest count among B,C,D,E,F (exclude A generic)
        fix_candidates = [(n, b) for b, n in ranked if b in {"B", "C", "D", "E", "F"}]
        if fix_candidates:
            fix_candidates.sort(reverse=True)
            print(f"Next highest-value fix target (non-generic buckets): {fix_candidates[0][1]} - {bucket_label(fix_candidates[0][1])}")
        else:
            print("Next highest-value fix target: A - tighten trusted working / reconciliation accounting")


def _new_ep(r: Row) -> dict:
    ep = {
        "instrument": r.instrument,
        "start": r.ts,
        "last_ts": r.ts,
        "max_uwc": r.uwc,
        "flags": defaultdict(int),
        "fail_closed": False,
        "events": [],
    }
    _add_row(ep, r)
    return ep


def _add_row(ep: dict, r: Row) -> None:
    ep["last_ts"] = r.ts
    ep["max_uwc"] = max(ep["max_uwc"], r.uwc)
    ep["events"].append((r.ts, r.event, r.uwc))
    if r.flags.get("fail_closed"):
        ep["fail_closed"] = True
    for k, v in r.flags.items():
        if v:
            ep["flags"][k] += 1


if __name__ == "__main__":
    main()
