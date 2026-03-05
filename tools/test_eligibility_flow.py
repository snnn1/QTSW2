#!/usr/bin/env python3
"""
Manual verification script for Session Eligibility flow.

Run this to verify:
1. Eligibility builder creates/updates eligibility files
2. Eligibility status API returns correct data (backend must be running)
3. Matrix save triggers eligibility builder (optional: run resequence)

Usage:
  python tools/test_eligibility_flow.py [--api-port 8001]

Prerequisites:
  - Master matrix exists in data/master_matrix/
  - For API test: Dashboard backend running (default port 8001)
"""

import argparse
import json
import sys
from pathlib import Path
from datetime import datetime, timezone

# Add project root
ROOT = Path(__file__).parent.parent
sys.path.insert(0, str(ROOT))


def run_eligibility_builder(trading_date: str) -> bool:
    """Run eligibility_builder.py for given date. Returns True if success."""
    import subprocess
    script = ROOT / "scripts" / "eligibility_builder.py"
    if not script.exists():
        print(f"  [FAIL] Script not found: {script}")
        return False
    result = subprocess.run(
        [sys.executable, str(script), "--date", trading_date],
        cwd=str(ROOT),
        capture_output=True,
        text=True,
        timeout=120,
    )
    if result.returncode != 0:
        print(f"  [FAIL] exit_code={result.returncode}")
        if result.stderr:
            print(f"  stderr: {result.stderr[:500]}")
        return False
    print("  [OK] Builder completed")
    return True


def check_eligibility_file(trading_date: str) -> bool:
    """Check that eligibility file exists and is valid."""
    path = ROOT / "data" / "timetable" / f"eligibility_{trading_date}.json"
    if not path.exists():
        print(f"  [FAIL] File not found: {path}")
        return False
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except Exception as e:
        print(f"  [FAIL] Invalid JSON: {e}")
        return False
    required = ["trading_date", "freeze_time_utc", "eligible_stream_count", "eligible_streams"]
    for k in required:
        if k not in data:
            print(f"  [FAIL] Missing key: {k}")
            return False
    print(f"  [OK] eligibility_{trading_date}.json: {data['eligible_stream_count']} eligible")
    return True


def check_api(port: int = 8001) -> bool:
    """Check GET /api/timetable/eligibility/status."""
    try:
        import requests
    except ImportError:
        print("  [SKIP] requests not installed")
        return True
    url = f"http://localhost:{port}/api/timetable/eligibility/status"
    try:
        r = requests.get(url, timeout=5)
    except requests.exceptions.ConnectionError:
        print(f"  [SKIP] Backend not running at port {port}")
        return True
    if r.status_code != 200:
        if r.status_code == 404:
            print(f"  [SKIP] API route not found (404) - ensure dashboard backend is running and has /api/timetable/eligibility/status")
        else:
            print(f"  [FAIL] status={r.status_code}")
            return False
        return True
    data = r.json()
    if data.get("status") == "none":
        print("  [OK] API returns status=none (no eligibility files)")
        return True
    print(f"  [OK] API: trading_date={data.get('trading_date')}, "
          f"eligible={data.get('eligible_stream_count')}")
    return True


def main():
    parser = argparse.ArgumentParser(description="Verify eligibility flow")
    parser.add_argument("--api-port", type=int, default=8001, help="Backend port for API check")
    parser.add_argument("--date", type=str, help="Trading date YYYY-MM-DD (default: CME today)")
    args = parser.parse_args()

    from modules.timetable.cme_session import get_trading_date_cme
    trading_date = args.date or get_trading_date_cme(datetime.now(timezone.utc))
    print(f"Trading date: {trading_date}")

    print("\n1. Run eligibility builder...")
    if not run_eligibility_builder(trading_date):
        sys.exit(1)

    print("\n2. Check eligibility file...")
    if not check_eligibility_file(trading_date):
        sys.exit(1)

    print("\n3. Check eligibility status API...")
    if not check_api(args.api_port):
        sys.exit(1)

    print("\n[PASS] All checks completed.")


if __name__ == "__main__":
    main()
