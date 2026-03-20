# Task: Wire TimetableCache into RobotEngine (Tightly Scoped)

**Date:** 2026-03-19  
**Priority:** P1 — Confirmed live contention point (7 engines, direct reads, WinError 32)  
**Prerequisite:** Operational rule: no resequence during trading hours (define and enforce first)

---

## 1. Target Behavior

| Before | After |
|--------|-------|
| Each engine: `TimetableFilePoller.Poll` → `File.ReadAllBytes` + `LoadFromFile` (2 reads) | Each engine: `TimetableCache.GetOrLoad` → 1 shared read path |
| 7 engines × 2 reads = up to 14 reads per poll cycle | 1 read per poll cycle (first engine); others hit cache |
| Per-engine `_lastHash` in TimetableFilePoller | Per-engine `_lastTimetableHash` in RobotEngine (unchanged) |
| Direct file I/O on every poll | In-memory cache; disk read only on cache miss |

**Success criteria:**
- No engine-level direct `File.ReadAllBytes` or `LoadFromFile` for timetable after integration
- Poll interval gating preserved (e.g. 10s)
- `ReloadTimetableIfChanged` behavior unchanged (same inputs: `FilePollResult`, timetable, parseException)
- All call sites of `PollAndParseTimetable` continue to work

---

## 2. Files to Modify

| File | Change |
|------|--------|
| `RobotCore_For_NinjaTrader/RobotEngine.cs` | Replace `PollAndParseTimetable` implementation to use `TimetableCache.GetOrLoad` |
| `RobotCore_For_NinjaTrader/TimetableFilePoller.cs` | Add `MarkPolled(utcNow)` to update `_lastPollUtc` without I/O (or equivalent) |

**No changes to:** `TimetableCache.cs`, `ReloadTimetableIfChanged`, `RobotSimStrategy`, `FilePollResult`.

---

## 3. Current Flow (to Replace)

```csharp
// RobotEngine.cs - PollAndParseTimetable (lines 3337-3356)
private (FilePollResult Poll, TimetableContract? Timetable, Exception? ParseException) PollAndParseTimetable(DateTimeOffset utcNow)
{
    var poll = _timetablePoller.Poll(_timetablePath, utcNow);  // 1. File.Exists + File.ReadAllBytes + hash
    if (poll.Error is not null)
        return (poll, null, null);
    try
    {
        var timetable = TimetableContract.LoadFromFile(_timetablePath);  // 2. SECOND read of same file
        return (poll, timetable, null);
    }
    catch (Exception ex)
    {
        return (poll, null, ex);
    }
}
```

**Problem:** Two reads per engine per poll. `TimetableFilePoller.Poll` reads; `LoadFromFile` reads again.

---

## 4. New Flow (Implementation)

### 4.1 TimetableFilePoller — Add `MarkPolled`

```csharp
/// <summary>Mark that a poll occurred (updates interval). Use when polling via TimetableCache.</summary>
public void MarkPolled(DateTimeOffset utcNow)
{
    _lastPollUtc = utcNow;
}
```

Keep `ShouldPoll` unchanged. `Poll` can remain for backward compatibility but will not be used by RobotEngine after this change.

### 4.2 RobotEngine — Replace `PollAndParseTimetable`

```csharp
private (FilePollResult Poll, TimetableContract? Timetable, Exception? ParseException) PollAndParseTimetable(DateTimeOffset utcNow)
{
    _timetablePoller.MarkPolled(utcNow);

    if (!File.Exists(_timetablePath))
        return (new FilePollResult(false, null, "MISSING"), null, null);

    var (hash, timetable, changed) = TimetableCache.GetOrLoad(_timetablePath, _lastTimetableHash);

    // Parse error: GetOrLoad returns (rawHash, null, true) when LoadFromBytes fails
    if (timetable is null && hash is not null)
        return (new FilePollResult(true, hash, "PARSE_ERROR"), null, new InvalidOperationException("Timetable parse failed"));
    if (timetable is null && hash is null)
        return (new FilePollResult(false, null, "MISSING"), null, null);

    return (new FilePollResult(changed, hash, null), timetable, null);
}
```

**Note:** `TimetableCache.GetOrLoad` returns `(null, null, false)` when file is missing. We already check `File.Exists` before calling. When parse fails, it returns `(rawHash, null, true)` — so we need to map that to `FilePollResult` with `Error = "PARSE_ERROR"` and a non-null hash. Check `TimetableCache` implementation for exact behavior on parse failure.

**Re-check TimetableCache on parse failure:**
```csharp
catch
{
    var rawHash = Sha256Hex(bytes);
    return (rawHash, null, true);  // hash is rawHash, timetable is null, changed is true
}
```
So we get `(hash, null, true)`. We should return `FilePollResult(changed: true, hash, "PARSE_ERROR")` and `parseException` can be a generic one or we could try to capture it — but TimetableCache swallows the exception. So we use a generic parse exception.

**Also:** We must not update `_lastTimetableHash` in PollAndParseTimetable — that's done in `ReloadTimetableIfChanged` when we actually apply the timetable. So we pass `_lastTimetableHash` to GetOrLoad for the `changed` computation. Good.

### 4.3 Edge Cases

| Case | TimetableCache return | Map to |
|------|------------------------|--------|
| File missing | (null, null, false) | Check `File.Exists` first → (MISSING, null, null) |
| Parse error | (rawHash, null, true) | (PARSE_ERROR, hash, parseException) |
| Success | (hash, timetable, changed) | (poll with changed, timetable, null) |

---

## 5. Verification

1. **Build:** `dotnet build` for RobotCore_For_NinjaTrader and NT_ADDONS (if applicable)
2. **Harness:** Run robot harness; verify timetable loads and streams created
3. **No regression:** `ReloadTimetableIfChanged` logic unchanged; same events (TIMETABLE_INVALID, etc.)
4. **Cache behavior:** With 7 engines, first poll after interval does 1 read; subsequent engines in same cycle hit cache (if file unchanged)

---

## 6. Rollout Order (from Assessment)

1. **Operational rule:** No resequence during trading hours — define and enforce first
2. **This task:** Wire TimetableCache into RobotEngine
3. **Re-test:** Disconnect behavior under normal trading load
4. **If still unstable:** Journal I/O optimization
5. **If still needed:** Separate machine/VPS for pipeline

---

## 7. Decision Standard (Post-Implementation)

After deployment, ask:
- Do disconnects still cluster around pipeline activity?
- Do timetable write-contention errors (WinError 32) disappear?
- Does robot responsiveness improve during matrix activity?
- Do live and sim still cascade close together?

**Target:** Yes, yes, yes, no.

---

## 8. Out of Scope (This Task)

- Background poller (single poll thread for entire process) — future enhancement
- Journal I/O optimization
- Matrix pipeline scheduling
- Separate machine for pipeline
