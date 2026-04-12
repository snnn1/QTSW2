"""
Forensic: isolate Bucket E episodes (same rules as residual_risk_log_audit.py) and
expand each to full event chains from corpus within a time window.
"""
from __future__ import annotations

import glob
import json
import os
import re
from collections import defaultdict
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from typing import Any

# Reuse audit constants
ROOT = os.path.join(os.path.dirname(__file__), "..", "logs", "robot")
GAP_SEC = 600
PRE_SEC = 90
POST_SEC = 120

_PAYLOAD_INST = re.compile(r"instrument\s*=\s*([A-Z][A-Z0-9]*)", re.I)
_UWC_POS = re.compile(r'"unexplained_working_count"\s*:\s*"([0-9]+)"')
_TS_INLINE = re.compile(r'"ts_utc"\s*:\s*"([^"]+)"')

EXTERNAL_MANUAL_EVENTS = frozenset({
    "MANUAL_OR_EXTERNAL_ORDER_DETECTED",
    "MANUAL_FLATTEN_REQUIRED",
    "FOREIGN_INSTRUMENT_QTSW2_ORDER_SKIPPED",
})
UNOWNED_ADOPTION_EVENTS = frozenset({
    "UNOWNED_LIVE_ORDER_DETECTED",
    "EXECUTION_UNOWNED",
    "ORDER_REGISTRY_RECOVERABLE_UNOWNED_DETECTED",
    "ADOPTION_GRACE_EXPIRED_UNOWNED",
})

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


def parse_ts_safe(s: str | None):
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


def raw_line_positive_uwc(line: str) -> bool:
    m = _UWC_POS.search(line)
    if not m:
        return False
    try:
        return int(m.group(1)) > 0
    except ValueError:
        return False


def cheap_lower_text(obj: dict, data: dict, event: str) -> str:
    parts = [event or "", str(obj.get("message") or "")]
    for k in ("readiness_summary", "readiness_contradictions", "reconciliation_outcome", "note", "reason"):
        v = data.get(k)
        if v is not None:
            parts.append(str(v))
    return " ".join(parts).lower()


def _instrument_skew(data: dict, log_instrument: str) -> bool:
    canon = data.get("canonical_instrument") or data.get("CanonicalInstrument")
    exec_i = data.get("execution_instrument") or data.get("ExecutionInstrument")
    inst = data.get("instrument") or log_instrument
    if canon and exec_i and str(canon) != str(exec_i):
        return True
    if canon and inst and str(canon).strip().upper() != str(inst).strip().upper():
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


def extract_flags(obj: dict, data: dict, event: str, dl: str) -> dict[str, bool]:
    iea_unav = str(data.get("iea_unavailable", "")).lower() in ("true", "1")
    rsum = str(data.get("readiness_summary") or "").lower()
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


def primary_bucket(ep_flags: dict[str, int], max_uwc: int) -> str:
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


@dataclass
class Row:
    ts: datetime
    instrument: str
    run_id: str
    event: str
    uwc: int
    flags: dict[str, bool]
    data: dict


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
                    ts = parse_ts_safe(obj.get("ts_utc"))
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
                    yield Row(ts=ts, instrument=inst or "?", run_id=str(obj.get("run_id") or ""), event=ev, uwc=uwc, flags=flags, data=data)
        except OSError:
            pass


def _new_ep(r: Row) -> dict:
    ep = {
        "instrument": r.instrument,
        "start": r.ts,
        "last_ts": r.ts,
        "max_uwc": r.uwc,
        "flags": defaultdict(int),
        "fail_closed": False,
        "rows": [],
    }
    _add_ep(ep, r)
    return ep


def _add_ep(ep: dict, r: Row) -> None:
    ep["last_ts"] = r.ts
    ep["max_uwc"] = max(ep["max_uwc"], r.uwc)
    ep["rows"].append(r)
    if r.flags.get("fail_closed"):
        ep["fail_closed"] = True
    for k, v in r.flags.items():
        if v:
            ep["flags"][k] += 1


def cluster_episodes(rows: list[Row]) -> list[dict]:
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
        _add_ep(cur, r)
    flush()
    return episodes


