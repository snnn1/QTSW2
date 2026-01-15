# Log Error Analysis Report
Generated: 2026-01-14

## Executive Summary

**Total Errors Found**: 456,502  
**Total Warnings**: 91  
**Critical Issues**: 3  
**Status**: ⚠️ **ACTIVE ERRORS DETECTED**

---

## 🔴 CRITICAL ISSUES (Active)

### 1. RANGE_COMPUTE_FAILED - NO_BARS_IN_WINDOW
**Status**: 🔴 **ACTIVE RIGHT NOW**  
**Occurrences**: 454,449  
**Most Recent**: 2026-01-14T20:58:37 UTC (just now)

**Error Details**:
```
Event: RANGE_COMPUTE_FAILED
Level: ERROR
Reason: NO_BARS_IN_WINDOW
Message: Range computation failed - will retry on next tick or use partial data
```

**Affected Instruments**: CL, ES, GC, NG (all instruments)

**What's Happening**:
- Streams are attempting to compute ranges when there are no bars in the window
- This is happening **every second** (repeated errors)
- Likely related to the `ARMED` → `RANGE_BUILDING` transition bug we just fixed
- Streams may be stuck in `RANGE_BUILDING` state trying to compute ranges prematurely

**Root Cause**:
- Streams transitioned to `RANGE_BUILDING` before range start time
- Or streams are trying to compute ranges when bars haven't arrived yet
- Could be related to the `_preHydrationComplete` bug we fixed

**Action Required**:
1. ✅ **FIXED**: Updated `UpdateTradingDate()` to reset state to `PRE_HYDRATION`
2. ⚠️ **MONITOR**: Check if streams are still stuck after fix
3. ⚠️ **VERIFY**: Ensure streams wait for range start time before computing

---

### 2. File Locking Errors - robot_ENGINE.jsonl
**Status**: ⚠️ **ONGOING**  
**Occurrences**: 40+  
**Most Recent**: 2026-01-05

**Error Details**:
```
Write failure for ENGINE: The process cannot access the file 
'C:\Users\jakej\QTSW2\logs\robot\robot_ENGINE.jsonl' 
because it is being used by another process.
```

**What's Happening**:
- Multiple processes trying to write to the same log file
- File locking conflicts preventing log writes
- Could cause log loss or corruption

**Action Required**:
1. Check if multiple robot instances are running
2. Implement file locking mechanism for log writes
3. Consider using separate log files per process instance

---

### 3. EXECUTION_GATE_INVARIANT_VIOLATION
**Status**: ⚠️ **HISTORICAL**  
**Occurrences**: 1,600  
**Most Recent**: 2026-01-05

**Error Details**:
```
Event: EXECUTION_GATE_INVARIANT_VIOLATION
Level: ERROR
```

**What's Happening**:
- Execution gate logic detecting invalid state transitions
- May indicate logic errors in risk management

**Action Required**:
1. Review execution gate logic in `RiskGate.cs`
2. Check if violations are expected or indicate bugs
3. Add more context to error messages

---

## ⚠️ MEDIUM PRIORITY ISSUES

### 4. TIMETABLE_INVALID
**Occurrences**: 425  
**Status**: Historical

**What's Happening**:
- Invalid timetable configurations detected
- May cause streams to not initialize correctly

**Action Required**:
1. Review timetable validation logic
2. Check timetable files for errors

---

### 5. RANGE_HYDRATION_ERROR
**Occurrences**: 28  
**Status**: Historical

**What's Happening**:
- Pre-hydration (CSV loading) failures
- Streams unable to load historical data

**Action Required**:
1. Check CSV file availability
2. Verify file paths and permissions
3. Review hydration error handling

---

### 6. Notification Errors (Pushover)
**Occurrences**: 5 TaskCanceledException errors  
**Status**: Minor

**Error Details**:
```
Pushover notification failed: TaskCanceledException
```

**What's Happening**:
- Network timeouts when sending notifications
- Most notifications succeed (200+ successful)

