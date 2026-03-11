#!/usr/bin/env python3
"""
Diagnose NoTrade Discrepancy

Compares analyzer output vs master matrix to find days/slots marked NoTrade
in the matrix when the analyzer produced Win/Loss/BE (executed trades).

Usage:
  python scripts/diagnose_no_trade_discrepancy.py [--stream ES1] [--limit 50]

Output:
  - Count of discrepancies by stream
  - Sample rows where analyzer had executed trade but matrix has NoTrade
  - Possible causes and next steps
"""

import argparse
import sys
from pathlib import Path

# Add project root
PROJECT_ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(PROJECT_ROOT))

import pandas as pd


def load_analyzer_data(analyzer_dir: Path, stream_id: str) -> pd.DataFrame:
    """Load analyzer output for a stream from data/analyzed/<stream>/."""
    stream_dir = analyzer_dir / stream_id
    if not stream_dir.exists():
        return pd.DataFrame()

    dfs = []
    for year_dir in stream_dir.iterdir():
        if not year_dir.is_dir() or len(year_dir.name) != 4 or not year_dir.name.isdigit():
            continue
        for parquet in year_dir.glob(f"{stream_id}_an_*.parquet"):
            try:
                df = pd.read_parquet(parquet)
                dfs.append(df)
            except Exception as e:
                print(f"  [WARN] Could not read {parquet}: {e}", file=sys.stderr)
    if not dfs:
        return pd.DataFrame()
    out = pd.concat(dfs, ignore_index=True)
    # Normalize Date (analyzer uses Date column)
    if 'trade_date' not in out.columns and 'Date' in out.columns:
        out['trade_date'] = pd.to_datetime(out['Date'], errors='coerce')
    elif 'trade_date' in out.columns:
        out['trade_date'] = pd.to_datetime(out['trade_date'], errors='coerce')
    if 'Time' in out.columns:
        out['Time_str'] = out['Time'].astype(str).str.strip()
        # Normalize 7:30 -> 07:30
        out['Time_str'] = out['Time_str'].str.replace(r'^(\d):', r'0\1:', regex=True)
    return out


def load_matrix_data(matrix_dir: Path) -> pd.DataFrame:
    """Load the best (fullest) master matrix file."""
    parquet_files = sorted(matrix_dir.glob("master_matrix_*.parquet"), key=lambda p: p.stat().st_mtime, reverse=True)
    # Prefer files with row count in filename
    import re
    row_re = re.compile(r"_(\d+)n\.parquet$", re.I)
    best = None
    best_rows = 0
    for pf in parquet_files[:30]:
        m = row_re.search(pf.name)
        rows = int(m.group(1)) if m else 0
        if rows > best_rows:
            best_rows = rows
            best = pf
    if best is None:
        best = parquet_files[0] if parquet_files else None
    if best is None:
        return pd.DataFrame()
    return pd.read_parquet(best)


def normalize_result(r) -> str:
    if pd.isna(r):
        return ""
    s = str(r).strip().upper()
    if s in ("WIN", "LOSS", "BE", "BREAKEVEN", "TIME"):
        return "EXECUTED"
    if s in ("NOTRADE", "NO_TRADE"):
        return "NOTRADE"
    return s