def relevant_log_files(all_files: list[str], inst: str) -> list[str]:
    """Per-instrument jsonl plus ENGINE streams (deferral payloads reference instrument)."""
    out: list[str] = []
    for fp in all_files:
        bn = os.path.basename(fp)
        if inst and inst != "?" and bn.startswith(f"robot_{inst}") and bn.endswith(".jsonl"):
            out.append(fp)
        elif "robot_ENGINE" in bn:
            out.append(fp)
        elif bn == "robot_ENGINE.jsonl":
            out.append(fp)
    return sorted(set(out)) if out else sorted(all_files)


def fast_ts_from_line(line: str):
    m = _TS_INLINE.search(line)
    if not m:
        return None
    s = m.group(1).replace("\\u002B", "+").replace("\\u002b", "+")
    return parse_ts_safe(s)


def expand_chain(files: list[str], inst: str, t0: datetime, t1: datetime, focus_events: frozenset[str] | None = None):
    """Parseable log rows in [t0,t1] whose effective instrument matches inst."""
    out: list[tuple[datetime, str, dict, dict]] = []
    for fp in relevant_log_files(files, inst):
        try:
            with open(fp, encoding="utf-8", errors="replace") as f:
                for line in f:
                    fts = fast_ts_from_line(line)
                    if fts is None or fts < t0 or fts > t1:
                        continue
                    try:
                        obj = json.loads(line.strip())
                    except json.JSONDecodeError:
                        continue
                    data = obj.get("data") if isinstance(obj.get("data"), dict) else {}
                    if inst and inst != "?" and effective_instrument(obj, data) != inst:
                        continue
                    ts = parse_ts_safe(obj.get("ts_utc")) or fts
                    ev = obj.get("event") or ""
                    if focus_events is not None and ev not in focus_events and focus_events:
                        continue
                    out.append((ts, ev, data, obj))
        except OSError:
            pass
    out.sort(key=lambda x: (x[0], x[1]))
    return out


def qtsw2_oco_evidence(text: str) -> list[str]:
    hints: list[str] = []
    t = text.upper()
    if "QTSW2" in t:
        signals = ["QTSW2", "OCO", "oco_group", "INTENT"]
        hints.extend([s for s in signals if s.lower() in text.lower() or s in t])
    if "oco_group" in text.lower():
        hints.append("oco_group_field")
    return list(dict.fromkeys(hints))


def summarize_manual_payload(data: dict) -> dict[str, Any]:
    return {
        "broker_order_id": data.get("broker_order_id"),
        "intent_id": data.get("intent_id"),
        "ownership_status": data.get("ownership_status"),
        "source_context": data.get("source_context"),
        "note": data.get("note"),
        "policy": data.get("policy"),
        "tag": data.get("tag") or data.get("encoded_tag"),
        "order_state": data.get("order_state"),
    }


