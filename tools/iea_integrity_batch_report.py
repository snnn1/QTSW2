#!/usr/bin/env python3
"""
Batch extractor for iea_integrity metrics from existing daily audit JSON files.

Does not modify daily_audit.py or audit schema. Reads reports/daily_audit/YYYY-MM-DD.json only,
unless --run-daily-audit is passed (optional subprocess).
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
from dataclasses import dataclass, field
from datetime import date, timedelta
from pathlib import Path
from typing import Any, Dict, List, Optional, Sequence, Tuple


def resolve_project_root() -> Path:
    return Path(__file__).resolve().parent.parent


def parse_date(s: str) -> date:
    return date.fromisoformat(s)


def date_range_inclusive(start: date, end: date) -> List[date]:
    out: List[date] = []
    d = start
    while d <= end:
        out.append(d)
        d += timedelta(days=1)
    return out


def safe_get(d: Any, *keys: str, default: Any = None) -> Any:
    cur = d
    for k in keys:
        if not isinstance(cur, dict):
            return default
        cur = cur.get(k)
    return cur if cur is not None else default


@dataclass
class DayRow:
    audit_date: str
    audit_mode: Optional[str]
    engine_full: Optional[bool]
    confidence: Optional[str]
    overall_status: Optional[str]
    trade_readiness_decision: Optional[str]
    iea_status: Optional[str]
    iea_resolution_suspect_count: Optional[int]
    registry_timing_gap_count: Optional[int]
    registry_timing_gap_avg_delay_ms: Optional[float]
    registry_timing_gap_max_delay_ms: Optional[float]
    lifecycle_wrong_adapter_count: Optional[int]
    reconciliation_avg_efficiency: Optional[float]
    cross_iea_duplicate_events: Optional[int]
    _raw_path: str = field(default="", repr=False)

    def as_dict(self) -> Dict[str, Any]:
        return {
            "audit_date": self.audit_date,
            "meta": {"audit_mode": self.audit_mode, "engine_full": self.engine_full},
            "confidence": self.confidence,
            "overall_status": self.overall_status,
            "trade_readiness": {"decision": self.trade_readiness_decision},
            "iea_integrity": {
                "status": self.iea_status,
                "metrics": {
                    "iea_resolution_suspect_count": self.iea_resolution_suspect_count,
                    "registry_timing_gap": {
                        "count": self.registry_timing_gap_count,
                        "avg_delay_ms": self.registry_timing_gap_avg_delay_ms,
                        "max_delay_ms": self.registry_timing_gap_max_delay_ms,
                    },
                    "lifecycle_wrong_adapter_count": self.lifecycle_wrong_adapter_count,
                    "reconciliation_efficiency": {
                        "avg_efficiency": self.reconciliation_avg_efficiency
                    },
                    "cross_iea_duplicate_events": self.cross_iea_duplicate_events,
                },
            },
        }


def load_audit_json(path: Path) -> Optional[Dict[str, Any]]:
    if not path.is_file():
        return None
    try:
        with path.open("r", encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return None


def row_from_json(obj: Dict[str, Any], path: Path) -> DayRow:
    meta = obj.get("meta") if isinstance(obj.get("meta"), dict) else {}
    tr = obj.get("trade_readiness") if isinstance(obj.get("trade_readiness"), dict) else {}
    iea = obj.get("iea_integrity") if isinstance(obj.get("iea_integrity"), dict) else {}
    metrics = iea.get("metrics") if isinstance(iea.get("metrics"), dict) else {}
    rtg = metrics.get("registry_timing_gap") if isinstance(metrics.get("registry_timing_gap"), dict) else {}
    reff = (
        metrics.get("reconciliation_efficiency")
        if isinstance(metrics.get("reconciliation_efficiency"), dict)
        else {}
    )

    def nint(x: Any) -> Optional[int]:
        if x is None:
            return None
        try:
            return int(x)
        except Exception:
            return None

    def nfloat(x: Any) -> Optional[float]:
        if x is None:
            return None
        try:
            return float(x)
        except Exception:
            return None

    ad = str(obj.get("audit_date") or path.stem)

    return DayRow(
        audit_date=ad,
        audit_mode=meta.get("audit_mode") if "audit_mode" in meta else None,
        engine_full=meta.get("engine_full") if "engine_full" in meta else None,
        confidence=obj.get("confidence"),
        overall_status=obj.get("overall_status"),
        trade_readiness_decision=tr.get("decision"),
        iea_status=iea.get("status"),
        iea_resolution_suspect_count=nint(metrics.get("iea_resolution_suspect_count")),
        registry_timing_gap_count=nint(rtg.get("count")),
        registry_timing_gap_avg_delay_ms=nfloat(rtg.get("avg_delay_ms")),
        registry_timing_gap_max_delay_ms=nfloat(rtg.get("max_delay_ms")),
        lifecycle_wrong_adapter_count=nint(metrics.get("lifecycle_wrong_adapter_count")),
        reconciliation_avg_efficiency=nfloat(reff.get("avg_efficiency")),
        cross_iea_duplicate_events=nint(metrics.get("cross_iea_duplicate_events")),
        _raw_path=str(path),
    )


def run_daily_audit_for_date(project_root: Path, audit_date: str) -> int:
    """Return subprocess exit code."""
    cmd = [sys.executable, str(project_root / "tools" / "daily_audit.py"), "--date", audit_date]
    return subprocess.call(cmd, cwd=str(project_root))


def collect_rows(
    reports_dir: Path,
    dates: Sequence[date],
    run_daily_audit: bool,
    project_root: Path,
) -> Tuple[List[DayRow], List[str]]:
    """Load rows; optionally run daily_audit for missing files."""
    rows: List[DayRow] = []
    warnings: List[str] = []
    for d in dates:
        p = reports_dir / f"{d.isoformat()}.json"
        if run_daily_audit and not p.is_file():
            rc = run_daily_audit_for_date(project_root, d.isoformat())
            if rc != 0:
                warnings.append(f"daily_audit failed for {d.isoformat()} (exit {rc})")
        obj = load_audit_json(p)
        if obj is None:
            rows.append(
                DayRow(
                    audit_date=d.isoformat(),
                    audit_mode=None,
                    engine_full=None,
                    confidence=None,
                    overall_status=None,
                    trade_readiness_decision=None,
                    iea_status=None,
                    iea_resolution_suspect_count=None,
                    registry_timing_gap_count=None,
                    registry_timing_gap_avg_delay_ms=None,
                    registry_timing_gap_max_delay_ms=None,
                    lifecycle_wrong_adapter_count=None,
                    reconciliation_avg_efficiency=None,
                    cross_iea_duplicate_events=None,
                    _raw_path=str(p),
                )
            )
            warnings.append(f"missing or unreadable JSON: {p}")
            continue
        rows.append(row_from_json(obj, p))
    return rows, warnings


def is_full_day(r: DayRow) -> bool:
    return r.engine_full is True


def executive_summary(full_rows: List[DayRow]) -> Dict[str, Any]:
    n_full = len(full_rows)
    populated = sum(1 for r in full_rows if r.iea_status is not None)
    critical_days = sum(1 for r in full_rows if (r.iea_status or "").upper() == "CRITICAL")
    total_res = sum(r.iea_resolution_suspect_count or 0 for r in full_rows)
    total_life = sum(r.lifecycle_wrong_adapter_count or 0 for r in full_rows)
    eff_vals = [r.reconciliation_avg_efficiency for r in full_rows if r.reconciliation_avg_efficiency is not None]
    avg_eff = round(sum(eff_vals) / len(eff_vals), 8) if eff_vals else None
    total_dup = sum(r.cross_iea_duplicate_events or 0 for r in full_rows)
    return {
        "full_days_in_range": n_full,
        "full_days_with_populated_iea_integrity": populated,
        "days_iea_integrity_status_critical": critical_days,
        "total_iea_resolution_suspect_count": total_res,
        "total_lifecycle_wrong_adapter_count": total_life,
        "average_reconciliation_avg_efficiency_full_days_non_null": avg_eff,
        "full_days_with_non_null_avg_efficiency": len(eff_vals),
        "total_cross_iea_duplicate_events": total_dup,
    }


def key_observations(full_rows: List[DayRow]) -> List[str]:
    """Factual bullets only."""
    bullets: List[str] = []

    n_full = len(full_rows)
    pop = sum(1 for r in full_rows if r.iea_status is not None)
    if n_full and pop < n_full:
        bullets.append(
            f"IEA integrity block was populated (non-null iea_integrity.status) on {pop} of {n_full} FULL days; "
            f"{n_full - pop} day(s) have null IEA metrics (missing layer in JSON or not generated)."
        )

    res_days = sum(1 for r in full_rows if (r.iea_resolution_suspect_count or 0) > 0)
    if res_days:
        bullets.append(
            f"Resolution suspect signals clustered on {res_days} day{'s' if res_days != 1 else ''}."
        )

    life_days = sum(1 for r in full_rows if (r.lifecycle_wrong_adapter_count or 0) > 0)
    if life_days:
        bullets.append(
            f"Lifecycle wrong-adapter signals appeared on {life_days} day{'s' if life_days != 1 else ''}."
        )

    near_zero_eff_days = sum(
        1
        for r in full_rows
        if r.reconciliation_avg_efficiency is not None
        and (r.reconciliation_avg_efficiency <= 0.05 or abs(float(r.reconciliation_avg_efficiency)) < 1e-12)
    )
    if near_zero_eff_days:
        bullets.append(
            f"Reconciliation efficiency was zero or near-zero (≤ 0.05, non-null) on "
            f"{near_zero_eff_days} day{'s' if near_zero_eff_days != 1 else ''}."
        )

    null_eff_days = sum(1 for r in full_rows if r.reconciliation_avg_efficiency is None)
    if null_eff_days:
        bullets.append(
            f"Reconciliation efficiency avg_efficiency was null on {null_eff_days} FULL day(s)."
        )

    dup_days = sum(1 for r in full_rows if (r.cross_iea_duplicate_events or 0) > 0)
    if dup_days:
        bullets.append(
            f"Cross-IEA duplication appeared on {dup_days} day{'s' if dup_days != 1 else ''}."
        )

    crit_days = [r.audit_date for r in full_rows if (r.iea_status or "").upper() == "CRITICAL"]
    if crit_days:
        bullets.append(
            f"iea_integrity.status was CRITICAL on {len(crit_days)} day(s): {', '.join(crit_days)}."
        )

    if not bullets:
        bullets.append("No non-zero metrics across FULL days in the selected window (or all counts were zero).")

    return bullets


def markdown_table(rows: List[DayRow], title: str) -> List[str]:
    lines: List[str] = []
    lines.append(f"### {title}")
    lines.append("")
    lines.append(
        "| date | mode | engine_full | confidence | overall | trade_readiness | iea_status | "
        "res_suspect | rtg_cnt | rtg_avg_ms | rtg_max_ms | life_wrong | avg_eff | cross_dup |"
    )
    lines.append(
        "|------|------|---------------|------------|---------|-----------------|------------|"
        "-------------|---------|------------|------------|------------|---------|-----------|"
    )
    for r in rows:
        lines.append(
            "| {date} | {mode} | {ef} | {conf} | {ov} | {tr} | {iea_st} | "
            "{rs} | {rtc} | {rta} | {rtm} | {lw} | {ae} | {cd} |".format(
                date=r.audit_date,
                mode=r.audit_mode if r.audit_mode is not None else "null",
                ef=r.engine_full,
                conf=r.confidence if r.confidence is not None else "null",
                ov=r.overall_status if r.overall_status is not None else "null",
                tr=r.trade_readiness_decision if r.trade_readiness_decision is not None else "null",
                iea_st=r.iea_status if r.iea_status is not None else "null",
                rs=r.iea_resolution_suspect_count
                if r.iea_resolution_suspect_count is not None
                else "null",
                rtc=r.registry_timing_gap_count if r.registry_timing_gap_count is not None else "null",
                rta=r.registry_timing_gap_avg_delay_ms
                if r.registry_timing_gap_avg_delay_ms is not None
                else "null",
                rtm=r.registry_timing_gap_max_delay_ms
                if r.registry_timing_gap_max_delay_ms is not None
                else "null",
                lw=r.lifecycle_wrong_adapter_count
                if r.lifecycle_wrong_adapter_count is not None
                else "null",
                ae=r.reconciliation_avg_efficiency
                if r.reconciliation_avg_efficiency is not None
                else "null",
                cd=r.cross_iea_duplicate_events
                if r.cross_iea_duplicate_events is not None
                else "null",
            )
        )
    lines.append("")
    return lines


def null_counts_full_table(full_rows: List[DayRow]) -> Dict[str, int]:
    """Count nulls per extracted column among FULL rows."""
    keys = [
        "confidence",
        "overall_status",
        "trade_readiness_decision",
        "iea_status",
        "iea_resolution_suspect_count",
        "registry_timing_gap_count",
        "registry_timing_gap_avg_delay_ms",
        "registry_timing_gap_max_delay_ms",
        "lifecycle_wrong_adapter_count",
        "reconciliation_avg_efficiency",
        "cross_iea_duplicate_events",
    ]
    counts = {k: 0 for k in keys}
    for r in full_rows:
        if r.confidence is None:
            counts["confidence"] += 1
        if r.overall_status is None:
            counts["overall_status"] += 1
        if r.trade_readiness_decision is None:
            counts["trade_readiness_decision"] += 1
        if r.iea_status is None:
            counts["iea_status"] += 1
        if r.iea_resolution_suspect_count is None:
            counts["iea_resolution_suspect_count"] += 1
        if r.registry_timing_gap_count is None:
            counts["registry_timing_gap_count"] += 1
        if r.registry_timing_gap_avg_delay_ms is None:
            counts["registry_timing_gap_avg_delay_ms"] += 1
        if r.registry_timing_gap_max_delay_ms is None:
            counts["registry_timing_gap_max_delay_ms"] += 1
        if r.lifecycle_wrong_adapter_count is None:
            counts["lifecycle_wrong_adapter_count"] += 1
        if r.reconciliation_avg_efficiency is None:
            counts["reconciliation_avg_efficiency"] += 1
        if r.cross_iea_duplicate_events is None:
            counts["cross_iea_duplicate_events"] += 1
    return counts


def render_markdown_report(
    exec_sum: Dict[str, Any],
    full_rows: List[DayRow],
    excluded_rows: List[DayRow],
    compare_rows: Optional[List[DayRow]],
    compare_label: str,
    warnings: List[str],
    null_counts: Dict[str, int],
) -> str:
    lines: List[str] = []
    lines.append("# IEA integrity batch report")
    lines.append("")
    lines.append("## Executive summary")
    lines.append("")
    for k, v in sorted(exec_sum.items()):
        lines.append(f"- **{k}**: {v}")
    lines.append("")
    lines.extend(markdown_table(full_rows, "FULL-day table (meta.engine_full == true)"))
    if excluded_rows:
        lines.append("## Excluded / non-full days")
        lines.append("")
        lines.extend(markdown_table(excluded_rows, "Non-FULL days in range"))
    if compare_rows is not None:
        lines.append("## Comparison table (selected dates)")
        lines.append("")
        lines.append(f"*{compare_label}*")
        lines.append("")
        lines.extend(markdown_table(compare_rows, "Compare dates"))
    lines.append("## Key observations")
    lines.append("")
    for b in key_observations(full_rows):
        lines.append(f"- {b}")
    lines.append("")
    lines.append("## Null-heavy columns (FULL-day table)")
    lines.append("")
    n = len(full_rows)
    if n == 0:
        lines.append("(no FULL days)")
    else:
        for col, cnt in sorted(null_counts.items(), key=lambda x: (-x[1], x[0])):
            lines.append(f"- `{col}`: {cnt} null / {n} days ({round(100.0 * cnt / n, 2)}%)")
    if warnings:
        lines.append("")
        lines.append("## Warnings")
        lines.append("")
        for w in warnings:
            lines.append(f"- {w}")
    lines.append("")
    return "\n".join(lines)


def build_json_output(
    exec_sum: Dict[str, Any],
    full_rows: List[DayRow],
    excluded_rows: List[DayRow],
    compare_rows: Optional[List[DayRow]],
    compare_dates: Optional[List[str]],
    warnings: List[str],
    date_range: Tuple[str, str],
    null_counts: Dict[str, int],
) -> Dict[str, Any]:
    def serialize_row(r: DayRow) -> Dict[str, Any]:
        return {
            "audit_date": r.audit_date,
            "meta": {"audit_mode": r.audit_mode, "engine_full": r.engine_full},
            "confidence": r.confidence,
            "overall_status": r.overall_status,
            "trade_readiness": {"decision": r.trade_readiness_decision},
            "iea_integrity": {
                "status": r.iea_status,
                "metrics": {
                    "iea_resolution_suspect_count": r.iea_resolution_suspect_count,
                    "registry_timing_gap": {
                        "count": r.registry_timing_gap_count,
                        "avg_delay_ms": r.registry_timing_gap_avg_delay_ms,
                        "max_delay_ms": r.registry_timing_gap_max_delay_ms,
                    },
                    "lifecycle_wrong_adapter_count": r.lifecycle_wrong_adapter_count,
                    "reconciliation_efficiency": {"avg_efficiency": r.reconciliation_avg_efficiency},
                    "cross_iea_duplicate_events": r.cross_iea_duplicate_events,
                },
            },
        }

    out: Dict[str, Any] = {
        "schema": "iea_integrity_batch_report_v1",
        "date_range": {"start": date_range[0], "end": date_range[1]},
        "executive_summary": exec_sum,
        "full_days": [serialize_row(r) for r in full_rows],
        "excluded_non_full_days": [serialize_row(r) for r in excluded_rows],
        "key_observations": key_observations(full_rows),
        "null_counts_full_day_columns": null_counts,
        "warnings": warnings,
    }
    if compare_rows is not None:
        out["compare_dates"] = compare_dates or []
        out["compare_table"] = [serialize_row(r) for r in compare_rows]
    return out


def main() -> int:
    project_root = resolve_project_root()
    p = argparse.ArgumentParser(description="IEA integrity batch report from daily audit JSON files")
    p.add_argument("--start-date", default="2026-03-13", help="YYYY-MM-DD")
    p.add_argument("--end-date", default="2026-03-24", help="YYYY-MM-DD")
    p.add_argument("--reports-dir", default=None, help="Override reports/daily_audit directory")
    p.add_argument(
        "--run-daily-audit",
        action="store_true",
        help="Run tools/daily_audit.py for any missing JSON in range (slow; default off)",
    )
    p.add_argument(
        "--compare-dates",
        nargs="+",
        default=None,
        metavar="YYYY-MM-DD",
        help="Optional second table: same columns for these dates only",
    )
    p.add_argument(
        "--markdown-out",
        default=None,
        help="Override markdown path (default: reports/daily_audit/IEA_INTEGRITY_BATCH_REPORT.md)",
    )
    p.add_argument(
        "--json-out",
        default=None,
        help="Override JSON path (default: reports/daily_audit/IEA_INTEGRITY_BATCH_REPORT.json)",
    )
    p.add_argument(
        "--markdown",
        action="store_true",
        help="Also print the markdown report to stdout",
    )
    args = p.parse_args()

    start = parse_date(args.start_date)
    end = parse_date(args.end_date)
    if end < start:
        print("end-date must be >= start-date", file=sys.stderr)
        return 2

    reports_dir = Path(args.reports_dir) if args.reports_dir else project_root / "reports" / "daily_audit"
    md_path = Path(args.markdown_out) if args.markdown_out else reports_dir / "IEA_INTEGRITY_BATCH_REPORT.md"
    json_path = Path(args.json_out) if args.json_out else reports_dir / "IEA_INTEGRITY_BATCH_REPORT.json"

    dates = date_range_inclusive(start, end)
    rows, warnings = collect_rows(reports_dir, dates, args.run_daily_audit, project_root)

    full_rows = [r for r in rows if is_full_day(r)]
    excluded_rows = [r for r in rows if not is_full_day(r)]

    exec_sum = executive_summary(full_rows)
    null_counts = null_counts_full_table(full_rows)

    compare_rows: Optional[List[DayRow]] = None
    compare_dates_str: Optional[List[str]] = None
    compare_label = ""
    if args.compare_dates:
        compare_dates_str = sorted({parse_date(s).isoformat() for s in args.compare_dates})
        by_date: Dict[str, DayRow] = {r.audit_date: r for r in rows}
        for ds in compare_dates_str:
            if ds in by_date:
                continue
            p = reports_dir / f"{ds}.json"
            obj = load_audit_json(p)
            if obj is None:
                warnings.append(f"compare-dates: missing or unreadable JSON: {p}")
                by_date[ds] = DayRow(
                    audit_date=ds,
                    audit_mode=None,
                    engine_full=None,
                    confidence=None,
                    overall_status=None,
                    trade_readiness_decision=None,
                    iea_status=None,
                    iea_resolution_suspect_count=None,
                    registry_timing_gap_count=None,
                    registry_timing_gap_avg_delay_ms=None,
                    registry_timing_gap_max_delay_ms=None,
                    lifecycle_wrong_adapter_count=None,
                    reconciliation_avg_efficiency=None,
                    cross_iea_duplicate_events=None,
                    _raw_path=str(p),
                )
            else:
                by_date[ds] = row_from_json(obj, p)
        compare_rows = [by_date[ds] for ds in compare_dates_str if ds in by_date]
        compare_label = f"Dates: {', '.join(compare_dates_str)}"

    md_body = render_markdown_report(
        exec_sum, full_rows, excluded_rows, compare_rows, compare_label, warnings, null_counts
    )
    md_path.parent.mkdir(parents=True, exist_ok=True)
    with md_path.open("w", encoding="utf-8") as f:
        f.write(md_body)

    json_obj = build_json_output(
        exec_sum,
        full_rows,
        excluded_rows,
        compare_rows,
        compare_dates_str,
        warnings,
        (start.isoformat(), end.isoformat()),
        null_counts,
    )
    with json_path.open("w", encoding="utf-8") as f:
        json.dump(json_obj, f, indent=2, sort_keys=True)

    print(f"Wrote {md_path}")
    print(f"Wrote {json_path}")
    nfull = len(full_rows)
    if nfull:
        heavy = [(c, cnt) for c, cnt in sorted(null_counts.items(), key=lambda x: (-x[1], x[0])) if cnt > 0]
        print("")
        print("Null-heavy columns (FULL days, count / days):")
        for c, cnt in heavy[:12]:
            print(f"  {c}: {cnt} / {nfull} ({round(100.0 * cnt / nfull, 1)}%)")
        if not heavy:
            print("  (no all-null columns)")
    if args.markdown:
        print("")
        print(md_body)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
