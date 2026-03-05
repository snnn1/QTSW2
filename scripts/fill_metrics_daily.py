#!/usr/bin/env python3
"""
Fill Metrics Daily — Phase 4.3 Monitoring

Output daily fill metrics for operational hygiene.
Can run standalone (metrics only) or as part of rebuild_ledger_from_logs --metrics.

Usage:
  python scripts/fill_metrics_daily.py --date 2026-03-03
  python scripts/fill_metrics_daily.py --date 2026-03-03 --json

Targets:
  fill_coverage_rate: must be 100%
  unmapped_rate: target 0
  null_trading_date_rate: target 0
  invariant_violation_count: target 0 (requires ledger build)
"""

import argparse
import json
import sys
from pathlib import Path

project_root = Path(__file__).parent.parent
sys.path.insert(0, str(project_root))

from modules.watchdog.pnl.fill_metrics import compute_fill_metrics


def main() -> int:
    parser = argparse.ArgumentParser(description="Daily fill metrics for execution logging hygiene.")
    parser.add_argument("--date", required=True, help="Trading date (YYYY-MM-DD)")
    parser.add_argument("--stream", help="Optional stream filter")
    parser.add_argument("--json", action="store_true", help="Output JSON only")
    args = parser.parse_args()

    metrics = compute_fill_metrics(args.date, args.stream or None)

    if args.json:
        print(json.dumps(metrics, indent=2))
        return 0

    print(f"METRICS:trading_date={metrics['trading_date']}")
    print(f"METRICS:total_fills={metrics['total_fills']}")
    print(f"METRICS:mapped_fills={metrics['mapped_fills']}")
    print(f"METRICS:unmapped_fills={metrics['unmapped_fills']}")
    print(f"METRICS:null_trading_date_fills={metrics['null_trading_date_fills']}")
    print(f"METRICS:fill_coverage_rate={metrics['fill_coverage_rate']}")
    print(f"METRICS:unmapped_rate={metrics['unmapped_rate']}")
    print(f"METRICS:null_trading_date_rate={metrics['null_trading_date_rate']}")
    print("METRICS:invariant_violation_count=N/A (run rebuild_ledger_from_logs --metrics for full audit)")

    # Exit 1 if targets not met
    if metrics["fill_coverage_rate"] < 1.0 or metrics["unmapped_rate"] > 0 or metrics["null_trading_date_rate"] > 0:
        print("METRICS:WARN targets not met (fill_coverage=100%, unmapped=0, null_td=0)", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
