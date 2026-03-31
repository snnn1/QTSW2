"""
Convergence failure explanation layer for daily_audit.

Grounds narratives in:
- StateConsistencyReleaseEvaluator (RobotCore StateConsistencyGateModels.cs)
- Gate payloads from MismatchEscalationCoordinator.ToGatePayloadWithResultTelemetry
- Reconciliation episode boundaries from analyze_progress_reconciliation
"""
from __future__ import annotations

from collections import Counter, defaultdict
from datetime import datetime
from typing import Any, Dict, List, Optional, Sequence, Tuple

try:
    from zoneinfo import ZoneInfo
except Exception:  # pragma: no cover
    ZoneInfo = None  # type: ignore

from log_audit import NormEvent

from daily_audit_iea_integrity import analyze_progress_reconciliation

CHICAGO = "America/Chicago"

# Mirrors StateConsistencyReleaseEvaluator.Evaluate — release requires all of:
CONVERGENCE_RULES_REFERENCE = {
    "source": "StateConsistencyReleaseEvaluator.Evaluate (StateConsistencyGateModels.cs)",
    "release_ready_requires": [
        "BrokerPositionQty == JournalOpenQty (no unexplained position qty)",
        "BrokerWorkingCount explained by IEA: max(0, broker_working - iea_owned_plus_adopted) == 0 when IEA available",
        "PendingAdoptionCandidateCount == 0",
        "Local state coherent (IEA not unavailable when UseInstrumentExecutionAuthority)",
        "SnapshotSufficient == true",
    ],
    "notes": [
        "Logged gate events expose broker_qty/local_qty, broker_working_count, iea_owned_count, "
        "readiness_contradictions, release_ready, reconciliation_outcome.*After fields.",
        "pending_adoption_count is often null on payload; pending adoption is inferred from readiness_contradictions.",
    ],
}


def _data(ev: NormEvent) -> Dict[str, Any]:
    d = ev.data if isinstance(ev.data, dict) else {}
    return d


def _instrument(ev: NormEvent) -> str:
    d = _data(ev)
    inst = (ev.instrument or "").strip()
    if inst:
        return inst
    v = d.get("instrument")
    if v is not None and str(v).strip():
        return str(v).strip()
    return ""


def _int(v: Any) -> Optional[int]:
    if v is None:
        return None
    try:
        return int(str(v).strip())
    except Exception:
        return None


def _as_bool(v: Any) -> Optional[bool]:
    if v is True or v is False:
        return bool(v)
    if isinstance(v, str):
        lo = v.strip().lower()
        if lo in ("true", "1", "yes"):
            return True
        if lo in ("false", "0", "no"):
            return False
    return None


def _truthy(d: Dict[str, Any], *keys: str) -> bool:
    for k in keys:
        v = d.get(k)
        if v is True:
            return True
        if isinstance(v, str) and v.lower() in ("true", "1", "yes"):
            return True
    return False


def _parse_ts(s: str) -> Optional[datetime]:
    if not s:
        return None
    try:
        if s.endswith("Z"):
            return datetime.fromisoformat(s.replace("Z", "+00:00"))
        return datetime.fromisoformat(s)
    except Exception:
        return None


def _ro(d: Dict[str, Any]) -> Dict[str, Any]:
    ro = d.get("reconciliation_outcome")
    return ro if isinstance(ro, dict) else {}


