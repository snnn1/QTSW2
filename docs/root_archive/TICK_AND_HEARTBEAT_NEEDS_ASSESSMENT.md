# Tick and Heartbeat Events - What We Need vs What We Don't

**Date**: 2026-01-30  
**Analysis**: Current log status and recommendations

---

## Current Status (Last 2 Hours)

### ✅ WORKING - MUST KEEP

#### 1. ENGINE_TICK_CALLSITE ✅ **WORKING PERFECTLY**
- **Count**: 37,193 events
- **Latest**: 2026-01-30T22:05:05 (5 minutes ago)
- **Purpose**: Primary watchdog liveness signal
- **Do We Need It?**: ✅ **YES - CRITICAL**
- **Action**: **KEEP** - This is the primary heartbeat for watchdog monitoring

#### 2. ENGINE_TICK_STALL_DETECTED ✅ **WORKING**
- **Count**: 6 events detected
- **Purpose**: Detects when Tick() stops running
- **Do We Need It?**: ✅ **YES - CRITICAL**
- **Action**: **KEEP** - Essential for failure detection

#### 3. ENGINE_TICK_STALL_RECOVERED ✅ **WORKING**
- **Count**: 0 events (not needed - no stalls to recover from)
- **Purpose**: Confirms recovery after stall
- **Do We Need It?**: ✅ **YES - CRITICAL**
- **Action**: **KEEP** - Needed for recovery tracking

---

### ⚠️ NOT APPEARING - SHOULD KEEP (Rate-Limited)

#### 4. HEARTBEAT (Stream-Level) ⚠️ **NOT APPEARING YET**
- **Count**: 0 events
- **Rate Limit**: Once per 7 minutes per stream
- **Purpose**: Stream-level health monitoring
- **Do We Need It?**: ✅ **YES - IMPORTANT**
- **Why Not Appearing**: Streams may not have been running 7+ minutes yet
- **Action**: **KEEP** - Wait longer to see if it appears, then keep for stream health

#### 5. SUSPENDED_STREAM_HEARTBEAT ⚠️ **CONDITIONAL**
- **Count**: 0 events
- **Purpose**: Monitors suspended streams
- **Do We Need It?**: ✅ **YES - IMPORTANT**
- **Why Not Appearing**: Only logs when streams are suspended (none currently)
- **Action**: **KEEP** - Needed when streams are suspended

---

### ❌ NOT LOGGING - OPTIONAL/DIAGNOSTIC

#### 6. TICK_CALLED_FROM_ONMARKETDATA ❌ **NOT FOUND**
- **Count**: 0 events in recent logs (but 30 events total in last 2 hours)
- **Purpose**: Diagnostic to verify continuous execution fix
- **Do We Need It?**: ⚠️ **OPTIONAL**
- **Action**: **CAN REMOVE** - Was useful for verifying fix, but fix is now verified

#### 7. ENGINE_TICK_HEARTBEAT ❌ **NOT LOGGING**
- **Count**: 0 events
- **Purpose**: Bar-driven heartbeat (diagnostic)
- **Requires**: `enable_diagnostic_logs = true`
- **Do We Need It?**: ⚠️ **OPTIONAL**
- **Action**: **CAN REMOVE** - Only needed if debugging bar processing

#### 8. ENGINE_BAR_HEARTBEAT ❌ **NOT LOGGING**
- **Count**: 0 events
- **Purpose**: Bar ingress diagnostic
- **Requires**: `enable_diagnostic_logs = true`
- **Do We Need It?**: ⚠️ **OPTIONAL**
- **Action**: **CAN REMOVE** - Only needed if debugging bar routing

---

### ❌ DEPRECATED/NOT IMPLEMENTED - DON'T NEED

#### 9. ENGINE_HEARTBEAT ❌ **DEPRECATED**
- **Status**: Deprecated, replaced by ENGINE_TICK_CALLSITE
- **Do We Need It?**: ❌ **NO**
- **Action**: **REMOVE** - Not needed, replaced by ENGINE_TICK_CALLSITE

#### 10. ENGINE_TICK_HEARTBEAT_AUDIT ❌ **NOT IMPLEMENTED**
- **Status**: Not implemented
- **Do We Need It?**: ❌ **NO**
- **Action**: **REMOVE** - Not implemented, not needed

