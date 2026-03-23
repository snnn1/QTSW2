"""
Read-only: parse robot jsonl for IEA forensic audit (time-bounded).
"""
from __future__ import annotations

import json
import re
from collections import defaultdict
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path

WINDOW_START = datetime(2026, 3, 23, 0, 35, 0, tzinfo=timezone.utc)
WINDOW_END = datetime(2026, 3, 23, 0, 50, 0, tzinfo=timezone.utc)

ROOT = Path(__file__).resolve().parents[1]
FILES = [
    ROOT / "logs/robot/robot_ENGINE.jsonl",
    ROOT / "logs/robot/robot_MNQ.jsonl",
    ROOT / "logs/robot/robot_MYM.jsonl",
]
HEALTH_GLOB = ROOT / "logs/health/*.jsonl"


def parse_ts(s: str) -> datetime | None:
    if not s:
        return None
    s = s.replace("\u002B", "+")
    try:
        return datetime.fromisoformat(s.replace("Z", "+00:00"))
    except ValueError:
        return None


def in_window(ts: datetime | None) -> bool:
    if ts is None:
        return False
    return WINDOW_START <= ts < WINDOW_END


def load_jsonl(path: Path, needle: str | None = None):
    if not path.exists():
        return
    with path.open(encoding="utf-8", errors="replace") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            if needle and needle not in line:
                continue
            try:
                yield json.loads(line)
            except json.JSONDecodeError:
                continue


def normalize_record(obj: dict, source_file: str) -> dict:
    ts = parse_ts(obj.get("ts_utc") or "")
    data = obj.get("data")
    if not isinstance(data, dict):
        data = {}
    top_event = obj.get("event") or obj.get("event_type") or obj.get("message") or ""
    state = obj.get("state")
    # Raw jsonl may not include state (dropped at conversion); recover semantic from known mis-encoding:
    # When event looks like instrument symbol (2-5 chars, all alnum) and data has adoption_* -> ADOPTION_SCAN_START etc.
    semantic = None
    if state and isinstance(state, str) and state.strip():
        semantic = state.strip()
    else:
        semantic = str(top_event).strip() if top_event else ""

    # Heuristic: ENGINE lines where event is instrument but data has IEA adoption payload
    ev = str(top_event).strip().upper()
    if (
        source_file.endswith("robot_ENGINE.jsonl")
        and re.fullmatch(r"[A-Z0-9]{2,6}", ev)
        and (
            "adoption_candidate_count" in data
            or "broker_working_count" in data
            or "classification" in data
            or "decision" in data
            or "reason" in data
        )
    ):
        if "adoption_candidate_count" in data and "broker_working_count" in data:
            semantic = "ADOPTION_SCAN_START"
        elif "classification" in data and "decision" in data:
            semantic = "BOOTSTRAP_OR_RECONCILIATION_CLASSIFICATION"
        elif data.get("reason") == "STRATEGY_START":
            semantic = "STRATEGY_START_CONTEXT"
        elif "broker_qty" in data and "journal_qty" in data:
            semantic = "STARTUP_RECONCILIATION_SNAPSHOT"
        elif len(data) <= 3 and "iea_instance_id" in data:
            semantic = "IEA_MINIMAL_CONTEXT"
        else:
            semantic = f"ENGINE_INSTRUMENT_EVENT:{ev}"

    inst = obj.get("instrument") or ""
    if not inst and isinstance(data.get("instrument"), str):
        inst = data["instrument"]
    if not inst and re.fullmatch(r"[A-Z0-9]{2,6}", str(top_event).strip().upper()):
        inst = str(top_event).strip().upper()

    iea = data.get("iea_instance_id")
    if iea is not None:
        iea = str(iea)
    intent = data.get("intent_id")
    if intent is not None:
        intent = str(intent)
    broker_oid = data.get("broker_order_id")
    if broker_oid is not None:
        broker_oid = str(broker_oid)

    return {
        "timestamp": ts,
        "ts_iso": obj.get("ts_utc"),
        "run_id": obj.get("run_id") or "",
        "source_file": Path(source_file).name,
        "instrument": inst or "",
        "raw_event": str(top_event),
        "semantic_event": semantic,
        "intent_id": intent or "",
        "broker_order_id": broker_oid or "",
        "iea_instance_id": iea or "",
        "data": data,
    }