def _normalize_gate_snapshot(ev: NormEvent) -> Dict[str, Any]:
    d = _data(ev)
    ro = _ro(d)
    return {
        "ts_utc": ev.ts_utc.isoformat().replace("+00:00", "Z"),
        "gate_state": str(d.get("gate_state") or ""),
        "mismatch_type": str(d.get("mismatch_type") or ""),
        "broker_position_qty": _int(d.get("broker_position_qty")),
        "broker_qty": _int(d.get("broker_qty")),
        "journal_open_qty": _int(d.get("local_qty")),
        "broker_working_count": _int(d.get("broker_working_count")),
        "iea_owned_plus_adopted_working": _int(d.get("iea_owned_count")),
        "pending_adoption_candidate_count": _int(d.get("pending_adoption_count")),
        "unexplained_working_count": _int(d.get("unexplained_working_count")),
        "unexplained_position_qty": _int(d.get("unexplained_position_qty")),
        "release_ready": d.get("release_ready"),
        "readiness_summary": str(d.get("readiness_summary") or ""),
        "readiness_contradictions": list(d.get("readiness_contradictions") or [])
        if isinstance(d.get("readiness_contradictions"), list)
        else [],
        "reconciliation_outcome_status": str(ro.get("OutcomeStatus") or ro.get("outcomeStatus") or ""),
        "reconciliation_reason": str(ro.get("Reason") or ro.get("reason") or ""),
        "adoption_candidate_after": _int(ro.get("AdoptionCandidateCountAfter") or ro.get("adoption_candidate_count_after")),
        "broker_working_after": _int(ro.get("BrokerWorkingCountAfter") or ro.get("broker_working_count_after")),
        "release_ready_after": ro.get("ReleaseReadyAfter") if "ReleaseReadyAfter" in ro else ro.get("release_ready_after"),
        "expensive_invoked": d.get("expensive_invoked"),
        "throttle_suppressed_expensive": d.get("throttle_suppressed_expensive"),
        "gate_reconciliation_throttled": d.get("gate_reconciliation_throttled"),
        "skip_reason": str(d.get("skip_reason") or ""),
    }


def _gate_events_in_window(
    evs: Sequence[NormEvent], inst: str, t0: datetime, t1: datetime
) -> List[NormEvent]:
    out: List[NormEvent] = []
    for e in evs:
        if e.event != "STATE_CONSISTENCY_GATE_RECONCILIATION_RESULT":
            continue
        if _instrument(e) != inst:
            continue
        if e.ts_utc < t0 or e.ts_utc > t1:
            continue
        out.append(e)
    out.sort(key=lambda x: x.ts_utc)
    return out


def _expected_vs_actual_table(snap_first: Optional[Dict[str, Any]], snap_last: Optional[Dict[str, Any]]) -> Dict[str, Any]:
    """Human-readable expected convergence vs last observed (audit-facing)."""
    def _bq(s: Optional[Dict[str, Any]]) -> Optional[int]:
        if not s:
            return None
        a = s.get("broker_qty")
        b = s.get("broker_position_qty")
        if a is not None:
            return int(a)
        if b is not None:
            return int(b)
        return None

    jq = snap_last.get("journal_open_qty") if snap_last else None
    bw = snap_last.get("broker_working_count") if snap_last else None
    iw = snap_last.get("iea_owned_plus_adopted_working") if snap_last else None
    bq = _bq(snap_last)
    pac = snap_last.get("pending_adoption_candidate_count") if snap_last else None
    uwc = snap_last.get("unexplained_working_count") if snap_last else None

    rows: List[Dict[str, Any]] = [
        {
            "field": "broker_position_qty",
            "expected_for_release": "must equal journal_open_qty",
            "last_observed": bq,
        },
        {
            "field": "journal_open_qty",
            "expected_for_release": "must equal broker_position_qty",
            "last_observed": jq,
        },
        {
            "field": "broker_working_count",
            "expected_for_release": "must be fully explained by iea_owned_plus_adopted_working (unexplained_working == 0)",
            "last_observed": bw,
        },
        {
            "field": "iea_owned_plus_adopted_working",
            "expected_for_release": "logged as iea_owned_count on gate payload; should cover broker_working",
            "last_observed": iw,
        },
        {
            "field": "pending_adoption_candidate_count",
            "expected_for_release": 0,
            "last_observed": pac,
            "observability_note": "often null on payload; use readiness_contradictions for pending_adoption_candidates",
        },
        {
            "field": "unexplained_working_count",
            "expected_for_release": 0,
            "last_observed": uwc,
        },
        {
            "field": "release_ready",
            "expected_for_release": True,
            "last_observed": snap_last.get("release_ready") if snap_last else None,
        },
        {
            "field": "gate_phase",
            "expected_for_release": "StablePendingRelease then timed release, or None after release",
            "last_observed": snap_last.get("gate_state") if snap_last else None,
        },
    ]
    delta_note = None
    if bq is not None and jq is not None and bq != jq:
        delta_note = f"position_qty_delta_{abs(bq - jq)} (per evaluator contradictions)"
    return {"rows": rows, "position_journal_delta_note": delta_note}


