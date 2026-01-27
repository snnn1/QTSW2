#!/usr/bin/env python3
"""List all ways a stream can be committed"""
import json
from pathlib import Path

print("="*80)
print("STREAM COMMIT REASONS - ALL WAYS A STREAM CAN BE COMMITTED")
print("="*80)

print("""
A stream is committed when Commit() is called, which sets:
- _journal.Committed = true
- _journal.CommitReason = <reason>
- State = StreamState.DONE

Once committed, a stream:
- Cannot be re-armed (Arm() checks Committed and returns early)
- Is skipped by RobotEngine (committed streams are marked DONE)
- Cannot trade for the rest of the trading day

COMMIT REASONS (in order of occurrence):
""")

print("""
1. NO_TRADE_LATE_START_MISSED_BREAKOUT
   Location: HandlePreHydrationState() - lines 1465, 1587
   Condition: Stream starts AFTER slot_time AND breakout already occurred
   Context: 
   - Late start detected (now > slot_time)
   - Range reconstructed from historical bars
   - Breakout detected in historical data BEFORE slot_time
   - Cannot trade because entry opportunity already passed
   Event: NO_TRADE_LATE_START_MISSED_BREAKOUT
   State: PRE_HYDRATION -> DONE (committed)

2. NO_TRADE_MARKET_CLOSE
   Location: Multiple places
   - HandlePreHydrationState() - line 1874 (before transitioning to RANGE_BUILDING)
   - HandleRangeBuildingState() - line 2019 (during range building)
   - HandleRangeLockedState() - line 2500 (during range locked)
   Condition: Market has closed AND no entry detected
   Context:
   - utcNow >= MarketCloseUtc
   - _entryDetected == false
   - Trading day ended without entry
   Event: MARKET_CLOSE_NO_TRADE
   State: PRE_HYDRATION/RANGE_BUILDING/RANGE_LOCKED -> DONE (committed)

3. RANGE_INVALIDATED
   Location: Multiple places
   - HandleRangeBuildingState() - line 2118 (when range invalidated detected)
   - HandleRangeLockedState() - line 2486 (when range invalidated at slot end)
   Condition: Gap tolerance violation detected
   Context:
   - _rangeInvalidated == true
   - Gap tolerance rules violated:
     * Single gap > MAX_SINGLE_GAP_MINUTES (default 3.0)
     * Total gaps > MAX_TOTAL_GAP_MINUTES (default 6.0)
     * Gap in last 10 minutes > MAX_GAP_LAST_10_MINUTES (default 2.0)
   - Only DATA_FEED_FAILURE gaps invalidate (LOW_LIQUIDITY gaps never invalidate)
   Event: Gap tolerance violation
   State: RANGE_BUILDING/RANGE_LOCKED -> DONE (committed)

4. NO_TRADE_NO_DATA
   Location: HandleRangeBuildingState() - line 2246
   Condition: No bars available for range computation AND slot time passed
   Context:
   - utcNow >= SlotTimeUtc.AddMinutes(5) (5 minute grace period)
   - isNoDataFailure == true
   - rangeResult.BarCount == 0
   - Cannot compute range without bars
   Event: No bars available for range computation after slot time
   State: RANGE_BUILDING -> DONE (committed)

5. STREAM_STAND_DOWN
   Location: EnterRecoveryManage() - line 2548
   Condition: Stream enters recovery management state
   Context:
   - EnterRecoveryManage() called (e.g., from RobotEngine on error)
   - Stream needs to be shut down gracefully
   - Prevents further processing
   Event: STREAM_STAND_DOWN
   State: Any -> DONE (committed)

COMMIT METHOD SIGNATURE:
private void Commit(DateTimeOffset utcNow, string commitReason, string eventType)

GUARDS:
- If already committed (_journal.Committed == true), method returns early
- Commit is idempotent (safe to call multiple times)

EFFECTS:
- Sets _journal.Committed = true
- Sets _journal.CommitReason = commitReason
- Sets _journal.LastState = DONE
- Sets _journal.LastUpdateUtc = utcNow
- Sets _journal.TimetableHashAtCommit = _timetableHash
- Saves journal to disk
- Sets State = StreamState.DONE
- Logs JOURNAL_WRITTEN event
- Logs commit-specific event (eventType parameter)
""")

print("="*80)
print("CODE LOCATIONS:")
print("="*80)
print("""
Commit() method definition:
  modules/robot/core/StreamStateMachine.cs:4107-4144

Commit() call sites:
  1. NO_TRADE_LATE_START_MISSED_BREAKOUT (SIM mode):
     modules/robot/core/StreamStateMachine.cs:1465
     Context: HandlePreHydrationState() - late start with missed breakout
  
  2. NO_TRADE_LATE_START_MISSED_BREAKOUT (DRYRUN mode):
     modules/robot/core/StreamStateMachine.cs:1587
     Context: HandlePreHydrationState() - late start with missed breakout
  
  3. NO_TRADE_MARKET_CLOSE (PRE_HYDRATION):
     modules/robot/core/StreamStateMachine.cs:1874
     Context: HandlePreHydrationState() - market closed before range building
  
  4. NO_TRADE_MARKET_CLOSE (RANGE_BUILDING):
     modules/robot/core/StreamStateMachine.cs:2019
     Context: HandleRangeBuildingState() - market closed during range building
  
  5. RANGE_INVALIDATED (RANGE_BUILDING):
     modules/robot/core/StreamStateMachine.cs:2118
     Context: HandleRangeBuildingState() - gap violation detected
  
  6. NO_TRADE_NO_DATA:
     modules/robot/core/StreamStateMachine.cs:2246
     Context: HandleRangeBuildingState() - no bars after slot time + grace period
  
  7. RANGE_INVALIDATED (RANGE_LOCKED):
     modules/robot/core/StreamStateMachine.cs:2486
     Context: HandleRangeLockedState() - range invalidated at slot end
  
  8. NO_TRADE_MARKET_CLOSE (RANGE_LOCKED):
     modules/robot/core/StreamStateMachine.cs:2500
     Context: HandleRangeLockedState() - market closed during range locked
  
  9. STREAM_STAND_DOWN:
     modules/robot/core/StreamStateMachine.cs:2548
     Context: EnterRecoveryManage() - stream recovery/standdown
""")

print("="*80)
print("COMMIT REASON SUMMARY:")
print("="*80)
print("""
Total commit reasons: 5 unique reasons

1. NO_TRADE_LATE_START_MISSED_BREAKOUT
   - Prevents trading when starting late after breakout occurred
   - Safety: Avoids trading on stale breakouts

2. NO_TRADE_MARKET_CLOSE
   - Prevents trading after market close
   - Safety: Market closed, no trading allowed

3. RANGE_INVALIDATED
   - Prevents trading when data quality is insufficient
   - Safety: Gap tolerance violations indicate unreliable data

4. NO_TRADE_NO_DATA
   - Prevents trading when no bars available
   - Safety: Cannot compute range without data

5. STREAM_STAND_DOWN
   - Prevents trading when stream enters recovery
   - Safety: Graceful shutdown on errors

All commits are terminal - once committed, stream cannot be reset or re-armed.
""")

print("="*80)
