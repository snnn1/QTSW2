"""Tests for rolling z-score anomaly detector over daily audit JSON."""
import json
from pathlib import Path

from modules.watchdog.audit import anomaly_detector as ad


def _mini(**kwargs) -> dict:
    row = {
        "fail_closed_count": 0,
        "fail_closed_total_duration_seconds": 0.0,
        "disconnect_count": 0,
        "recovery_completed_count": 0,
        "adoption_grace_expired_count": 0,
        "engine_stall_count": 0,
        "data_stall_count": 0,
    }
    row.update(kwargs)
    return row


def test_insufficient_prior_days(tmp_path: Path) -> None:
    d = tmp_path / "2026-06-10.json"
    d.write_text(json.dumps(_mini()), encoding="utf-8")
    out = ad.detect_anomalies_for_date("2026-06-10", audit_dir=tmp_path, window=14)
    assert len(out["prior_dates_used"]) == 0
    for item in out["anomalies"]:
        assert item.get("status") == "insufficient_data"


def test_zscore_spike_crITICAL(tmp_path: Path) -> None:
    base = tmp_path
    days = ["2026-06-01", "2026-06-02", "2026-06-03", "2026-06-04", "2026-06-05"]
    priors_fc = [1, 1, 1, 1, 2]
    for day, v in zip(days, priors_fc):
        (base / f"{day}.json").write_text(
            json.dumps(_mini(fail_closed_count=v, disconnect_count=0)),
            encoding="utf-8",
        )
    today = "2026-06-06"
    (base / f"{today}.json").write_text(
        json.dumps(_mini(fail_closed_count=40, disconnect_count=0)),
        encoding="utf-8",
    )
    out = ad.detect_anomalies_for_date(today, audit_dir=base, window=14)
    fc = [a for a in out["anomalies"] if a.get("metric") == "fail_closed_count" and "z_score" in a]
    assert fc, "expected fail_closed_count anomaly"
    assert fc[0]["severity"] == "CRITICAL"
    assert fc[0]["z_score"] >= 3.0


def test_zero_variance_spike(tmp_path: Path) -> None:
    for day in ["2026-07-01", "2026-07-02", "2026-07-03", "2026-07-04", "2026-07-05"]:
        (tmp_path / f"{day}.json").write_text(
            json.dumps(_mini(engine_stall_count=2)),
            encoding="utf-8",
        )
    today = "2026-07-06"
    (tmp_path / f"{today}.json").write_text(json.dumps(_mini(engine_stall_count=9)), encoding="utf-8")
    out = ad.detect_anomalies_for_date(today, audit_dir=tmp_path, window=14)
    eng = [a for a in out["anomalies"] if a.get("metric") == "engine_stall_count" and a.get("note") == "zero_variance_prior"]
    assert eng and eng[0]["severity"] == "CRITICAL"