def _classify_blocker(snap_last: Optional[Dict[str, Any]], episode: Dict[str, Any]) -> Tuple[str, List[str], str]:
    """
    Return (primary_key, secondary_keys, one_line_mechanism).
    """
    secondaries: List[str] = []
    if snap_last is None:
        return (
            "insufficient_gate_telemetry",
            [],
            "No STATE_CONSISTENCY_GATE_RECONCILIATION_RESULT events in episode window for this instrument.",
        )

    contr = [str(c).lower() for c in (snap_last.get("readiness_contradictions") or [])]
    rs = str(snap_last.get("readiness_summary") or "").lower()
    bq = snap_last.get("broker_qty")
    if bq is None:
        bq = snap_last.get("broker_position_qty")
    jq = snap_last.get("journal_open_qty")
    uwc = snap_last.get("unexplained_working_count")
    ro_st = str(snap_last.get("reconciliation_outcome_status") or "").upper()
    throttled = snap_last.get("gate_reconciliation_throttled") is True
    suppressed = snap_last.get("throttle_suppressed_expensive") is True
    expensive = snap_last.get("expensive_invoked")
    if expensive is None:
        expensive = True
    rr_raw = snap_last.get("release_ready")
    rr = _as_bool(rr_raw)

    primary = "unclassified"
    # Position / working explainability before adoption-pending (payload can list both; qty mismatch is often root)
    if any("position_qty_delta" in str(c) for c in contr):
        primary = "broker_journal_qty_mismatch"
    elif uwc is not None and int(uwc) > 0:
        primary = "unexplained_broker_working_orders"
    elif bq is not None and jq is not None and int(bq) == 0 and int(jq) > 0:
        primary = "broker_flat_journal_still_open"
    elif bq is not None and jq is not None and int(bq) != int(jq):
        primary = "broker_journal_qty_mismatch"
    elif any("pending_adoption" in c for c in contr) or "pending_adoption" in rs:
        primary = "pending_adoption_never_cleared"
    elif any("iea_unavailable" in c for c in contr):
        primary = "iea_unavailable_blocked_release"
    elif any("no_snapshot" in c for c in contr) or "snapshot" in rs:
        primary = "snapshot_insufficient"
    elif throttled or suppressed or expensive is False:
        primary = "expensive_reconciliation_suppressed_or_throttled"
    elif ro_st in ("FAILED", "PARTIAL"):
        primary = "gate_reconciliation_runner_did_not_succeed"
    elif rr is False:
        primary = "release_ready_false_other_contradictions"
    elif rr is True:
        primary = "release_ready_true_but_no_gate_release_event_in_episode"

    if primary == "unclassified" and int(episode.get("measurable_progress_events") or 0) == 0:
        primary = "no_measurable_progress_despite_cycles"

    if int(episode.get("reconciliation_cycles") or 0) >= 10 and primary not in (
        "insufficient_gate_telemetry",
        "unclassified",
    ):
        pass
    elif primary == "release_ready_false_other_contradictions":
        secondaries.extend([c for c in contr if c])

    mechanism = {
        "pending_adoption_never_cleared": (
            "Pending adoption candidates remained a release-blocking contradiction "
            "(readiness_contradictions includes pending_adoption_candidates)."
        ),
        "unexplained_broker_working_orders": (
            "Broker working orders exceeded IEA-owned/adopted explanation "
            "(unexplained_working_count > 0 per gate readiness evaluation)."
        ),
        "broker_flat_journal_still_open": (
            "Broker position quantity was zero while journal_open_qty (local_qty) stayed positive — classic flat-broker / open-journal split."
        ),
        "broker_journal_qty_mismatch": (
            "Broker position quantity and journal open quantity diverged (|broker_qty - journal_open_qty| > 0)."
        ),
        "iea_unavailable_blocked_release": (
            "Evaluator marked IEA unavailable while authority expected it (iea_unavailable contradiction)."
        ),
        "snapshot_insufficient": (
            "Release readiness could not be evaluated with a sufficient snapshot (NO_SNAPSHOT / snapshot_insufficient)."
        ),
        "expensive_reconciliation_suppressed_or_throttled": (
            "Gate cycle skipped or throttled expensive reconciliation (throttle_suppressed_expensive / gate_reconciliation_throttled / expensive_invoked false)."
        ),
        "gate_reconciliation_runner_did_not_succeed": (
            f"Reconciliation runner finished with OutcomeStatus={ro_st} "
            f"(Reason={snap_last.get('reconciliation_reason') or 'n/a'})."
        ),
        "release_ready_false_other_contradictions": (
            f"release_ready stayed false with readiness_summary={snap_last.get('readiness_summary')!r}."
        ),
        "no_measurable_progress_despite_cycles": (
            "Episode ended with zero measurable progress events despite gate reconciliation cycles — inspect gate payloads and contradictions."
        ),
        "release_ready_true_but_no_gate_release_event_in_episode": (
            "Last gate snapshot shows release_ready=true (invariants explainable) but the episode never recorded "
            "STATE_CONSISTENCY_GATE_RELEASED / RECONCILIATION_MISMATCH_CLEARED before the audit closed the span — "
            "typically stability window not satisfied or episode segmentation excludes the release event."
        ),
        "unclassified": ("Could not map last gate snapshot to a single dominant blocker family."),
        "insufficient_gate_telemetry": ("No gate reconciliation results in window — cannot derive readiness deltas."),
    }.get(primary, ("Unknown blocker.",))

    if isinstance(mechanism, tuple):
        mechanism = mechanism[0]

    return primary, secondaries, str(mechanism)