def main():
    ap = argparse.ArgumentParser(description="Diagnose NoTrade discrepancies between analyzer and matrix")
    ap.add_argument("--stream", default=None, help="Stream to check (e.g. ES1). If omitted, check all streams.")
    ap.add_argument("--limit", type=int, default=50, help="Max sample rows to print per stream")
    ap.add_argument("--verbose", "-v", action="store_true", help="Show analyzer vs matrix row for first discrepancy")
    ap.add_argument("--analyzer-dir", default="data/analyzed", help="Analyzer output directory")
    ap.add_argument("--matrix-dir", default="data/master_matrix", help="Master matrix directory")
    args = ap.parse_args()

    analyzer_dir = PROJECT_ROOT / args.analyzer_dir
    matrix_dir = PROJECT_ROOT / args.matrix_dir

    if not analyzer_dir.exists():
        print(f"ERROR: Analyzer dir not found: {analyzer_dir}")
        return 1
    if not matrix_dir.exists():
        print(f"ERROR: Matrix dir not found: {matrix_dir}")
        return 1

    matrix_df = load_matrix_data(matrix_dir)
    if matrix_df.empty:
        print("ERROR: No master matrix files found")
        return 1

    # Ensure matrix has required columns
    for col in ['Stream', 'trade_date', 'Time', 'Result']:
        if col not in matrix_df.columns:
            print(f"ERROR: Matrix missing column: {col}")
            return 1

    # Normalize matrix
    matrix_df['trade_date'] = pd.to_datetime(matrix_df['trade_date'], errors='coerce')
    matrix_df['Time_str'] = matrix_df['Time'].astype(str).str.strip().str.replace(r'^(\d):', r'0\1:', regex=True)
    matrix_df['Date_norm'] = matrix_df['trade_date'].dt.normalize()

    streams_to_check = [args.stream] if args.stream else sorted(matrix_df['Stream'].unique().tolist())

    print("=" * 70)
    print("NoTrade Discrepancy Diagnostic")
    print("=" * 70)
    print(f"Matrix file: {matrix_dir} (loaded {len(matrix_df)} rows)")
    print(f"Streams to check: {streams_to_check}")
    print()

    total_discrepancies = 0
    causes = []

    for stream_id in streams_to_check:
        analyzer_df = load_analyzer_data(analyzer_dir, stream_id)
        if analyzer_df.empty:
            print(f"[{stream_id}] No analyzer data found - SKIP")
            continue

        # Analyzer: executed = Win/Loss/BE
        analyzer_executed = analyzer_df[
            analyzer_df['Result'].astype(str).str.upper().str.strip().isin(['WIN', 'LOSS', 'BE', 'BREAKEVEN', 'TIME'])
        ].copy()
        if analyzer_executed.empty:
            print(f"[{stream_id}] No executed trades in analyzer - SKIP")
            continue

        # Build analyzer key: (date, time_str)
        date_col = 'trade_date' if 'trade_date' in analyzer_executed.columns else 'Date'
        analyzer_executed['Date_norm'] = pd.to_datetime(analyzer_executed[date_col], errors='coerce').dt.normalize()
        if 'Time_str' not in analyzer_executed.columns:
            analyzer_executed['Time_str'] = analyzer_executed['Time'].astype(str).str.strip().str.replace(r'^(\d):', r'0\1:', regex=True)

        analyzer_keys = set(
            (row['Date_norm'], row['Time_str'])
            for _, row in analyzer_executed.iterrows()
            if pd.notna(row.get('Date_norm')) and pd.notna(row.get('Time_str'))
        )

        # Matrix: NoTrade rows for this stream
        matrix_stream = matrix_df[matrix_df['Stream'] == stream_id]
        matrix_notrade = matrix_stream[
            matrix_stream['Result'].astype(str).str.upper().str.strip().isin(['NOTRADE', 'NO_TRADE', ''])
        ]

        discrepancies = []
        for _, row in matrix_notrade.iterrows():
            dt = row.get('Date_norm') or row.get('trade_date')
            if pd.isna(dt):
                continue
            time_str = str(row.get('Time_str', row.get('Time', ''))).strip()
            if (dt, time_str) in analyzer_keys:
                discrepancies.append({
                    'date': dt,
                    'time': time_str,
                    'stream': stream_id,
                })

        if discrepancies:
            total_discrepancies += len(discrepancies)
            print(f"[{stream_id}] FOUND {len(discrepancies)} discrepancies: analyzer had executed trade, matrix has NoTrade")
            for i, d in enumerate(discrepancies[: args.limit]):
                print(f"    {i+1}. {d['date']} {d['time']}")
            if len(discrepancies) > args.limit:
                print(f"    ... and {len(discrepancies) - args.limit} more")
            causes.append(f"{stream_id}: {len(discrepancies)} slots")

            if args.verbose and discrepancies:
                d = discrepancies[0]
                dt, tm = d['date'], d['time']
                ana_row = analyzer_executed[
                    (analyzer_executed['Date_norm'] == dt) & (analyzer_executed['Time_str'] == tm)
                ].iloc[0]
                mat_row = matrix_notrade[
                    (matrix_notrade['Date_norm'] == dt) & (matrix_notrade['Time_str'] == tm)
                ].iloc[0]
                print(f"\n  [VERBOSE] Sample discrepancy for {stream_id} {dt} {tm}:")
                print(f"    Analyzer: Result={ana_row.get('Result')}, Session={ana_row.get('Session')}, Time={ana_row.get('Time')}")
                print(f"    Matrix:   Result={mat_row.get('Result')}, Session={mat_row.get('Session')}, Time={mat_row.get('Time')}")
        else:
            print(f"[{stream_id}] OK - no discrepancies")

    print()
    print("=" * 70)
    print("Summary")
    print("=" * 70)
    print(f"Total discrepancies: {total_discrepancies}")
    if total_discrepancies > 0:
        print()
        print("Possible causes:")
        print("1. Session mismatch: select_trade_for_time requires (Time_str, Session) match.")
        print("   Check if analyzer Session column matches matrix session for that time.")
        print("2. Time format: Analyzer Time vs matrix Time normalization (07:30 vs 7:30).")
        print("3. Date format: trade_date/Date parsing differences (timezone, format).")
        print("4. Excluded times: Stream filter exclude_times may filter out valid trades.")
        print("5. Invalid trade_date: Rows dropped in salvage mode (check logs for 'invalid trade_date').")
        print()
        print("Next steps:")
        print("- Run with --stream ES1 (or affected stream) and inspect sample dates.")
        print("- Check logs/master_matrix.log for 'invalid trade_date' or 'Filtered out'.")
        print("- Compare analyzer parquet (Date, Time, Session, Result) for a sample date.")
        print("- Verify merger output in data/analyzed/<stream>/ has correct Session column.")

    return 0 if total_discrepancies == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