def classify_episode(ep_rows: list[Row], chain_summary: str) -> tuple[str, str, str]:
    """Returns (ownership_guess, false_vs_true, narrow_fix)."""
    man_lines = [r for r in ep_rows if r.event == "MANUAL_OR_EXTERNAL_ORDER_DETECTED"]
    own = [r.data.get("ownership_status") for r in man_lines if r.data.get("ownership_status")]
    intents = [r.data.get("intent_id") for r in man_lines if r.data.get("intent_id")]
    policy = next((r.data.get("policy") for r in man_lines if r.data.get("policy")), None)
    ctx = [r.data.get("source_context") for r in man_lines if r.data.get("source_context")]
    if ctx:
        ctx_s = ",".join(str(c) for c in dict.fromkeys(ctx))
    else:
        ctx_s = ""
    if own and all(str(o).upper().find("RECOVERABLE") >= 0 for o in own):
        og = "Logged as RECOVERABLE_ROBOT_OWNED (registry classifies recoverable, not raw UNOWNED)"
    elif own:
        og = "Registry ownership on MANUAL_OR_EXTERNAL log: " + ",".join(str(x) for x in set(own))
        if ctx_s:
            og += f"; source_context={ctx_s}"
    elif policy == "FAIL_CLOSED_FLATTEN":
        og = "Sim path: policy FAIL_CLOSED_FLATTEN (order update before registry/OrderMap); see NinjaTraderSimAdapter"
    else:
        og = "Unknown ownership from cluster rows; expand window for submit/tag chain"
        if ctx_s:
            og += f" source_context={ctx_s}"

    q = qtsw2_oco_evidence(chain_summary)
    if "STALE_QTSW2_ORDER_DETECTED" in ctx_s:
        ft = "False 'external': STALE_QTSW2 path - robot-tagged order treated as stale/unowned (journal/adoption gap), not operator manual"
    elif q and intents and any(str(i).strip() for i in intents):
        ft = "Likely misclassified robot order: intent_id present + QTSW2 evidence; typical race OrderMap/registry vs broker updates"
    elif q:
        ft = "Ambiguous: QTSW2/OCO hints without clean intent linkage in window"
    elif not any(str(i).strip() for i in intents):
        ft = "Possible true external/manual unless intent omitted in log payload"
    else:
        ft = "Review full chain"

    if "STALE_QTSW2" in chain_summary or "STALE_QTSW2_ORDER_DETECTED" in ctx_s:
        nf = "IEA stale QTSW2 stop/target: improve journal adoption candidate or relax UNOWNED registration when tag proves robot ownership"
    elif policy == "FAIL_CLOSED_FLATTEN":
        nf = "Register broker order id in OrderMap/registry synchronously on submit-accept before first Working/CancelPending update"
    elif "ORDER_REGISTRY_RECOVERY_DEFERRED_FAIL_CLOSED" in chain_summary or "ORDER_REGISTRY_MISSING_FAIL_CLOSED" in chain_summary:
        nf = "Registry missing path: fix iea_working=0 during broker working (adoption/IEA availability) before deferring fail-closed"
    else:
        nf = "Broker id resolution: alias pre/post ack in TryResolveByBrokerOrderId"

    return og, ft, nf


