# Watchdog Complexity Analysis

**Date:** 2026-01-26  
**Purpose:** Identify removable code, simplify complexity, and reduce maintenance burden

## Executive Summary

The watchdog system has grown organically and contains several areas that can be simplified or removed:

1. **Dead Code:** `websocket_minimal.py` (116 lines) - not imported anywhere
2. **Complex Files:** `websocket.py` (586 lines), `WebSocketContext.tsx` (545 lines)
3. **Unused Routes:** SummaryPage and JournalPage may be unused
4. **Duplicate Logic:** Some error handling patterns repeated across files

## Backend Analysis

### File Size Breakdown

| File | Lines | Functions | Classes | Status |
|------|-------|-----------|---------|--------|
| `websocket.py` | 586 | 3 | 0 | **COMPLEX** - Consider splitting |
| `websocket_minimal.py` | 116 | 2 | 0 | **DEAD CODE** - Remove |
| `watchdog.py` | 279 | 15+ | 0 | Moderate |
| `aggregator.py` | 492 | 10+ | 1 | Moderate |
| `event_processor.py` | 442 | 20+ | 1 | Moderate |
| `state_manager.py` | ~400 | 15+ | 2 | Moderate |
| `event_feed.py` | ~300 | 5+ | 1 | Simple |

### 1. Dead Code: `websocket_minimal.py` ⚠️ **REMOVE**

**Location:** `modules/watchdog/backend/routers/websocket_minimal.py`

**Status:** Not imported anywhere - completely unused

**Evidence:**
- Not imported in `main.py` (only imports `websocket`, not `websocket_minimal`)
- Not referenced in `__init__.py`
- No grep matches for `websocket_minimal` in codebase

**Action:** Delete this file (116 lines saved)

**Risk:** None - it's completely unused

---

### 2. Complex File: `websocket.py` (586 lines) 🔴 **SIMPLIFY**

**Location:** `modules/watchdog/backend/routers/websocket.py`

**Issues:**
- Single file handles: snapshot sending, streaming, heartbeat, error handling, connection tracking
- Multiple nested try/except blocks
- Complex file reading logic (backwards reading for large files)
- Duplicate error handling patterns

**Recommendations:**

#### Option A: Split into modules (Recommended)
```
websocket/
  ├── __init__.py          # Export main router
  ├── handler.py          # Main websocket_events handler
  ├── snapshot.py         # Snapshot sending logic
  ├── streaming.py        # Event streaming logic
  └── heartbeat.py        # Heartbeat logic
```

#### Option B: Extract helpers
- Move `_read_feed_events_since()` to `event_feed.py` (already reads feed)
- Move `_send_snapshot()` to separate module
- Simplify error handling with shared utilities

**Complexity Reduction:** ~200-300 lines could be extracted

---

### 3. Duplicate File Reading Logic

**Issue:** Both `websocket.py` and `event_feed.py` read `frontend_feed.jsonl`

**Files:**
- `websocket.py:_read_feed_events_since()` - reads feed for streaming
- `websocket.py:_send_snapshot()` - reads feed for snapshot (optimized backwards reading)
- `event_feed.py` - reads feed for processing

**Recommendation:** 
- Consolidate feed reading into `event_feed.py`
- Expose a shared `read_feed_events()` function
- Remove duplicate reading logic from `websocket.py`

**Complexity Reduction:** ~50-100 lines

---

### 4. Overly Complex Error Handling

**Issue:** Similar error handling patterns repeated across files

**Patterns Found:**
- `CancelledError` handling (repeated 3+ times)
- `WebSocketDisconnect` / `ClientDisconnected` handling (repeated 5+ times)
- Connection state checking (repeated 10+ times)

**Recommendation:**
Create `modules/watchdog/websocket_utils.py`:
```python
def is_connection_closed_error(error: Exception) -> bool:
    """Check if error indicates connection is closed."""
    ...

def handle_websocket_error(error: Exception, phase: str) -> bool:
    """Handle WebSocket error, return True if should break loop."""
    ...
```

**Complexity Reduction:** ~50-100 lines of duplicate code

---

## Frontend Analysis

### File Size Breakdown

| File | Lines | Components | Status |
|------|-------|------------|--------|
| `WebSocketContext.tsx` | 545 | 1 | **COMPLEX** - Consider splitting |
| `WatchdogPage.tsx` | ~200 | 1 | Moderate |
| `SummaryPage.tsx` | ? | 1 | **CHECK USAGE** |
| `JournalPage.tsx` | ? | 1 | **CHECK USAGE** |