def _why_failed_sentence(
    primary: str,
    snap_last: Optional[Dict[str, Any]],
    episode: Dict[str, Any],
    actions: List[Dict[str, Any]],
) -> str:
    """Single-sentence root cause for the report."""
    cycles = int(episode.get("reconciliation_cycles") or 0)
    prog = int(episode.get("measurable_progress_events") or 0)
    a_gate = sum(1 for a in actions if a.get("action_key") == "gate_reconciliation_started")
    a_audit = sum(1 for a in actions if a.get("action_key") == "reconciliation_attempt_audit")

    if primary == "insufficient_gate_telemetry":
        return (
            "Convergence failed because the audit window for this episode contained no gate reconciliation result "
            "telemetry to compare expected vs actual release inputs."
        )
    if primary == "pending_adoption_never_cleared":
        return (
            "Convergence failed because pending adoption candidates remained a release-blocking contradiction across "
            f"{cycles} gate cycles (measurable progress events={prog}), so ReleaseReady could not latch true."
        )
    if primary == "unexplained_broker_working_orders":
        uwc = snap_last.get("unexplained_working_count") if snap_last else None
        return (
            "Convergence failed because unexplained broker working orders remained "
            f"(unexplained_working_count={uwc}) after repeated gate reconciliations, violating broker_working explainability."
        )
    if primary == "broker_flat_journal_still_open":
        return (
            "Convergence failed because the broker was flat while the execution journal still showed open quantity, "
            "so position explainability never recovered despite reconciliation attempts."
        )
    if primary == "broker_journal_qty_mismatch":
        bq = snap_last.get("broker_qty") if snap_last else None
        jq = snap_last.get("journal_open_qty") if snap_last else None
        return (
            "Convergence failed because broker position quantity and journal open quantity stayed mismatched "
            f"(last broker_qty={bq}, journal_open_qty={jq}), preventing IsExplainable from becoming true."
        )
    if primary == "expensive_reconciliation_suppressed_or_throttled":
        return (
            "Convergence failed because expensive gate reconciliation was repeatedly suppressed or throttled "
            f"(gate_attempt_signals: gate_started={a_gate}, attempt_audit={a_audit}), limiting repair actions that could clear deltas."
        )
    if primary == "release_ready_true_but_no_gate_release_event_in_episode":
        return (
            "Convergence failed to complete only in an episode bookkeeping sense: release_ready was true on the final "
            f"gate result after {cycles} cycles, but no gate-release event fell inside this episode window — "
            "check stable-window timing and episode boundaries vs STATE_CONSISTENCY_GATE_RELEASED."
        )
    if primary == "gate_reconciliation_runner_did_not_succeed":
        ro = str(snap_last.get("reconciliation_outcome_status") if snap_last else "")
        rsn = str(snap_last.get("reconciliation_reason") if snap_last else "")
        return (
            f"Convergence failed because the gate reconciliation runner reported OutcomeStatus={ro} "
            f"with reason {rsn!r}, leaving release readiness unsatisfied after {cycles} gate cycles."
        )
    return (
        f"Convergence failed because {primary.replace('_', ' ')} persisted through {cycles} reconciliation cycles "
        f"(measurable progress events={prog})."
    )