def main():
    pattern = os.path.join(ROOT, "robot_*.jsonl")
    arch = os.path.join(ROOT, "archive", "robot_*.jsonl")
    files = glob.glob(pattern) + glob.glob(arch)

    rows = list(iter_rows(files))
    rows.sort(key=lambda r: (r.ts, r.instrument, r.run_id))
    episodes = cluster_episodes(rows)
    uwc_eps = [e for e in episodes if e["max_uwc"] > 0]
    e_eps = [e for e in uwc_eps if primary_bucket(e["flags"], e["max_uwc"]) == "E"]
    e_eps.sort(key=lambda e: e["start"])

    print(f"Bucket E episodes (uwc>0): {len(e_eps)}")
    print()

    # Aggregate root causes
    root_tags: list[str] = []
    danger_notes: list[tuple[float, str, str]] = []  # fail_closed, inst, tag

    for i, ep in enumerate(e_eps, 1):
        inst = ep["instrument"]
        t0 = ep["start"] - timedelta(seconds=PRE_SEC)
        t1 = ep["last_ts"] + timedelta(seconds=POST_SEC)
        triggers = [r for r in ep["rows"] if r.event in EXTERNAL_MANUAL_EVENTS]
        trig_ev = triggers[0].event if triggers else "(none in-cluster)"
        chain = expand_chain(files, inst, t0, t1, focus_events=frozenset())
        # compact chain for print: key events + any line with broker_order_id from manual
        key_ev = frozenset({
            "MANUAL_OR_EXTERNAL_ORDER_DETECTED", "ORDER_REGISTRY_RECOVERABLE_UNOWNED_DETECTED",
            "ORDER_REGISTRY_RECOVERY_DEFERRED_FAIL_CLOSED", "STATE_CONSISTENCY_GATE_RECONCILIATION_RESULT",
            "ORDER_SUBMITTED", "ORDER_SUBMIT_SUCCESS", "INTENT_POLICY_REGISTERED",
            "EXECUTION_UPDATE_UNKNOWN_ORDER", "ADOPTION_NON_CONVERGENCE_ESCALATED",
            "REGISTRY_BROKER_DIVERGENCE", "ORDER_REGISTRY_MISSING_FAIL_CLOSED",
        })
        raw_blob: list[str] = []
        manual_summaries = []
        for ts, ev, data, obj in chain:
            raw_blob.append(json.dumps(data, default=str))
            if ev == "MANUAL_OR_EXTERNAL_ORDER_DETECTED":
                manual_summaries.append(summarize_manual_payload(data))

        full_text = " ".join(raw_blob)
        og, ft, nf = classify_episode(ep["rows"], full_text)
        qev = qtsw2_oco_evidence(full_text)

        appropriate = "Deferred when registry/ownership unresolved; conservative if RECOVERABLE+QTSW2 visible in snapshot but defer still fires"
        if ep["fail_closed"] and "RECOVERABLE" in full_text:
            appropriate = "Overly conservative: fail-closed defer coexists with recoverable-unowned signals in window"
        elif ep["fail_closed"]:
            appropriate = "Plausible: explicit defer with broker_working vs robot_tagged snapshot (verify per payload)"

        tag = f"{trig_ev}:{inst}"
        root_tags.append(tag)
        danger_notes.append((1.0 if ep["fail_closed"] else 0.0, inst, trig_ev))

        print(f"======== Episode {i}/{len(e_eps)} ========")
        print(f"Instrument: {inst} | Episode window (cluster): {ep['start'].isoformat()} .. {ep['last_ts'].isoformat()}")
        print(f"max_uwc={ep['max_uwc']} | fail_closed_in_episode={ep['fail_closed']}")
        print(f"Trigger (first external/manual in cluster): {trig_ev}")
        if manual_summaries:
            print(f"MANUAL_OR_EXTERNAL payloads: {json.dumps(manual_summaries, default=str)[:600]}")
        print(f"Ownership / classification read: {og}")
        print(f"False vs true manual/external: {ft}")
        print(f"QTSW2/OCO evidence in expanded window: {qev or 'none detected in parsed data blobs'}")
        print(f"Registry deferral appropriateness: {appropriate}")
        print(f"Narrowest fix target: {nf}")
        print("Event chain (time order, expanded window; skip high-volume noise):")
        skip_prefix = (
            "HEARTBEAT_", "TICK_", "BAR_", "RANGE_", "HYDRATION_", "FRONTEND_",
        )
        shown = 0
        for ts, ev, data, _obj in chain:
            if ev.startswith(skip_prefix):
                continue
            uwc = to_int(data.get("unexplained_working_count"))
            pl = data.get("payload")
            interesting = (
                ev in key_ev
                or ev in EXTERNAL_MANUAL_EVENTS
                or uwc > 0
                or "ORDER_REGISTRY" in ev
                or "RECONCILIATION" in ev
                or "ADOPTION" in ev
                or ev in ("ORDER_SUBMITTED", "ORDER_SUBMIT_SUCCESS", "ORDER_FILLED", "ORDER_CANCELLED")
            )
            if not interesting:
                continue
            line = f"  {ts.isoformat()} | {ev} | uwc={uwc}"
            if data.get("broker_order_id"):
                line += f" | oid={data.get('broker_order_id')}"
            if data.get("intent_id"):
                line += f" | intent={data.get('intent_id')}"
            if data.get("ownership_status"):
                line += f" | own={data.get('ownership_status')}"
            if data.get("note") and ev == "MANUAL_OR_EXTERNAL_ORDER_DETECTED":
                line += f" | note={str(data.get('note'))[:100]}"
            if isinstance(pl, str):
                line += f" | payload_snip={pl[:180]}{'...' if len(pl)>180 else ''}"
            print(line)
            shown += 1
            if shown > 100:
                print("  ... truncated ...")
                break
        print()

    # Summary
    from collections import Counter
    c = Counter(root_tags)
    print("=== SUMMARY ===")
    print("Most common Bucket E root cause (trigger:instrument):", c.most_common(3))
    # most dangerous: fail_closed episodes first
    fc_eps = [e for e in e_eps if e["fail_closed"]]
    print(f"Episodes with fail-closed: {len(fc_eps)} / {len(e_eps)}")
    print("Most dangerous (heuristic): fail-closed Bucket E episodes tie to ORDER_REGISTRY_RECOVERY_DEFERRED_FAIL_CLOSED bursts after MANUAL_OR_EXTERNAL_ORDER_DETECTED")


if __name__ == "__main__":
    main()