### 1. Complex File: `WebSocketContext.tsx` (545 lines) 🔴 **SIMPLIFY**

**Issues:**
- Single file handles: connection, reconnection, event deduplication, subscription system
- Complex lifecycle management (StrictMode guards, tab visibility, cleanup)
- Multiple refs and state management

**Recommendations:**

#### Option A: Split into hooks
```
hooks/
  ├── useWebSocket.ts          # Core WebSocket connection
  ├── useWebSocketReconnect.ts # Reconnection logic
  ├── useWebSocketEvents.ts    # Event handling/deduplication
  └── useWebSocketSubscriptions.ts # Subscription system
```

#### Option B: Extract utilities
- Move reconnection logic to `utils/websocketReconnect.ts`
- Move event deduplication to `utils/eventDeduplication.ts`
- Simplify lifecycle management

**Complexity Reduction:** ~200-300 lines could be extracted

---

### 2. Pages: SummaryPage & JournalPage ✅ **KEEP**

**Location:** 
- `modules/watchdog/frontend/src/SummaryPage.tsx` (148 lines)
- `modules/watchdog/frontend/src/JournalPage.tsx` (118 lines)

**Status:** Functional pages with legitimate features
- SummaryPage: Daily summary with P&L, execution stats, intent breakdown
- JournalPage: Execution journal with filters for trading date, stream, intent ID

**Action:** Keep - these are core features, not dead code

---

### 3. Duplicate Event Deduplication Logic

**Issue:** `seenEventsRef` deduplication logic could be extracted

**Location:** `WebSocketContext.tsx`

**Recommendation:**
Create `utils/eventDeduplication.ts`:
```typescript
export function useEventDeduplication(maxEvents: number) {
  // Extract deduplication logic
}
```

**Complexity Reduction:** ~30-50 lines

---

## Dependency Analysis

### Unused Imports

**Check for:**
- Unused React hooks
- Unused utility functions
- Unused type definitions

**Action:** Run ESLint/TypeScript unused import checks

---

## Recommendations Priority

### 🔴 High Priority (Immediate Action)

1. **Remove `websocket_minimal.py`** (116 lines)
   - Zero risk - completely unused
   - Immediate complexity reduction
   - **Action:** Delete file, verify no imports

### 🟡 Medium Priority (Next Sprint)

3. **Extract WebSocket error handling utilities**
   - Reduces duplication
   - Improves maintainability
   - ~50-100 lines saved

4. **Consolidate feed reading logic**
   - Removes duplication
   - Single source of truth
   - ~50-100 lines saved

### 🟢 Low Priority (Future Refactoring)

5. **Split `websocket.py` into modules**
   - Large refactor
   - Requires testing
   - ~200-300 lines reorganized (not removed)

6. **Split `WebSocketContext.tsx` into hooks**
   - Large refactor
   - Requires testing
   - ~200-300 lines reorganized (not removed)

---

## Complexity Metrics

### Current State
- **Total Backend Python:** ~2,500 lines
- **Total Frontend TypeScript/TSX:** ~1,500 lines (excluding node_modules)
- **Dead Code:** ~116 lines (websocket_minimal.py)
- **Potential Removals:** ~200-500 lines (if pages unused)

### After Cleanup (High Priority Only)
- **Dead Code Removed:** 116 lines
- **Total Reduction:** ~116 lines (~5% reduction)
- **Future Refactoring Potential:** ~300-500 lines could be reorganized (not removed)

---

## Implementation Plan

### Phase 1: Dead Code Removal (1 hour)
1. Delete `websocket_minimal.py`
2. Verify no imports reference it
3. Test watchdog still works

### Phase 2: Error Handling Extraction (2-3 hours)
1. Create `websocket_utils.py`
2. Extract common error handling patterns
3. Refactor `websocket.py` to use utilities
4. Test thoroughly

### Phase 3: Feed Reading Consolidation (2-3 hours)
1. Move feed reading to `event_feed.py`
2. Update `websocket.py` to use shared functions
3. Test snapshot and streaming

---

## Testing Checklist

After each phase:
- [ ] WebSocket connects successfully
- [ ] Snapshot loads correctly
- [ ] Streaming events work
- [ ] Heartbeat continues
- [ ] Reconnection works
- [ ] No console errors
- [ ] Backend logs are clean

---

## Notes

- This analysis focuses on **removable** code, not just refactoring
- Complexity reduction should maintain functionality
- Test thoroughly after each change
- Consider creating unit tests for extracted utilities