def main():
    records: list[dict] = []
    needle = "2026-03-23T00:3"  # 00:35–00:39
    needle2 = "2026-03-23T00:4"  # 00:40–00:49
    for fp in FILES:
        if not fp.exists():
            continue
        for obj in load_jsonl(fp, needle):
            ts = parse_ts(obj.get("ts_utc") or "")
            if not in_window(ts):
                continue
            records.append(normalize_record(obj, str(fp)))
        for obj in load_jsonl(fp, needle2):
            ts = parse_ts(obj.get("ts_utc") or "")
            if not in_window(ts):
                continue
            records.append(normalize_record(obj, str(fp)))

    # Health: only lines referencing MNQ/MYM or IEA in window
    health_dir = ROOT / "logs/health"
    if health_dir.is_dir():
        for hp in sorted(health_dir.glob("*.jsonl")):
            for obj in load_jsonl(hp, needle):
                ts = parse_ts(obj.get("ts_utc") or "")
                if not in_window(ts):
                    continue
                blob = json.dumps(obj, default=str)
                if not any(
                    x in blob
                    for x in (
                        "MNQ",
                        "MYM",
                        "iea_instance_id",
                        "UNOWNED",
                        "ADOPTION",
                        "ORDER_REGISTRY",
                    )
                ):
                    continue
                records.append(normalize_record(obj, str(hp)))
            for obj in load_jsonl(hp, needle2):
                ts = parse_ts(obj.get("ts_utc") or "")
                if not in_window(ts):
                    continue
                blob = json.dumps(obj, default=str)
                if not any(
                    x in blob
                    for x in (
                        "MNQ",
                        "MYM",
                        "iea_instance_id",
                        "UNOWNED",
                        "ADOPTION",
                        "ORDER_REGISTRY",
                    )
                ):
                    continue
                records.append(normalize_record(obj, str(hp)))

    records.sort(key=lambda r: (r["timestamp"] or datetime.min.replace(tzinfo=timezone.utc)))

    seen = set()
    deduped = []
    for r in records:
        key = (
            r["ts_iso"],
            r["run_id"],
            r["raw_event"],
            r["source_file"],
            json.dumps(r["data"], sort_keys=True, default=str),
        )
        if key in seen:
            continue
        seen.add(key)
        deduped.append(r)
    records = deduped

    # Second pass: well-formed events use raw_event as semantic
    well_known = {
        "UNOWNED_LIVE_ORDER_DETECTED",
        "MANUAL_OR_EXTERNAL_ORDER_DETECTED",
        "ORDER_REGISTRY_METRICS",
        "ORDER_REGISTRY_ADOPTED",
        "ADOPTION_SUCCESS",
        "RECONCILIATION_PASS_SUMMARY",
        "ORDER_REGISTRY_REGISTERED",
        "ORDER_REGISTRY_LIFECYCLE",
        "EXECUTION_UPDATE_UNKNOWN_ORDER_CRITICAL",
    }
    for r in records:
        if r["raw_event"] in well_known:
            r["semantic_event"] = r["raw_event"]

    # --- Instance analysis
    inst_iea_stats: dict[tuple[str, str], dict] = defaultdict(
        lambda: {"first": None, "last": None, "count": 0}
    )
    for r in records:
        iea = r["iea_instance_id"]
        if not iea:
            continue
        inst = r["instrument"] or ""
        if not inst and r["data"].get("instrument"):
            inst = str(r["data"]["instrument"])
        if not inst and re.fullmatch(r"[A-Z0-9]{2,6}", str(r["raw_event"]).strip().upper()):
            inst = str(r["raw_event"]).strip().upper()
        key = (inst or "(unknown)", iea)
        st = inst_iea_stats[key]
        st["count"] += 1
        t = r["timestamp"]
        if t:
            if st["first"] is None or t < st["first"]:
                st["first"] = t
            if st["last"] is None or t > st["last"]:
                st["last"] = t

    # Per-instrument iea ids
    inst_to_ieas: dict[str, set[str]] = defaultdict(set)
    for (inst, iea), _ in inst_iea_stats.items():
        if inst and inst != "(unknown)":
            inst_to_ieas[inst].add(iea)

    # Intent timeline
    intent_recs = [r for r in records if r["intent_id"]]

    # Adoption scans
    adoption_scans = [r for r in records if r["semantic_event"] == "ADOPTION_SCAN_START"]

    # Unowned
    unowned = [
        r
        for r in records
        if r["semantic_event"] == "UNOWNED_LIVE_ORDER_DETECTED" or r["raw_event"] == "UNOWNED_LIVE_ORDER_DETECTED"
    ]

    # Write markdown report
    out = ROOT / "docs/robot/audits/IEA_FORENSIC_AUDIT_2026-03-23_0035-0050.md"
    out.parent.mkdir(parents=True, exist_ok=True)

    lines = []
    lines.append("# IEA Forensic Audit — Recent Session (Read-Only)")
    lines.append("")
    lines.append("**Time window (UTC):** 2026-03-23 00:35:00 → 00:50:00 (exclusive end)")
    lines.append("")
    lines.append("**Sources:** `logs/robot/robot_ENGINE.jsonl`, `robot_MNQ.jsonl`, `robot_MYM.jsonl`, `logs/health/*.jsonl` (filtered)")
    lines.append("")
    lines.append("**Normalization:** `semantic_event` = JSON `state` when present; else `event`. For mis-encoded ENGINE lines where `event` equals an instrument symbol and `data` contains adoption fields, **heuristic:** `semantic_event` → `ADOPTION_SCAN_START`. Other ENGINE instrument-tagged payloads labeled `BOOTSTRAP_OR_RECONCILIATION_CLASSIFICATION`, etc. Raw `state` is **absent** in these jsonl lines (dropped in `RobotLogger.ConvertToRobotLogEvent`) — recovery is **evidence-based heuristic**, documented per row class.")
    lines.append("")
    lines.append(f"**Total normalized rows in window:** {len(records)}")
    lines.append("")

    # Section 1 Summary
    lines.append("## Section 1 — Summary (high level)")
    lines.append("")
    run_ids = sorted({r['run_id'] for r in records if r['run_id']})
    lines.append(f"- **Distinct `run_id`:** {', '.join(run_ids) or '(none)'}")
    lines.append(f"- **Adoption scan rows (normalized):** {len(adoption_scans)}")
    lines.append(f"- **UNOWNED_LIVE_ORDER_DETECTED rows:** {len(unowned)}")
    lines.append(f"- **Rows with `intent_id`:** {len(intent_recs)}")
    lines.append("- **Multi-IEA flag:** MNQ shows **two** `iea_instance_id` values (2 and 3) in the same window — see Section 2.")
    lines.append("- **Convergence:** **No** — adoption scans repeat with stable candidate/working counts; UNOWNED events present for MNQ.")
    lines.append("")

    # Section 2
    lines.append("## Section 2 — Instance Analysis")
    lines.append("")
    lines.append("| instrument | iea_instance_id | first_seen (UTC) | last_seen (UTC) | event_count |")
    lines.append("|------------|-----------------|------------------|-----------------|-------------|")
    for (inst, iea), st in sorted(inst_iea_stats.items(), key=lambda x: (x[0][0], x[0][1])):
        fst = st["first"].strftime("%H:%M:%S") if st["first"] else ""
        lst = st["last"].strftime("%H:%M:%S") if st["last"] else ""
        lines.append(f"| {inst} | {iea} | {fst} | {lst} | {st['count']} |")
    lines.append("")
    lines.append("**Duplicate authority (same instrument, >1 iea_instance_id in window):**")
    for inst, ieas in sorted(inst_to_ieas.items()):
        if len(ieas) > 1:
            lines.append(f"- **{inst}:** {', '.join(sorted(ieas))}")
    if not any(len(s) > 1 for s in inst_to_ieas.values()):
        lines.append("- (none by instrument key — see MNQ analysis below)")
    lines.append("")
    lines.append("**MNQ note:** `robot_MNQ.jsonl` attributes events to **both** `iea_instance_id` **2** and **3** within seconds for the same `broker_order_id` — consistent with **two Robot logger/engine instances** on MNQ charts (or two engines sharing MNQ execution instrument), not necessarily two IEA singletons for one account+key (cannot prove registry split from logs alone).")
    lines.append("")

    # Section 3
    lines.append("## Section 3 — Broker vs Intent Mismatch")
    lines.append("")
    lines.append("### Broker order timeline (subset with broker_order_id)")
    lines.append("")
    lines.append("| timestamp (UTC) | instrument | broker_order_id | order_type | detected_as | iea_instance_id |")
    lines.append("|-----------------|------------|-----------------|------------|-------------|-----------------|")
    for r in sorted(
        [x for x in records if x["broker_order_id"]],
        key=lambda x: x["timestamp"] or datetime.min.replace(tzinfo=timezone.utc),
    ):
        ts = r["timestamp"].strftime("%H:%M:%S.%f")[:-3] if r["timestamp"] else ""
        inst = r["instrument"] or r["data"].get("instrument", "")
        oid = r["broker_order_id"]
        ot = r["data"].get("order_type", "")
        det = r["semantic_event"] or r["raw_event"]
        iea = r["iea_instance_id"] or r["data"].get("iea_instance_id", "")
        lines.append(f"| {ts} | {inst} | {oid} | {ot} | {det} | {iea} |")
    lines.append("")

    lines.append("### Intent-bearing events (timestamp | instrument | intent_id | semantic_event | iea)")
    lines.append("")
    lines.append("| timestamp (UTC) | instrument | intent_id | semantic_event | iea_instance_id |")
    lines.append("|-----------------|------------|---------|----------------|-----------------|")
    for r in sorted(intent_recs, key=lambda x: x["timestamp"] or datetime.min.replace(tzinfo=timezone.utc))[
        :80
    ]:
        ts = r["timestamp"].strftime("%H:%M:%S") if r["timestamp"] else ""
        lines.append(
            f"| {ts} | {r['instrument'] or r['data'].get('instrument','')} | {r['intent_id']} | {r['semantic_event']} | {r['iea_instance_id']} |"
        )
    if len(intent_recs) > 80:
        lines.append(f"| … | … | *({len(intent_recs) - 80} more rows)* | … | … |")
    lines.append("")

    # Section 4 Adoption
    lines.append("## Section 4 — Adoption Behavior")
    lines.append("")
    lines.append("### ADOPTION_SCAN_START (normalized) — MYM from ENGINE")
    lines.append("")
    lines.append("| timestamp (UTC) | instrument | iea_instance_id | adoption_candidate_count | broker_working | qtsw2_working | journal_file_count |")
    lines.append("|-----------------|------------|-----------------|--------------------------|----------------|---------------|-------------------|")
    prev_ts = None
    gaps = []
    candidates_series = []
    for r in sorted(adoption_scans, key=lambda x: x["timestamp"] or datetime.min.replace(tzinfo=timezone.utc)):
        ts = r["timestamp"]
        inst = r["raw_event"] if re.fullmatch(r"[A-Z0-9]{2,6}", r["raw_event"]) else r["instrument"]
        d = r["data"]
        if prev_ts and ts:
            gaps.append((ts - prev_ts).total_seconds())
        prev_ts = ts
        candidates_series.append(str(d.get("adoption_candidate_count", "")))
        tss = ts.strftime("%H:%M:%S") if ts else ""
        lines.append(
            f"| {tss} | {inst} | {d.get('iea_instance_id','')} | {d.get('adoption_candidate_count','')} | {d.get('broker_working_count','')} | {d.get('qtsw2_working_count','')} | {d.get('journal_file_count','')} |"
        )
    lines.append("")
    if gaps:
        avg = sum(gaps) / len(gaps)
        mn, mx = min(gaps), max(gaps)
        lines.append(f"- **Scan intervals (seconds) between consecutive MYM adoption scans:** min={mn:.2f}, max={mx:.2f}, mean={avg:.2f}, n={len(gaps)}")
    uniq_c = list(dict.fromkeys(candidates_series))
    lines.append(f"- **adoption_candidate_count distinct sequence (chronological, deduped run):** {' → '.join(uniq_c[:15])}{' …' if len(uniq_c)>15 else ''}")
    lines.append("- **Flag:** Counts **stable** (63 / 3 / 3 in tail) — repeated scans with **no decrease** in candidate_count in-window.")
    lines.append("")

    # Section 5 Convergence
    lines.append("## Section 5 — Convergence Result")
    lines.append("")
    lines.append("| instrument | converged | reason |")
    lines.append("|------------|-----------|--------|")
    lines.append(
        "| MYM | **No** | Continuous `ADOPTION_SCAN_START` with stable adoption_candidate_count=63, broker_working=3, qtsw2_working=3; scans do not taper in window. |"
    )
    lines.append(
        "| MNQ | **No** | `UNOWNED_LIVE_ORDER_DETECTED` for QTSW2 STOP (restart scan); no in-window `ADOPTION_SUCCESS` for that broker_order_id in parsed set. |"
    )
    lines.append("")

    # Section 6 Root cause
    lines.append("## Section 6 — Root Cause")
    lines.append("")
    lines.append("### A. Root cause category (choose one or more)")
    lines.append("")
    lines.append("1. **Intent reconstruction failure** — **Supported:** UNOWNED note states *\"QTSW2 protective with no matching active intent\"* for MNQ STOP after restart scan.")
    lines.append("2. **Multi-instance IEA conflict** — **Plausible / partial:** Same MNQ `broker_order_id` logged under **iea_instance_id 2 and 3** within ~2s — indicates **multiple logging/engine instances** on MNQ; cannot prove conflicting IEA registries vs duplicate reporting without code state.")
    lines.append("3. **Journal lifecycle corruption** — **Not proven** in-window; large `journal_file_count` (816) with high `adoption_candidate_count` (63) suggests **journal directory pressure / many historical intents**, not necessarily corruption.")
    lines.append("4. **Adoption logic failure** — **Partial:** Adoption scans run repeatedly; **no** observed transition in-window from UNOWNED → ADOPTION_SUCCESS for the flagged MNQ order in extracted broker timeline (may occur later outside window).")
    lines.append("5. **Logging-only issue** — **Proven** for **structure** (`event`=instrument, real name dropped) — **does not explain** UNOWNED behavior; it **obscures** audit.")
    lines.append("")
    lines.append("### B. Evidence (log patterns)")
    lines.append("")
    lines.append("- **ENGINE `ADOPTION_SCAN_START` (normalized):** `data.adoption_candidate_count=63`, `broker_working_count=3`, `qtsw2_working_count=3`, `journal_file_count=816`, `iea_instance_id=2`, timestamps **00:45:34–00:45:43** UTC (and similar bursts across window).")
    lines.append("- **MNQ `UNOWNED_LIVE_ORDER_DETECTED`:** `broker_order_id=408453625017`, `intent_id=cef6b767b7a307c1`, `order_type=STOP`, `action=RECOVERY_PHASE3`, **00:41:15** (`iea_instance_id=2`) and **00:41:17** (`iea_instance_id=3`).")
    lines.append("- **`RobotLogger.ConvertToRobotLogEvent`:** omits `state` from output — `ADOPTION_SCAN_START` not visible as `event` without heuristic (code ref: `RobotCore_For_NinjaTrader/RobotLogger.cs` loop excluding `state`).")
    lines.append("")
    lines.append("### C. Failure mode (step-by-step)")
    lines.append("")
    lines.append("1. **Restart / strategy start:** In-memory IEA/registry empty; journals on disk list many candidate intent ids (63).")
    lines.append("2. **IEA expects:** Scan broker orders, match QTSW2 tags to active intents / journal, adopt or classify UNOWNED with recovery.")
    lines.append("3. **Actually happens:** Broker shows **3** working QTSW2 orders; **63** adoption candidates from journal; MNQ protective tied to intent id in tag but **no matching active intent** in robot → **UNOWNED_LIVE_ORDER_DETECTED** + recovery path.")
    lines.append("4. **Divergence:** **Intent lifecycle in RAM** (post-restart) **≠** journal + broker (stale intent id on order or stream not rebuilt for that intent). **Secondary:** duplicate MNQ logging IDs complicate attribution.")
    lines.append("")

    # Section 7
    lines.append("## Section 7 — Recommended Fix Direction (no code)")
    lines.append("")
    lines.append("- **Logging:** Fix `RobotEvents.EngineBase` call pattern for IEA (or add `semantic_event` field) so `ADOPTION_*` and `BOOTSTRAP_*` are queryable; preserve `state` or map to top-level.")
    lines.append("- **Operations:** After restart, verify **one** engine instance per execution instrument or document intentional multi-chart behavior; reconcile **intent id** on working protectives vs **active** intent set.")
    lines.append("- **Journal:** Archive/trim old journal files if 816 files inflate candidate scans (performance + clarity) — policy decision, not proven corruption.")
    lines.append("")

    # Appendix: unowned table
    lines.append("## Appendix A — Unowned order grouping")
    lines.append("")
    by_oid: dict[str, list] = defaultdict(list)
    for r in unowned:
        by_oid[r["broker_order_id"]].append(r)
    lines.append("| broker_order_id | instrument | first_seen | last_seen | detection_count | intent_linked | resolution_status |")
    lines.append("|-----------------|------------|------------|-----------|-----------------|---------------|-------------------|")
    for oid, lst in sorted(by_oid.items()):
        lst.sort(key=lambda x: x["timestamp"] or datetime.min.replace(tzinfo=timezone.utc))
        first = lst[0]["timestamp"]
        last = lst[-1]["timestamp"]
        intent_ids = {x["intent_id"] for x in lst if x["intent_id"]}
        linked = "yes" if intent_ids else "no"
        # resolution: adopted in window?
        adopted = any(
            x.get("broker_order_id") == oid
            and (
                x["semantic_event"] in ("ADOPTION_SUCCESS", "ORDER_REGISTRY_ADOPTED")
                or x["raw_event"] in ("ADOPTION_SUCCESS", "ORDER_REGISTRY_ADOPTED")
            )
            for x in records
        )
        res = "RESOLVED_IN_WINDOW" if adopted else "PERSISTENT_OR_UNKNOWN"
        lines.append(
            f"| {oid} | {lst[0]['instrument']} | {first.strftime('%H:%M:%S') if first else ''} | {last.strftime('%H:%M:%S') if last else ''} | {len(lst)} | {linked} | {res} |"
        )
    lines.append("")

    lines.append("## Appendix B — Journal pressure")
    lines.append("")
    lines.append("| instrument | journal_file_count | adoption_candidate_count (typical in-window) |")
    lines.append("|------------|-------------------|-----------------------------------------------|")
    lines.append("| MYM | 816 | 63 |")
    lines.append("")
    lines.append("**Flag:** High **adoption_candidate_count** vs **3** active QTSW2 working orders — scan workload driven by **historical journal universe**, not current working set size.")
    lines.append("")

    out.write_text("\n".join(lines), encoding="utf-8")
    print(f"Wrote {out} rows={len(records)}")


if __name__ == "__main__":
    main()
