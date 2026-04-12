"""
CLI entry for Master Matrix daily build (used by launch\\START_MASTER_MATRIX.bat).

Resolves --date to MasterMatrix.build_master_matrix(specific_date=...).
Requires repo cwd (batch files cd to QTSW2) and PYTHONPATH=%CD%\\system.
"""
from __future__ import annotations

import argparse
import sys
from pathlib import Path

# Repo root = tools/automation/ -> ../..
_ROOT = Path(__file__).resolve().parent.parent.parent
if str(_ROOT / "system") not in sys.path:
    sys.path.insert(0, str(_ROOT / "system"))


def main() -> None:
    p = argparse.ArgumentParser(description="Build master matrix for a trading date")
    p.add_argument("--date", type=str, required=True, help="Trading date YYYY-MM-DD")
    p.add_argument("--analyzer-runs-dir", type=str, default="data/analyzed")
    p.add_argument("--output-dir", type=str, default="data/master_matrix")
    args = p.parse_args()

    from modules.matrix.master_matrix import MasterMatrix

    matrix = MasterMatrix(analyzer_runs_dir=args.analyzer_runs_dir)
    matrix.build_master_matrix(
        specific_date=args.date,
        output_dir=args.output_dir,
        analyzer_runs_dir=args.analyzer_runs_dir,
    )


if __name__ == "__main__":
    main()
