"""
Matrix Mode Comparison Script

Compares authoritative rebuild vs window update modes on the same date range.
Produces evidence for mode simplification assessment.
"""

import pandas as pd
import hashlib
import json
from pathlib import Path
from datetime import datetime, timedelta
from typing import Dict, Any, Tuple
import sys

# Add parent directories to path
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from modules.matrix.master_matrix import MasterMatrix
from modules.matrix.file_manager import load_existing_matrix


def compute_output_hash(df: pd.DataFrame) -> str:
    """Compute deterministic hash of matrix output."""
    if df.empty:
        return hashlib.sha256(b"empty").hexdigest()
    
    df_sorted = df.sort_values(
        by=['trade_date', 'entry_time', 'Instrument', 'Stream'],
        ascending=[True, True, True, True]
    ).reset_index(drop=True)
    parquet_bytes = df_sorted.to_parquet(index=False)
    return hashlib.sha256(parquet_bytes).hexdigest()


def compare_dataframes(df1: pd.DataFrame, df2: pd.DataFrame, name1: str, name2: str) -> Dict[str, Any]:
    """Compare two DataFrames and return detailed differences."""
    comparison = {
        'row_count_1': len(df1),
        'row_count_2': len(df2),
        'row_count_diff': len(df1) - len(df2),
        'hash_1': compute_output_hash(df1),
        'hash_2': compute_output_hash(df2),
        'hashes_match': compute_output_hash(df1) == compute_output_hash(df2),
        'columns_1': list(df1.columns) if not df1.empty else [],
        'columns_2': list(df2.columns) if not df2.empty else [],
        'columns_match': list(df1.columns) == list(df2.columns) if not (df1.empty or df2.empty) else True,
    }
    
    if df1.empty and df2.empty:
        comparison['differences'] = []
        comparison['summary'] = f"Both {name1} and {name2} are empty"
        return comparison
    
    if df1.empty or df2.empty:
        comparison['differences'] = [f"One DataFrame is empty: {name1} has {len(df1)} rows, {name2} has {len(df2)} rows"]
        comparison['summary'] = f"One DataFrame is empty"
        return comparison
    
    # Compare date ranges
    if 'trade_date' in df1.columns and 'trade_date' in df2.columns:
        comparison['date_range_1'] = {
            'min': str(df1['trade_date'].min()),
            'max': str(df1['trade_date'].max())
        }
        comparison['date_range_2'] = {
            'min': str(df2['trade_date'].min()),
            'max': str(df2['trade_date'].max())
        }
    
    # Compare by trade key (Stream, Date, Time)
    if all(col in df1.columns for col in ['Stream', 'trade_date', 'entry_time']):
        df1_keys = df1[['Stream', 'trade_date', 'entry_time']].apply(
            lambda x: (str(x['Stream']), str(x['trade_date']), str(x['entry_time'])), axis=1
        ).tolist()
        df2_keys = df2[['Stream', 'trade_date', 'entry_time']].apply(
            lambda x: (str(x['Stream']), str(x['trade_date']), str(x['entry_time'])), axis=1
        ).tolist()
        
        df1_key_set = set(df1_keys)
        df2_key_set = set(df2_keys)
        
        comparison['keys_only_in_1'] = list(df1_key_set - df2_key_set)
        comparison['keys_only_in_2'] = list(df2_key_set - df1_key_set)
        comparison['keys_in_both'] = len(df1_key_set & df2_key_set)
        comparison['key_differences'] = {
            'only_in_1_count': len(comparison['keys_only_in_1']),
            'only_in_2_count': len(comparison['keys_only_in_2']),
            'in_both_count': comparison['keys_in_both']
        }
    
    # Compare streams
    if 'Stream' in df1.columns and 'Stream' in df2.columns:
        streams_1 = set(df1['Stream'].unique())
        streams_2 = set(df2['Stream'].unique())
        comparison['streams_1'] = sorted(list(streams_1))
        comparison['streams_2'] = sorted(list(streams_2))
        comparison['streams_only_in_1'] = sorted(list(streams_1 - streams_2))
        comparison['streams_only_in_2'] = sorted(list(streams_2 - streams_1))
    
    # Sample differences
    differences = []
    if comparison['row_count_diff'] != 0:
        differences.append(f"Row count difference: {comparison['row_count_diff']} rows")
    if not comparison['hashes_match']:
        differences.append("Output hashes differ (data content differs)")
    if comparison.get('key_differences', {}).get('only_in_1_count', 0) > 0:
        differences.append(f"{comparison['key_differences']['only_in_1_count']} trade keys only in {name1}")
    if comparison.get('key_differences', {}).get('only_in_2_count', 0) > 0:
        differences.append(f"{comparison['key_differences']['only_in_2_count']} trade keys only in {name2}")
    
    comparison['differences'] = differences
    comparison['summary'] = "; ".join(differences) if differences else "No differences detected"
    
    return comparison


def run_authoritative_rebuild(analyzer_runs_dir: str, output_dir: str, start_date: str = None, end_date: str = None) -> Tuple[pd.DataFrame, Dict[str, Any]]:
    """Run authoritative rebuild mode."""
    print(f"\n{'='*80}")
    print("Running AUTHORITATIVE REBUILD")
    print(f"{'='*80}")
    
    matrix = MasterMatrix(analyzer_runs_dir=analyzer_runs_dir)
    
    start_time = datetime.now()
    df = matrix.build_master_matrix(
        start_date=start_date,
        end_date=end_date,
        output_dir=output_dir,
        authoritative=True
    )
    end_time = datetime.now()
    
    duration = (end_time - start_time).total_seconds()
    
    stats = {
        'mode': 'authoritative_rebuild',
        'row_count': len(df),
        'duration_seconds': duration,
        'hash': compute_output_hash(df),
        'start_date': start_date,
        'end_date': end_date,
    }
    
    if not df.empty and 'trade_date' in df.columns:
        stats['date_range'] = {
            'min': str(df['trade_date'].min()),
            'max': str(df['trade_date'].max())
        }
        stats['streams'] = sorted(df['Stream'].unique().tolist()) if 'Stream' in df.columns else []
    
    print(f"Authoritative rebuild complete: {len(df)} rows in {duration:.2f}s")
    
    return df, stats