def _evidence_confidence(snap_last: Optional[Dict[str, Any]], has_actions: bool) -> Tuple[str, List[str]]:
    basis: List[str] = []
    if snap_last and snap_last.get("readiness_contradictions"):
        basis.append("direct readiness_contradictions on last gate result")
    if snap_last and snap_last.get("release_ready") is not None:
        basis.append("release_ready flag on gate result")
    if snap_last and (snap_last.get("reconciliation_outcome_status") or snap_last.get("reconciliation_reason")):
        basis.append("reconciliation_outcome status/reason on last gate result")
    if snap_last and (snap_last.get("broker_qty") is not None or snap_last.get("journal_open_qty") is not None):
        basis.append("broker_qty / local_qty diagnostic mirrors on gate payload")
    if has_actions:
        basis.append("repair action event sequence in episode window")

    if not snap_last:
        return "LOW", ["missing gate results in episode window"]
    if len(basis) >= 3:
        return "HIGH", basis
    if len(basis) >= 1:
        return "MEDIUM", basis
    return "LOW", basis or ["sparse gate payload fields"]


def _collect_actions(
    evs: Sequence[NormEvent], inst: str, t0: datetime, t1: datetime
) -> Tuple[List[Dict[str, Any]], Dict[str, int]]:
    """Aggregate repair / reconciliation attempts with first/last ts."""
    buckets: Dict[str, List[datetime]] = defaultdict(list)
    detail_seen: Dict[str, set] = defaultdict(set)

    def _add(key: str, ts: datetime, detail: str = "") -> None:
        buckets[key].append(ts)
        if detail:
            detail_seen[key].add(detail[:200])

    for e in evs:
        if e.ts_utc < t0 or e.ts_utc > t1:
            continue
        ei = _instrument(e)
        if not ei or ei != inst:
            continue
        d = _data(e)
        evn = e.event or ""

        if evn == "STATE_CONSISTENCY_GATE_RECONCILIATION_STARTED":
            _add("gate_reconciliation_started", e.ts_utc)
        elif evn == "RECONCILIATION_ATTEMPT_AUDIT":
            aa = str(d.get("action_attempted") or "")
            succ = d.get("success")
            _add(
                "reconciliation_attempt_audit",
                e.ts_utc,
                f"{aa} success={succ}",
            )
        elif evn in ("IEA_ADOPTION_SCAN_EXECUTION_STARTED", "ADOPTION_SCAN_START", "IEA_ADOPTION_SCAN_SCHEDULED"):
            _add("adoption_scan", e.ts_utc, evn)
        elif evn == "ORDER_REGISTRY_RECOVERY_DEFERRED_FAIL_CLOSED":
            _add("order_registry_recovery_deferred_fail_closed", e.ts_utc)
        elif evn == "STATE_CONSISTENCY_GATE_RESTABILIZATION_RESET":
            _add("gate_stable_reset_release_blocked", e.ts_utc)
        elif evn == "REGISTRY_BROKER_DIVERGENCE":
            _add("registry_broker_divergence", e.ts_utc)
        elif evn == "VERIFY_REGISTRY_INTEGRITY" or evn.startswith("ORDER_REGISTRY_INTEGRITY"):
            _add("registry_integrity_sweep_signal", e.ts_utc, evn)
        elif evn == "RECONCILIATION_PROGRESS_OBSERVED":
            kind = str(d.get("kind") or "")
            if kind.lower() != "baseline_signature":
                _add("explicit_progress_observed", e.ts_utc, kind)
        elif evn in ("RECONCILIATION_MISMATCH_CLEARED", "STATE_CONSISTENCY_GATE_RELEASED", "RECONCILIATION_CONVERGED"):
            _add("terminal_release_or_clear", e.ts_utc, evn)

    actions: List[Dict[str, Any]] = []
    counts: Dict[str, int] = {}
    for key, ts_list in sorted(buckets.items(), key=lambda x: x[0]):
        if not ts_list:
            continue
        ts_list.sort()
        counts[key] = len(ts_list)
        # Did this action coincide with measurable progress? proxy: any progress event after first action ts
        progressed = False
        if key in ("explicit_progress_observed", "terminal_release_or_clear"):
            progressed = True
        else:
            prog_ts = buckets.get("explicit_progress_observed", [])
            if prog_ts and ts_list[0] <= max(prog_ts):
                progressed = True
        actions.append(
            {
                "action_key": key,
                "count": len(ts_list),
                "first_ts_utc": ts_list[0].isoformat().replace("+00:00", "Z"),
                "last_ts_utc": ts_list[-1].isoformat().replace("+00:00", "Z"),
                "measurable_progress_proxy": progressed,
                "detail_samples": sorted(detail_seen.get(key, set()))[:5],
            }
        )
    return actions, counts


