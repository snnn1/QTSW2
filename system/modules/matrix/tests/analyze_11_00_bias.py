"""
Diagnostic script to analyze why 11:00 keeps winning time change decisions.
"""

import sys
sys.stdout.reconfigure(encoding='utf-8')

import pandas as pd
from pathlib import Path

# Add project root to path
project_root = Path(__file__).parent.parent.parent.parent
sys.path.insert(0, str(project_root))

from modules.matrix.master_matrix import MasterMatrix

def analyze_time_distribution():
    """Analyze time distribution in final matrix."""
    print("=" * 80)
    print("ANALYZING TIME DISTRIBUTION")
    print("=" * 80)
    print()
    
    mm = MasterMatrix()
    df = mm.build_master_matrix()
    
    print("=== TIME DISTRIBUTION FOR SESSION 2 STREAMS ===")
    print()
    
    for stream in ['ES2', 'NQ2', 'GC2', 'NG2', 'YM2', 'CL2']:
        stream_df = df[df['Stream'] == stream]
        if len(stream_df) > 0:
            time_counts = stream_df['Time'].value_counts()
            total = len(stream_df)
            print(f"{stream} (total: {total} trades):")
            for time in sorted(time_counts.index):
                count = time_counts[time]
                pct = (count / total) * 100
                print(f"  {time}: {count} trades ({pct:.1f}%)")
            print()
    
    print("=" * 80)
    print("If 11:00 has >50% of trades, it may indicate:")
    print("1. 11:00 genuinely has better performance (higher rolling sums)")
    print("2. There's a bias in how rolling sums are calculated")
    print("3. The sequencer is switching to 11:00 too often")
    print("=" * 80)

if __name__ == "__main__":
    analyze_time_distribution()

