#!/usr/bin/env python3
"""
Check RTY stop price calculation
"""
import json

# From logs:
range_low = 2652.5
range_high = 2674.6
range_size = range_high - range_low  # 22.1

# Short trade:
short_entry = 2652.4  # breakout short = range_low - tick_size (0.1)
short_stop = 2674.5   # from intent

# Long trade:
long_entry = 2674.7   # breakout long = range_high + tick_size (0.1)
long_stop = 2652.6    # from intent

print("="*80)
print("RTY STOP PRICE CALCULATION ANALYSIS")
print("="*80)

print(f"\nRange Values:")
print(f"  Range Low: {range_low}")
print(f"  Range High: {range_high}")
print(f"  Range Size: {range_size}")

print(f"\nShort Trade:")
print(f"  Entry Price: {short_entry}")
print(f"  Stop Price: {short_stop}")
print(f"  Stop Distance: {short_stop - short_entry}")

print(f"\nLong Trade:")
print(f"  Entry Price: {long_entry}")
print(f"  Stop Price: {long_stop}")
print(f"  Stop Distance: {long_entry - long_stop}")

# Calculate what stop should be based on formula:
# Stop loss = min(range_size, 3 * target_pts) in points
# For Short: stopPrice = entryPrice + slPoints

# Need to find base target for RTY
# Common targets: ES=10, NQ=20, RTY=?
# Let's reverse engineer from the stop price

print(f"\n" + "="*80)
print("REVERSE ENGINEERING STOP CALCULATION")
print("="*80)

# For Short: stop = entry + slPoints
# slPoints = stop - entry = 2674.5 - 2652.4 = 22.1

sl_points_short = short_stop - short_entry
print(f"\nShort Stop Points: {sl_points_short}")

# slPoints = min(range_size, 3 * target_pts)
# 22.1 = min(22.1, 3 * target_pts)
# This means range_size (22.1) was used, not 3 * target_pts
# So: 22.1 <= 3 * target_pts, meaning target_pts >= 7.37

# But wait, if range_size was used, then:
# slPoints = range_size = 22.1
# stop = entry + 22.1 = 2652.4 + 22.1 = 2674.5 ✓

print(f"\nCalculation Check:")
print(f"  If slPoints = range_size ({range_size}):")
print(f"    Stop = Entry + slPoints = {short_entry} + {range_size} = {short_entry + range_size}")
print(f"    Actual Stop: {short_stop}")
print(f"    Match: {'YES' if abs((short_entry + range_size) - short_stop) < 0.01 else 'NO'}")

# For Long: stop = entry - slPoints
sl_points_long = long_entry - long_stop
print(f"\nLong Stop Points: {sl_points_long}")

print(f"\nCalculation Check:")
print(f"  If slPoints = range_size ({range_size}):")
print(f"    Stop = Entry - slPoints = {long_entry} - {range_size} = {long_entry - range_size}")
print(f"    Actual Stop: {long_stop}")
print(f"    Match: {'YES' if abs((long_entry - range_size) - long_stop) < 0.01 else 'NO'}")

print(f"\n" + "="*80)
print("CONCLUSION")
print("="*80)
print(f"\nStop prices are calculated as:")
print(f"  Short: Entry ({short_entry}) + Range Size ({range_size}) = {short_entry + range_size}")
print(f"  Long: Entry ({long_entry}) - Range Size ({range_size}) = {long_entry - range_size}")
print(f"\nThis means range_size ({range_size}) was used, not 3 * target_pts")
print(f"(If 3 * target_pts was smaller, it would have been used instead)")