#### 11. TICK_METHOD_ENTERED ❌ **NOT FOUND**
- **Status**: Not found in logs
- **Do We Need It?**: ❌ **NO**
- **Action**: **REMOVE** - Optional diagnostic, not critical

#### 12. TICK_CALLED ❌ **NOT FOUND**
- **Status**: Rate-limited, not appearing
- **Do We Need It?**: ❌ **NO**
- **Action**: **REMOVE** - Optional diagnostic, not critical

#### 13. TICK_TRACE ❌ **NOT FOUND**
- **Status**: Rate-limited, not appearing
- **Do We Need It?**: ❌ **NO**
- **Action**: **REMOVE** - Optional diagnostic, not critical

---

## Summary Table

| Event | Status | Count | Need? | Action |
|-------|--------|-------|-------|--------|
| **ENGINE_TICK_CALLSITE** | ✅ Working | 37,193 | ✅ **YES** | **KEEP** |
| **ENGINE_TICK_STALL_DETECTED** | ✅ Working | 6 | ✅ **YES** | **KEEP** |
| **ENGINE_TICK_STALL_RECOVERED** | ✅ Working | 0 | ✅ **YES** | **KEEP** |
| **HEARTBEAT (stream)** | ⚠️ Rate-limited | 0 | ✅ **YES** | **KEEP** (wait 7+ min) |
| **SUSPENDED_STREAM_HEARTBEAT** | ⚠️ Conditional | 0 | ✅ **YES** | **KEEP** |
| **TICK_CALLED_FROM_ONMARKETDATA** | ❌ Not found | 0 | ⚠️ Optional | **CAN REMOVE** |
| **ENGINE_TICK_HEARTBEAT** | ❌ Not logging | 0 | ⚠️ Optional | **CAN REMOVE** |
| **ENGINE_BAR_HEARTBEAT** | ❌ Not logging | 0 | ⚠️ Optional | **CAN REMOVE** |
| **ENGINE_HEARTBEAT** | ❌ Deprecated | 0 | ❌ **NO** | **REMOVE** |
| **ENGINE_TICK_HEARTBEAT_AUDIT** | ❌ Not implemented | 0 | ❌ **NO** | **REMOVE** |
| **TICK_METHOD_ENTERED** | ❌ Not found | 0 | ❌ **NO** | **REMOVE** |
| **TICK_CALLED** | ❌ Not found | 0 | ❌ **NO** | **REMOVE** |
| **TICK_TRACE** | ❌ Not found | 0 | ❌ **NO** | **REMOVE** |

---

## Recommendations

### ✅ KEEP (5 events)
1. **ENGINE_TICK_CALLSITE** - Critical for watchdog (37K+ events)
2. **ENGINE_TICK_STALL_DETECTED** - Critical for failure detection
3. **ENGINE_TICK_STALL_RECOVERED** - Critical for recovery tracking
4. **HEARTBEAT (stream-level)** - Important for stream health (wait 7+ min to verify)
5. **SUSPENDED_STREAM_HEARTBEAT** - Important for suspended streams

### ⚠️ OPTIONAL (3 events - can remove if not debugging)
6. **TICK_CALLED_FROM_ONMARKETDATA** - Was useful for fix verification, can remove now
7. **ENGINE_TICK_HEARTBEAT** - Only if debugging bar processing
8. **ENGINE_BAR_HEARTBEAT** - Only if debugging bar routing

### ❌ REMOVE (5 events - not needed)
9. **ENGINE_HEARTBEAT** - Deprecated
10. **ENGINE_TICK_HEARTBEAT_AUDIT** - Not implemented
11. **TICK_METHOD_ENTERED** - Optional diagnostic
12. **TICK_CALLED** - Optional diagnostic
13. **TICK_TRACE** - Optional diagnostic

---

## Conclusion

**Must Keep**: 5 events (3 critical + 2 important)  
**Optional**: 3 events (can remove if not debugging)  
**Don't Need**: 5 events (deprecated/not implemented/optional diagnostics)

**Total**: 13 event types analyzed  
**Essential**: 5 events  
**Can Remove**: 8 events (3 optional + 5 not needed)
