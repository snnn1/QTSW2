#!/usr/bin/env python3
"""Explain why NG1 can't be reset/reconstructed"""
import json
from pathlib import Path

print("="*80)
print("WHY NG1 CANNOT BE RESET/RECONSTRUCTED")
print("="*80)

print("""
The issue is a DESIGN DECISION: Committed streams are intentionally terminal.

1. GAP COUNTERS ARE RESET IN Arm() METHOD:
   - Line 2521: _largestSingleGapMinutes = 0.0
   - Line 2522: _totalGapMinutes = 0.0
   - Line 2523: _lastBarOpenChicago = null
   - Line 2524: _rangeInvalidated = false
   
   BUT Arm() checks Committed FIRST:
   - Line 2506: if (_journal.Committed) { State = DONE; return; }
   - If committed, Arm() returns early WITHOUT resetting gaps

2. CONSTRUCTOR DOESN'T RESET GAP COUNTERS:
   - Gap counters start at 0.0 (lines 139-140)
   - BUT if journal is committed, stream is skipped entirely
   - RobotEngine.cs line 2865-2875: Committed streams are skipped

3. THE PROBLEM:
   - NG1 was committed at 16:42:30 with RANGE_INVALIDATED
   - On restart, journal shows Committed = True
   - Stream constructor runs, but stream is skipped (line 2868)
   - Arm() is never called (because stream is skipped)
   - Gap counters never reset (because Arm() never runs)

4. WHY THIS DESIGN EXISTS:
   - Committed streams represent "final state" for the day
   - Prevents trading after stream has been invalidated/completed
   - Safety feature: Once invalidated, don't allow retry
   - Prevents infinite retry loops on bad data

5. WHAT WOULD NEED TO CHANGE:
   - Allow resetting committed streams (risky - defeats safety)
   - Reset gap counters in constructor (doesn't help - stream still skipped)
   - Allow reconstruction even if committed (defeats commit semantics)
   - Reset commit status on restart (defeats idempotency)

The fundamental issue: Gap counters ARE reset in Arm(), but Arm() never runs
because the stream is committed and skipped entirely.
""")

print("="*80)
print("CODE FLOW ON RESTART:")
print("="*80)
print("""
1. RobotEngine.ApplyTimetable() creates StreamStateMachine
2. StreamStateMachine constructor loads journal
3. If journal.Committed = True:
   - Stream is created but marked as committed
   - RobotEngine checks newSm.Committed (line 2865)
   - Stream is SKIPPED (line 2868: STREAM_SKIPPED)
   - Arm() is NEVER called
   - Gap counters stay at 0.0 (but stream doesn't exist)

4. If journal.Committed = False:
   - Stream is created normally
   - Arm() is called (line 2877)
   - Gap counters ARE reset (lines 2521-2522)
   - Stream reconstructs normally
""")

print("="*80)
print("THE ACTUAL PROBLEM:")
print("="*80)
print("""
NG1 was invalidated DURING the restart reconstruction process itself:
- Restart at 16:42:27 → Stream enters PRE_HYDRATION
- BarsRequest loads bars → Gaps detected in historical data
- Gaps accumulate → Total gap exceeds 6.0 minutes
- Range invalidated → Stream committed at 16:42:30

So the gaps that caused invalidation happened DURING reconstruction,
not before. The gap counters WERE reset (they start at 0.0), but
the BarsRequest data itself had gaps that accumulated during reconstruction.

The real question: Why did BarsRequest return data with so many gaps?
This suggests a data feed issue, not a code issue.
""")

print("="*80)