def _match_chain_id(
    chains: Sequence[Dict[str, Any]], inst: str, t0: datetime, t1: datetime
) -> Tuple[Optional[int], Optional[str]]:
    for c in chains:
        try:
            cs = _parse_ts(str(c.get("start_utc") or ""))
            ce_raw = c.get("end_utc")
            ce = _parse_ts(str(ce_raw)) if ce_raw else None
        except Exception:
            continue
        if cs is None:
            continue
        # overlap
        end_cmp = ce if ce is not None else t1
        if end_cmp < t0 or cs > t1:
            continue
        trig_i = str(c.get("trigger_instrument") or "")
        evs = c.get("events") or []
        inst_hit = trig_i.upper() == inst.upper()
        if not inst_hit and isinstance(evs, list):
            for ev in evs:
                if str((ev or {}).get("instrument") or "").upper() == inst.upper():
                    inst_hit = True
                    break
        if inst_hit:
            return int(c.get("id") or 0) or None, str(c.get("classification") or "")
    return None, None


def analyze_convergence_failure_explanations(
    events: Sequence[NormEvent],
    incident_chains: Sequence[Dict[str, Any]],
    thresholds: Dict[str, Any],
) -> Dict[str, Any]:
    min_cycles = int(thresholds.get("convergence_significant_min_cycles", 8))
    max_export = int(thresholds.get("convergence_failure_max_episodes_export", 25))
    pr_full = analyze_progress_reconciliation(list(events), thresholds)
    raw_eps: List[Dict[str, Any]] = list(pr_full.get("mismatch_episodes") or [])

    evs = sorted(events, key=lambda e: e.ts_utc)

    significant: List[Dict[str, Any]] = []
    for ep in raw_eps:
        inst = str(ep.get("instrument") or "")
        if not inst or inst == "_global_":
            continue
        resolved = bool(ep.get("resolved"))
        cyc = int(ep.get("reconciliation_cycles") or 0)
        if resolved:
            continue
        if cyc < min_cycles:
            continue
        significant.append(ep)

    # If everything resolved, surface top episodes by cycle count (degraded / informational only)
    informational_only = False
    if not significant and raw_eps:
        informational_only = True
        significant = sorted(
            [e for e in raw_eps if str(e.get("instrument") or "") not in ("", "_global_")],
            key=lambda x: int(x.get("reconciliation_cycles") or 0),
            reverse=True,
        )[:3]

    blocker_episode_cycles: Counter = Counter()
    blocker_episode_duration: Counter = Counter()
    blocker_wasted_cycles: Counter = Counter()

    explanations: List[Dict[str, Any]] = []

    for idx, ep in enumerate(significant[:max_export], 1):
        inst = str(ep.get("instrument") or "")
        t0s = str(ep.get("started_ts_utc") or "")
        t1s = str(ep.get("ended_ts_utc") or "")
        t0 = _parse_ts(t0s)
        t1 = _parse_ts(t1s)
        if t0 is None or t1 is None:
            continue
        duration_ms = (t1 - t0).total_seconds() * 1000.0
        g_events = _gate_events_in_window(evs, inst, t0, t1)
        snaps = [_normalize_gate_snapshot(ge) for ge in g_events]
        snap_first = snaps[0] if snaps else None
        snap_last = snaps[-1] if snaps else None

        primary, secondaries, mechanism = _classify_blocker(snap_last, ep)
        actions, _ = _collect_actions(evs, inst, t0, t1)

        blocker_episode_cycles[primary] += 1
        blocker_episode_duration[primary] += int(max(duration_ms, 0))
        blocker_wasted_cycles[primary] += max(0, int(ep.get("reconciliation_cycles") or 0) - int(ep.get("measurable_progress_events") or 0))

        chain_id, chain_cls = _match_chain_id(incident_chains, inst, t0, t1)
        why = _why_failed_sentence(primary, snap_last, ep, actions)
        if informational_only:
            why = (
                "Episode ultimately recorded as resolved in the audit window; stall pattern before clearance — " + why
            )
        conf, ev_basis = _evidence_confidence(snap_last, bool(actions))
        if informational_only:
            conf = "MEDIUM" if conf == "HIGH" else conf

        classification = "FAILED" if not ep.get("resolved") else "DEGRADED"
        explanations.append(
            {
                "episode_index": idx,
                "incident_chain_id": chain_id,
                "incident_chain_classification": chain_cls,
                "instrument": inst,
                "classification": classification,
                "start_utc": t0s,
                "end_utc": t1s,
                "duration_ms": round(duration_ms, 2),
                "total_reconciliation_cycles": int(ep.get("reconciliation_cycles") or 0),
                "progress_events": int(ep.get("measurable_progress_events") or 0),
                "expected_convergence": CONVERGENCE_RULES_REFERENCE,
                "expected_vs_actual": _expected_vs_actual_table(snap_first, snap_last),
                "dominant_unresolved_blocker": {
                    "primary": primary,
                    "secondaries": secondaries,
                    "mechanism": mechanism,
                },
                "resolution_attempts": actions,
                "why_convergence_failed": why,
                "confidence": conf,
                "evidence_basis": ev_basis,
                "telemetry_gaps": []
                if conf == "HIGH"
                else (
                    ["sparse readiness fields on gate payloads"]
                    if conf == "MEDIUM"
                    else ["missing or partial gate reconciliation results in episode window"]
                ),
                "gate_snapshots_first_last": {"first": snap_first, "last": snap_last},
            }
        )

    def _top(counter: Counter, k: int = 3) -> List[Dict[str, Any]]:
        return [{"blocker": a, "value": int(b)} for a, b in counter.most_common(k)]

    summary = {
        "episodes_analyzed": len(explanations),
        "informational_fallback": informational_only,
        "total_raw_mismatch_episodes": len(raw_eps),
        "significance_filters": {
            "convergence_significant_min_cycles": min_cycles,
            "note": "Unresolved mismatch_episodes only, with cycles >= min_cycles; if none, top 3 by cycles (may be resolved) as samples.",
        },
        "top_blockers_by_episode_count": _top(blocker_episode_cycles),
        "top_blockers_by_wasted_cycles": _top(blocker_wasted_cycles),
        "top_blockers_by_total_duration_ms": _top(blocker_episode_duration),
    }

    return {
        "schema": "convergence_failure_explanation_v1",
        "summary": summary,
        "episodes": explanations,
    }


