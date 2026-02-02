#!/usr/bin/env python3
"""
Diagnose watchdog flickering issues by analyzing ENGINE_TICK_CALLSITE and bar event timing.
"""

import json
import sys
from pathlib import Path
from datetime import datetime, timezone, timedelta
from collections import defaultdict

# Thresholds from config
ENGINE_TICK_STALL_THRESHOLD_SECONDS = 15
DATA_STALL_THRESHOLD_SECONDS = 90
ENGINE_TICK_RATE_LIMIT_SECONDS = 5  # Rate limit in event feed

def parse_timestamp(ts_str):
    """Parse ISO timestamp string."""
    try:
        ts_str = ts_str.replace('Z', '+00:00')
        return datetime.fromisoformat(ts_str)
    except:
        return None

def analyze_tick_timing(log_path):
    """Analyze ENGINE_TICK_CALLSITE event timing."""
    if not log_path.exists():
        return None
    
    ticks = []
    bars = []
    
    with open(log_path, 'r', encoding='utf-8-sig') as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            
            try:
                event = json.loads(line)
                event_type = event.get('event_type', '')
                timestamp_utc = event.get('timestamp_utc', '')
                
                if event_type == 'ENGINE_TICK_CALLSITE' and timestamp_utc:
                    ts = parse_timestamp(timestamp_utc)
                    if ts:
                        ticks.append(ts)
                
                if event_type in ['BAR_ACCEPTED', 'BAR_RECEIVED_NO_STREAMS', 'ONBARUPDATE_CALLED'] and timestamp_utc:
                    ts = parse_timestamp(timestamp_utc)
                    if ts:
                        bars.append((ts, event_type))
            
            except json.JSONDecodeError:
                continue
    
    return ticks, bars

