#!/usr/bin/env python3
"""
Compare SessionAuthority observation vs timetable_current.json session (pre–Phase 2 drift check).

Usage (from repo root):
  python tools/compare_authority_timetable.py

Requires: modules on path (run from QTSW2 root).
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from modules.session_authority import get_session_authority_observation  # noqa: E402
from modules.timetable.cme_session import get_cme_trading_date  # noqa: E402
from datetime import datetime, timezone  # noqa: E402


def main() -> int:
    utc_now = datetime.now(timezone.utc)
    canonical = get_cme_trading_date(utc_now)

    obs = get_session_authority_observation(ROOT)
    auth_session = obs.state.session_trading_date

    tt_path = ROOT / "data" / "timetable" / "timetable_current.json"
    timetable_session = None
    tt_err = None
    if tt_path.is_file():
        try:
            doc = json.loads(tt_path.read_text(encoding="utf-8"))
            if isinstance(doc, dict):
                raw = doc.get("session_trading_date") or doc.get("trading_date")
                timetable_session = str(raw).strip().split("T")[0] if raw else None
        except Exception as e:
            tt_err = str(e)
    else:
        tt_err = "file_missing"

    print("=== SessionAuthority vs timetable (manual drift check) ===")
    print(f"canonical_cme_session (wall-clock rule): {canonical}")
    print(f"observation_reason: {obs.observation_reason}")
    print(f"authority session (observation):         {auth_session}")
    print(f"timetable_current.json session:          {timetable_session!s}  path={tt_path}")
    if tt_err and tt_err != "file_missing":
        print(f"timetable read error: {tt_err}")

    drift_auth_vs_canon = auth_session != canonical
    drift_tt_vs_canon = timetable_session is not None and timetable_session != canonical
    drift_auth_vs_tt = (
        timetable_session is not None
        and auth_session != timetable_session
        and obs.observation_reason == "loaded"
    )

    print("--- flags ---")
    print(f"authority_vs_canonical_mismatch: {drift_auth_vs_canon} (expected for manual/replay examples)")
    print(f"timetable_vs_canonical_mismatch: {drift_tt_vs_canon}")
    if obs.observation_reason == "loaded":
        print(f"authority_vs_timetable_mismatch: {drift_auth_vs_tt}")
    else:
        print("authority_vs_timetable_mismatch: n/a (synthetic/invalid authority; persist a real file to compare)")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
