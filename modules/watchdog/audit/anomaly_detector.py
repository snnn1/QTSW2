"""
Rolling z-score anomaly detection over daily audit JSON (no ML).

Reads ``data/watchdog/daily_audit/*.json``; writes ``{date}.anomalies.json``.
"""
from __future__ import annotations

import argparse
import json
import statistics
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, List, Optional

from modules.watchdog.config import QTSW2_ROOT

DEFAULT_WINDOW = 14
MIN_PRIOR_DAYS = 5
Z_WARNING = 2.0
Z_CRITICAL = 3.0
EPS = 1e-9

AUDIT_METRICS: Tuple[str, ...] = (
    "fail_closed_count",
    "fail_closed_total_duration_seconds",
    "disconnect_count",
    "recovery_completed_count",
    "adoption_grace_expired_count",
    "engine_stall_count",
    "data_stall_count",
)


def default_audit_dir() -> Path:
    return QTSW2_ROOT / "data" / "watchdog" / "daily_audit"


def list_audit_dates(audit_dir: Path) -> List[str]:
    dates: List[str] = []
    if not audit_dir.is_dir():
        return dates
    for p in audit_dir.glob("*.json"):
        if p.name.endswith(".anomalies.json"):
            continue
        stem = p.stem
        if len(stem) == 10 and stem[4] == "-" and stem[7] == "-":
            dates.append(stem)
    return sorted(set(dates))


def load_audit(trading_date: str, audit_dir: Path) -> Optional[Dict[str, Any]]:
    path = audit_dir / f"{trading_date}.json"
    if not path.is_file():
        return None
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return None


def _metric_value(payload: Dict[str, Any], metric: str) -> float:
    v = payload.get(metric, 0)
    if v is None:
        return 0.0
    if isinstance(v, (int, float)):
        return float(v)
    try:
        return float(v)
    except (TypeError, ValueError):
        return 0.0


def _z_severity(z: float) -> str:
    az = abs(z)
    if az >= Z_CRITICAL:
        return "CRITICAL"
    if az >= Z_WARNING:
        return "WARNING"
    return "OK"


def detect_anomalies_for_date(
    trading_date: str,
    audit_dir: Optional[Path] = None,
    window: int = DEFAULT_WINDOW,
) -> Dict[str, Any]:
    base = audit_dir or default_audit_dir()
    all_dates = list_audit_dates(base)
    if trading_date not in all_dates:
        today_payload = load_audit(trading_date, base)
        if today_payload is None:
            return {
                "trading_date": trading_date,
                "error": f"no_audit_json:{base / f'{trading_date}.json'}",
                "anomalies": [],
                "generated_at_utc": datetime.now(timezone.utc).isoformat(),
            }

    today = load_audit(trading_date, base)
    if today is None:
        return {
            "trading_date": trading_date,
            "error": "failed_to_parse_audit",
            "anomalies": [],
            "generated_at_utc": datetime.now(timezone.utc).isoformat(),
        }

    priors = [d for d in all_dates if d < trading_date][-window:]
    anomalies: List[Dict[str, Any]] = []

    for metric in AUDIT_METRICS:
        if len(priors) < MIN_PRIOR_DAYS:
            anomalies.append(
                {
                    "metric": metric,
                    "status": "insufficient_data",
                    "prior_days_available": len(priors),
                    "min_prior_days": MIN_PRIOR_DAYS,
                    "window_requested": window,
                }
            )
            continue

        hist: List[float] = []
        for d in priors:
            pl = load_audit(d, base)
            if pl is None:
                continue
            hist.append(_metric_value(pl, metric))

        if len(hist) < MIN_PRIOR_DAYS:
            anomalies.append(
                {
                    "metric": metric,
                    "status": "insufficient_data",
                    "prior_days_available": len(hist),
                    "min_prior_days": MIN_PRIOR_DAYS,
                    "note": "some_prior_audit_files_missing_or_unreadable",
                }
            )
            continue

        mean = statistics.mean(hist)
        std = statistics.pstdev(hist) if len(hist) > 1 else 0.0
        value = _metric_value(today, metric)

        if std < EPS:
            if abs(value - mean) < EPS:
                continue
            z_score = 10.0 if value > mean else -10.0
            sev = _z_severity(z_score)
            if sev == "OK":
                continue
            anomalies.append(
                {
                    "metric": metric,
                    "value": value,
                    "mean": round(mean, 6),
                    "std": round(std, 6),
                    "z_score": round(z_score, 4),
                    "severity": sev,
                    "note": "zero_variance_prior",
                }
            )
            continue

        z_score = (value - mean) / std
        sev = _z_severity(z_score)
        if sev == "OK":
            continue
        anomalies.append(
            {
                "metric": metric,
                "value": value,
                "mean": round(mean, 6),
                "std": round(std, 6),
                "z_score": round(z_score, 4),
                "severity": sev,
            }
        )

    return {
        "trading_date": trading_date,
        "window_days": window,
        "prior_dates_used": priors,
        "generated_at_utc": datetime.now(timezone.utc).isoformat(),
        "anomalies": anomalies,
    }


def write_anomalies_report(payload: Dict[str, Any], audit_dir: Optional[Path] = None) -> Path:
    base = audit_dir or default_audit_dir()
    base.mkdir(parents=True, exist_ok=True)
    td = payload["trading_date"]
    out = base / f"{td}.anomalies.json"
    out.write_text(json.dumps(payload, indent=2, sort_keys=True), encoding="utf-8")
    return out


def main() -> None:
    ap = argparse.ArgumentParser(description="Z-score anomalies vs recent daily audit JSON files.")
    ap.add_argument("--date", required=True, help="Trading date YYYY-MM-DD (audit JSON must exist)")
    ap.add_argument(
        "--window",
        type=int,
        default=DEFAULT_WINDOW,
        help=f"Max prior days to include in baseline (default {DEFAULT_WINDOW})",
    )
    ap.add_argument(
        "--audit-dir",
        type=Path,
        default=None,
        help="Override daily_audit directory",
    )
    args = ap.parse_args()
    audit_dir = args.audit_dir or default_audit_dir()
    payload = detect_anomalies_for_date(args.date.strip(), audit_dir=audit_dir, window=args.window)
    path = write_anomalies_report(payload, audit_dir=audit_dir)
    print(path.as_posix())


if __name__ == "__main__":
    main()