def render_convergence_markdown(block: Dict[str, Any], audit_tz: str) -> List[str]:
    lines: List[str] = []
    lines.append("[CONVERGENCE FAILURE EXPLANATION]")
    if not block:
        lines.append("(unavailable)")
        return lines
    summ = block.get("summary") or {}
    lines.append("Convergence failure summary")
    lines.append(f"- episodes in section: {summ.get('episodes_analyzed', 0)}")
    lines.append(f"- raw mismatch episodes (day): {summ.get('total_raw_mismatch_episodes', '—')}")
    tb = summ.get("top_blockers_by_episode_count") or []
    tw = summ.get("top_blockers_by_wasted_cycles") or []
    td = summ.get("top_blockers_by_total_duration_ms") or []
    lines.append("- top blockers by episode count: " + ", ".join(f"{r.get('blocker')}={r.get('value')}" for r in tb) if tb else "- top blockers by episode count: —")
    lines.append("- top blockers by wasted cycles: " + ", ".join(f"{r.get('blocker')}={r.get('value')}" for r in tw) if tw else "- top blockers by wasted cycles: —")
    lines.append("- top blockers by total duration (ms): " + ", ".join(f"{r.get('blocker')}={r.get('value')}" for r in td) if td else "- top blockers by total duration (ms): —")
    lines.append("")
    ref = CONVERGENCE_RULES_REFERENCE
    lines.append(f"Expected convergence (code ref: {ref.get('source')}):")
    for rule in ref.get("release_ready_requires", [])[:8]:
        lines.append(f"  • {rule}")
    lines.append("")

    for ep in block.get("episodes") or []:
        label = f"Episode #{ep.get('episode_index')} — {ep.get('instrument')} — {ep.get('classification')}"
        if ep.get("incident_chain_id"):
            label += f" (chain #{ep.get('incident_chain_id')})"
        lines.append(label)
        lines.append(
            f"  Duration_ms: {ep.get('duration_ms')} | cycles: {ep.get('total_reconciliation_cycles')} | "
            f"progress_events: {ep.get('progress_events')} | confidence: {ep.get('confidence')}"
        )
        dom = ep.get("dominant_unresolved_blocker") or {}
        lines.append(f"  Primary blocker: {dom.get('primary')} — {dom.get('mechanism')}")
        if dom.get("secondaries"):
            lines.append(f"  Secondary signals: {dom.get('secondaries')}")
        eva = ep.get("expected_vs_actual") or {}
        if isinstance(eva, dict) and eva.get("rows"):
            lines.append("  Expected vs last observed (gate payload):")
            for row in eva["rows"][:7]:
                lines.append(
                    f"    - {row.get('field')}: expected {row.get('expected_for_release')} | observed {row.get('last_observed')}"
                )
            if eva.get("position_journal_delta_note"):
                lines.append(f"    - note: {eva.get('position_journal_delta_note')}")
        lines.append(f"  Why convergence failed: {ep.get('why_convergence_failed')}")
        lines.append(f"  Evidence: {', '.join(ep.get('evidence_basis') or [])}")
        if ep.get("telemetry_gaps"):
            lines.append(f"  Telemetry gaps: {', '.join(ep.get('telemetry_gaps') or [])}")
        acts = ep.get("resolution_attempts") or []
        if acts:
            lines.append("  Actions attempted (episode window):")
            for a in acts[:12]:
                lines.append(
                    f"    - {a.get('action_key')}: count={a.get('count')} "
                    f"{a.get('first_ts_utc')} → {a.get('last_ts_utc')} "
                    f"progress_proxy={a.get('measurable_progress_proxy')}"
                )
        lines.append("")
    return lines
