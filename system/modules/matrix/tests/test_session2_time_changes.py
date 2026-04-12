#!/usr/bin/env python3
"""
Diagnostic test for Session 2 time change issues

Tests why Session 2 streams always select 11:00
"""

import sys
import pandas as pd
from pathlib import Path

# Add project root to path
QTSW2_ROOT = Path(__file__).parent.parent.parent.parent
sys.path.insert(0, str(QTSW2_ROOT))

from modules.matrix import sequencer_logic
from modules.matrix.utils import get_session_for_time

# Session configuration
SLOT_ENDS = {
    "S1": ["07:30", "08:00", "09:00"],
    "S2": ["09:30", "10:00", "10:30", "11:00"],
}

def test_session2_time_selection():
    """Test how times are selected for Session 2"""
    print("="*70)
    print("SESSION 2 TIME CHANGE DIAGNOSTIC")
    print("="*70)
    print()
    
    # Create sample S2-only data
    print("Creating sample Session 2 data...")
    sample_data = pd.DataFrame({
        'Date': pd.to_datetime(['2024-01-01', '2024-01-02', '2024-01-03', '2024-01-04', '2024-01-05'] * 4),
        'Time': ['09:30', '10:00', '10:30', '11:00'] * 5,
        'Stream': ['ES2'] * 20,
        'Instrument': ['ES'] * 20,
        'Session': ['S2'] * 20,
        'Result': ['Win', 'Loss', 'Win', 'Win', 'Loss'] * 4,  # Mix of results
        'Profit': [10, -5, 8, 12, -3] * 4,
        'EntryTime': ['09:30:00'] * 20,
        'ExitTime': ['10:00:00'] * 20,
        'Direction': ['Long'] * 20,
        'Target': [10] * 20,
        'Range': [5] * 20,
        'SL': [5] * 20,
        'Peak': [10] * 20,
    })
    
    print(f"Sample data created: {len(sample_data)} trades")
    print(f"Unique times: {sorted(sample_data['Time'].unique())}")
    print(f"Unique sessions: {sample_data['Session'].unique()}")
    print()
    
    # Test available times function
    print("Testing get_available_times...")
    available_times = sequencer_logic.get_available_times(sample_data, [])
    print(f"Available times (no exclusions): {available_times}")
    print(f"First time (will be used as initial): {available_times[0] if available_times else 'NONE'}")
    print()
    
    # Test with different initial times
    print("Testing sequencer logic with S2 data...")
    stream_filters = {}
    
    result_df = sequencer_logic.apply_sequencer_logic(sample_data, stream_filters, display_year=None)
    
    if result_df is not None and len(result_df) > 0:
        print(f"\nResult: {len(result_df)} trades selected")
        print("\nTime distribution:")
        time_counts = result_df['Time'].value_counts().sort_index()
        for time, count in time_counts.items():
            print(f"  {time}: {count} trades")
        
        print("\nTime changes:")
        if 'Time Change' in result_df.columns:
            time_changes = result_df[result_df['Time Change'].notna()]
            print(f"  Total time changes: {len(time_changes)}")
            for idx, row in time_changes.iterrows():
                print(f"  {row['Date']}: {row['Time Change']}")
        
        print("\nSelected times by date:")
        for date in sorted(result_df['Date'].unique()):
            date_trades = result_df[result_df['Date'] == date]
            if len(date_trades) > 0:
                time = date_trades.iloc[0]['Time']
                result = date_trades.iloc[0]['Result']
                print(f"  {date.date()}: {time} ({result})")
        
        # Check if always 11:00
        unique_times = result_df['Time'].unique()
        if len(unique_times) == 1 and unique_times[0] == '11:00':
            print("\n[ISSUE FOUND] All trades are at 11:00!")
            print("This suggests the time change logic is not working for S2.")
        elif '11:00' in unique_times:
            count_11 = (result_df['Time'] == '11:00').sum()
            total = len(result_df)
            pct = (count_11 / total) * 100
            print(f"\n[WARNING] {count_11}/{total} ({pct:.1f}%) trades are at 11:00")
    else:
        print("[ERROR] No trades selected!")
    
    print("\n" + "="*70)
    print("Testing time change decision logic...")
    print("="*70)
    
    # Simulate a loss scenario
    print("\nSimulating loss at 09:30 with different S2 time histories...")
    
    # Create mock time slot histories
    time_slot_histories = {
        '09:30': [1, 1, -2, 1, 1, 1, -2, 1, 1, 1, 1, 1, 1],  # Sum = 8
        '10:00': [1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1],    # Sum = 13
        '10:30': [1, -2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1],  # Sum = 11
        '11:00': [1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1],    # Sum = 13
    }
    
    current_time = '09:30'
    current_sum_after = sum(time_slot_histories[current_time]) - 2  # After loss: 8 - 2 = 6
    
    # Create date_df with all S2 times
    date_df = pd.DataFrame({
        'Time': ['09:30', '10:00', '10:30', '11:00'],
        'Session': ['S2', 'S2', 'S2', 'S2'],
        'Result': ['Loss', 'Win', 'Win', 'Win'],
    })
    
    available_times = ['09:30', '10:00', '10:30', '11:00']
    current_session = 'S2'
    
    next_time, changed_to = sequencer_logic.decide_time_change(
        current_time,
        current_sum_after,
        date_df,
        time_slot_histories,
        available_times,
        current_session,
        'ES2',
        '2024-01-01'
    )
    
    print(f"Current time: {current_time}, Sum after loss: {current_sum_after}")
    print(f"Other times and their sums:")
    for time in ['10:00', '10:30', '11:00']:
        t_sum = sum(time_slot_histories.get(time, []))
        print(f"  {time}: {t_sum}")
    print(f"\nDecision: Next time = {next_time}, Changed to = {changed_to}")
    
    if next_time == '11:00':
        print("\n[ISSUE] Always selecting 11:00!")
        print("This might be because:")
        print("  1. 11:00 has the highest sum (tie-breaking issue)")
        print("  2. max() function returns last item when tied")
        print("  3. Time slot histories not properly initialized for all times")

if __name__ == '__main__':
    test_session2_time_selection()