def run_window_update(analyzer_runs_dir: str, output_dir: str, reprocess_days: int = 60) -> Tuple[pd.DataFrame, Dict[str, Any]]:
    """Run window update mode."""
    print(f"\n{'='*80}")
    print(f"Running WINDOW UPDATE (reprocess_days={reprocess_days})")
    print(f"{'='*80}")
    
    matrix = MasterMatrix(analyzer_runs_dir=analyzer_runs_dir)
    
    start_time = datetime.now()
    df, update_stats = matrix.build_master_matrix_window_update(
        reprocess_days=reprocess_days,
        output_dir=output_dir
    )
    end_time = datetime.now()
    
    duration = (end_time - start_time).total_seconds()
    
    stats = {
        'mode': 'window_update',
        'row_count': len(df),
        'duration_seconds': duration,
        'hash': compute_output_hash(df),
        'reprocess_days': reprocess_days,
        'update_stats': update_stats,
    }
    
    if not df.empty and 'trade_date' in df.columns:
        stats['date_range'] = {
            'min': str(df['trade_date'].min()),
            'max': str(df['trade_date'].max())
        }
        stats['streams'] = sorted(df['Stream'].unique().tolist()) if 'Stream' in df.columns else []
    
    print(f"Window update complete: {len(df)} rows in {duration:.2f}s")
    
    return df, stats


def main():
    """Main comparison function."""
    import argparse
    
    parser = argparse.ArgumentParser(description='Compare matrix build modes')
    parser.add_argument('--analyzer-runs-dir', type=str, default='data/analyzed',
                       help='Directory containing analyzer output')
    parser.add_argument('--output-dir', type=str, default='data/master_matrix',
                       help='Directory for matrix output')
    parser.add_argument('--start-date', type=str, default=None,
                       help='Start date for authoritative rebuild (YYYY-MM-DD)')
    parser.add_argument('--end-date', type=str, default=None,
                       help='End date for authoritative rebuild (YYYY-MM-DD)')
    parser.add_argument('--reprocess-days', type=int, default=60,
                       help='Number of days to reprocess for window update')
    parser.add_argument('--output-file', type=str, default='MATRIX_MODE_COMPARISON.json',
                       help='Output file for comparison results')
    
    args = parser.parse_args()
    
    print("\n" + "="*80)
    print("MATRIX MODE COMPARISON")
    print("="*80)
    print(f"Analyzer runs dir: {args.analyzer_runs_dir}")
    print(f"Output dir: {args.output_dir}")
    print(f"Authoritative rebuild date range: {args.start_date} to {args.end_date}")
    print(f"Window update reprocess days: {args.reprocess_days}")
    
    # Run authoritative rebuild
    auth_df, auth_stats = run_authoritative_rebuild(
        analyzer_runs_dir=args.analyzer_runs_dir,
        output_dir=f"{args.output_dir}_auth",
        start_date=args.start_date,
        end_date=args.end_date
    )
    
    # Run window update
    window_df, window_stats = run_window_update(
        analyzer_runs_dir=args.analyzer_runs_dir,
        output_dir=f"{args.output_dir}_window",
        reprocess_days=args.reprocess_days
    )
    
    # Compare outputs
    print(f"\n{'='*80}")
    print("COMPARING OUTPUTS")
    print(f"{'='*80}")
    
    comparison = compare_dataframes(
        auth_df, window_df,
        "Authoritative Rebuild", "Window Update"
    )
    
    # Compile full report
    report = {
        'timestamp': datetime.now().isoformat(),
        'parameters': {
            'analyzer_runs_dir': args.analyzer_runs_dir,
            'output_dir': args.output_dir,
            'authoritative_start_date': args.start_date,
            'authoritative_end_date': args.end_date,
            'window_reprocess_days': args.reprocess_days,
        },
        'authoritative_rebuild': auth_stats,
        'window_update': window_stats,
        'comparison': comparison,
    }
    
    # Print summary
    print("\n" + "="*80)
    print("COMPARISON SUMMARY")
    print("="*80)
    print(f"Authoritative rebuild: {auth_stats['row_count']} rows, hash: {auth_stats['hash'][:16]}...")
    print(f"Window update: {window_stats['row_count']} rows, hash: {window_stats['hash'][:16]}...")
    print(f"\nDifferences: {comparison['summary']}")
    
    if comparison['hashes_match']:
        print("\n✅ OUTPUTS ARE IDENTICAL")
    else:
        print("\n⚠️ OUTPUTS DIFFER")
        if comparison.get('key_differences'):
            kd = comparison['key_differences']
            print(f"  - Keys only in authoritative: {kd.get('only_in_1_count', 0)}")
            print(f"  - Keys only in window update: {kd.get('only_in_2_count', 0)}")
            print(f"  - Keys in both: {kd.get('in_both_count', 0)}")
    
    # Save report
    output_path = Path(args.output_file)
    with open(output_path, 'w') as f:
        json.dump(report, f, indent=2, default=str)
    
    print(f"\nFull report saved to: {output_path}")
    
    return report


if __name__ == "__main__":
    main()