def main():
    log_file = Path("logs/robot/frontend_feed.jsonl")
    
    if not log_file.exists():
        print(f"Log file not found: {log_file}")
        return
    
    print("=" * 80)
    print("WATCHDOG FLICKERING DIAGNOSIS")
    print("=" * 80)
    print()
    
    ticks, bars = analyze_tick_timing(log_file)
    
    if not ticks:
        print("No ENGINE_TICK_CALLSITE events found in recent logs.")
        print("This could indicate:")
        print("  1. Robot is not running")
        print("  2. ENGINE_TICK_CALLSITE events are not being logged")
        print("  3. Events are being filtered out")
        return
    
    # Analyze tick gaps
    print(f"Found {len(ticks)} ENGINE_TICK_CALLSITE events")
    print()
    
    # Get recent ticks (last 100)
    recent_ticks = sorted(ticks)[-100:]
    
    if len(recent_ticks) < 2:
        print("Not enough ticks to analyze gaps.")
        return
    
    print("=" * 80)
    print("ENGINE TICK TIMING ANALYSIS")
    print("=" * 80)
    print()
    
    gaps = []
    for i in range(1, len(recent_ticks)):
        gap = (recent_ticks[i] - recent_ticks[i-1]).total_seconds()
        gaps.append(gap)
    
    if gaps:
        avg_gap = sum(gaps) / len(gaps)
        max_gap = max(gaps)
        min_gap = min(gaps)
        
        print(f"Tick gaps (seconds):")
        print(f"  Average: {avg_gap:.1f}s")
        print(f"  Min: {min_gap:.1f}s")
        print(f"  Max: {max_gap:.1f}s")
        print(f"  Threshold: {ENGINE_TICK_STALL_THRESHOLD_SECONDS}s")
        print()
        
        # Count gaps that exceed threshold
        stalls = [g for g in gaps if g > ENGINE_TICK_STALL_THRESHOLD_SECONDS]
        print(f"Gaps exceeding threshold ({ENGINE_TICK_STALL_THRESHOLD_SECONDS}s): {len(stalls)}")
        
        if stalls:
            print(f"  Largest gap: {max(stalls):.1f}s")
            print(f"  Average stall gap: {sum(stalls)/len(stalls):.1f}s")
            print()
            print("Stall gaps (seconds):")
            for gap in sorted(stalls, reverse=True)[:10]:
                print(f"  {gap:.1f}s")
        
        # Check for patterns
        print()
        print("Pattern Analysis:")
        if avg_gap > ENGINE_TICK_RATE_LIMIT_SECONDS * 1.5:
            print(f"  [WARN] Average gap ({avg_gap:.1f}s) is > 1.5x rate limit ({ENGINE_TICK_RATE_LIMIT_SECONDS}s)")
            print(f"         This suggests rate limiting is causing gaps")
        
        if max_gap > ENGINE_TICK_STALL_THRESHOLD_SECONDS:
            print(f"  [WARN] Max gap ({max_gap:.1f}s) exceeds stall threshold ({ENGINE_TICK_STALL_THRESHOLD_SECONDS}s)")
            print(f"         This would cause watchdog to show STALLED")
        
        # Check for consecutive gaps
        consecutive_stalls = 0
        max_consecutive = 0
        for gap in gaps:
            if gap > ENGINE_TICK_STALL_THRESHOLD_SECONDS:
                consecutive_stalls += 1
                max_consecutive = max(max_consecutive, consecutive_stalls)
            else:
                consecutive_stalls = 0
        
        if max_consecutive > 0:
            print(f"  [WARN] Found {max_consecutive} consecutive stall gaps")
            print(f"         This would cause extended flickering")
    
    print()
    print("=" * 80)
    print("BAR EVENT TIMING ANALYSIS")
    print("=" * 80)
    print()
    
    if bars:
        recent_bars = sorted(bars, key=lambda x: x[0])[-50:]
        bar_gaps = []
        
        for i in range(1, len(recent_bars)):
            gap = (recent_bars[i][0] - recent_bars[i-1][0]).total_seconds()
            bar_gaps.append(gap)
        
        if bar_gaps:
            avg_bar_gap = sum(bar_gaps) / len(bar_gaps)
            max_bar_gap = max(bar_gaps)
            
            print(f"Bar event gaps (seconds):")
            print(f"  Average: {avg_bar_gap:.1f}s")
            print(f"  Max: {max_bar_gap:.1f}s")
            print(f"  Threshold: {DATA_STALL_THRESHOLD_SECONDS}s")
            print()
            
            bar_stalls = [g for g in bar_gaps if g > DATA_STALL_THRESHOLD_SECONDS]
            if bar_stalls:
                print(f"Bar gaps exceeding threshold: {len(bar_stalls)}")
                print(f"  Largest gap: {max(bar_stalls):.1f}s")
    
    print()
    print("=" * 80)
    print("RECOMMENDATIONS")
    print("=" * 80)
    print()
    
    if gaps and max(gaps) > ENGINE_TICK_STALL_THRESHOLD_SECONDS:
        print("Issue: ENGINE_TICK_CALLSITE gaps exceed stall threshold")
        print()
        print("Possible causes:")
        print("  1. Rate limiting (events written every 5s) + processing delays")
        print("  2. Temporary Tick() pauses (garbage collection, I/O)")
        print("  3. Event feed processing delays")
        print()
        print("Solutions:")
        print("  1. Increase ENGINE_TICK_STALL_THRESHOLD_SECONDS to 20-25s")
        print("     (Currently 15s = 3x rate limit, could use 4-5x)")
        print("  2. Use smoothing/averaging instead of single threshold")
        print("  3. Check if Tick() is actually stalling or just rate limiting")
        print()
    
    if bar_gaps and max(bar_gaps) > DATA_STALL_THRESHOLD_SECONDS:
        print("Issue: Bar event gaps exceed stall threshold")
        print()
        print("Possible causes:")
        print("  1. Market pauses (lunch break, low liquidity)")
        print("  2. Rate limiting (BAR_RECEIVED_NO_STREAMS rate-limited to 60s)")
        print("  3. Actual data feed stalls")
        print()
        print("Solutions:")
        print("  1. Increase DATA_STALL_THRESHOLD_SECONDS if market pauses are normal")
        print("  2. Check if stalls correlate with market hours")
        print("  3. Verify data feed is actually flowing")

if __name__ == "__main__":
    main()