**Action Required**:
1. Increase timeout for notification requests
2. Add retry logic for failed notifications
3. Consider async notification queue

---

## 📊 Error Statistics

### By Event Type:
| Event Type | Count | Status |
|------------|-------|--------|
| RANGE_COMPUTE_FAILED | 454,449 | 🔴 ACTIVE |
| EXECUTION_GATE_INVARIANT_VIOLATION | 1,600 | ⚠️ Historical |
| TIMETABLE_INVALID | 425 | ⚠️ Historical |
| RANGE_HYDRATION_ERROR | 28 | ⚠️ Historical |

### By Instrument:
- **CL**: Multiple RANGE_COMPUTE_FAILED errors
- **ES**: Multiple RANGE_COMPUTE_FAILED errors  
- **GC**: Multiple RANGE_COMPUTE_FAILED errors
- **NG**: Multiple RANGE_COMPUTE_FAILED errors

### By Time:
- **Most Recent Errors**: 2026-01-14T20:58:37 UTC (just now)
- **Historical Errors**: 2026-01-05, 2025-12-07

---

## 🔍 Root Cause Analysis

### Primary Issue: RANGE_COMPUTE_FAILED

**Timeline**:
1. **2026-01-14 09:00 UTC**: Streams should transition `ARMED` → `RANGE_BUILDING`
2. **Bug Identified**: `_preHydrationComplete` reset while state remained `ARMED`
3. **Fix Applied**: Reset state to `PRE_HYDRATION` on trading day rollover
4. **Current**: Streams still showing `RANGE_COMPUTE_FAILED` errors

**Possible Causes**:
1. **Fix not deployed**: Code changes not compiled/deployed yet
2. **State inconsistency**: Streams already stuck before fix
3. **Timing issue**: Streams computing ranges before bars arrive
4. **Multiple instances**: Old robot instance still running

**Next Steps**:
1. ✅ Verify fix is deployed (check compiled code)
2. ✅ Restart robot to clear stuck states
3. ✅ Monitor logs after restart
4. ✅ Check if errors stop after fix

---

## 📋 Recommended Actions

### Immediate (Today):
1. ✅ **Deploy Fix**: Ensure `UpdateTradingDate()` fix is compiled and deployed
2. ✅ **Restart Robot**: Clear any stuck states
3. ✅ **Monitor Logs**: Watch for `RANGE_COMPUTE_FAILED` after restart
4. ✅ **Verify State Transitions**: Confirm `ARMED` → `RANGE_BUILDING` works correctly

### Short Term (This Week):
1. ⚠️ **Fix File Locking**: Implement proper file locking for logs
2. ⚠️ **Review Invariant Violations**: Understand why execution gate violations occur
3. ⚠️ **Improve Error Messages**: Add more context to error logs

### Long Term (This Month):
1. 📝 **Notification Retry Logic**: Add retry mechanism for Pushover
2. 📝 **Error Alerting**: Set up alerts for critical errors
3. 📝 **Error Dashboard**: Create dashboard to monitor error rates

---

## 🔗 Related Files

- `modules/robot/core/StreamStateMachine.cs` - State machine logic
- `modules/robot/core/RobotEngine.cs` - Engine tick logic
- `logs/robot/robot_*.jsonl` - Robot log files
- `logs/error_log.jsonl` - Centralized error log
- `logs/robot/robot_logging_errors.txt` - File locking errors

---

## 📝 Notes

- Most errors are from **today (2026-01-14)** - very recent
- The `RANGE_COMPUTE_FAILED` errors are happening **every second** - indicates stuck state
- File locking errors are from **2026-01-05** - older issue
- Notification errors are minor - most notifications succeed
- The fix we applied should resolve the `RANGE_COMPUTE_FAILED` issue once deployed

---

## ✅ Verification Checklist

- [ ] Fix compiled and deployed
- [ ] Robot restarted
- [ ] Logs checked after restart
- [ ] `RANGE_COMPUTE_FAILED` errors stopped
- [ ] State transitions working correctly
- [ ] No new errors appearing
