# Critical Policy Decisions for DataManager

## ⚠️ PENDING USER DECISIONS

This document outlines critical policy decisions that must be made before the DataManager can be considered production-ready. These decisions affect data integrity, trade sequencing, and PnL distribution.

---

## ❗ CRITICAL ISSUE 1: Duplicate Removal Strategy

**Current Assumption:** `keep="first"` (first occurrence kept)

**Impact:**
- Trade sequencing
- Trade timing
- Intra-bar simulation
- Entire PnL distribution

**Options:**

### Option A: Keep First (Current)
```python
df.drop_duplicates(subset=["timestamp", "instrument"], keep="first")
```
- **Pros:** Deterministic, preserves earliest data
- **Cons:** May lose more recent/correct data
- **Use Case:** When first data point is most reliable

### Option B: Keep Last
```python
df.drop_duplicates(subset=["timestamp", "instrument"], keep="last")
```
- **Pros:** Preserves most recent data (corrections/updates)
- **Cons:** May lose original data
- **Use Case:** When data gets corrected/updated over time

### Option C: Keep Earliest Non-Null OHLC
```python
# Custom logic: prefer row with most complete OHLC data
```
- **Pros:** Preserves data quality
- **Cons:** More complex, may be slower
- **Use Case:** When data completeness is priority

### Option D: Error if Duplicates Exist
```python
if duplicates_exist:
    raise ValueError("Duplicate timestamps found - manual resolution required")
```
- **Pros:** Forces explicit data quality decisions
- **Cons:** Stops processing, requires manual intervention
- **Use Case:** When data quality is critical and duplicates indicate problems

**YOUR DECISION:** [ ] Option A  [ ] Option B  [ ] Option C  [ ] Option D  [ ] Other: _________

---

## ❗ CRITICAL ISSUE 2: Synthetic Bar Flagging

**Current State:** DataManager does NOT flag synthetic bars

**Required:** Explicit `synthetic` boolean column

**Why:**
- Prevent using synthetic bars for range detection
- Avoid accidental trades in synthetic data
- Debugging and performance attribution
- Data lineage tracking

**Implementation Required:**
```python
def reconstruct_missing_bars(self, df, ...):
    # ... reconstruction logic ...
    df_reindexed['synthetic'] = df_reindexed['synthetic'].fillna(False)  # Original bars
    df_reindexed.loc[df_reindexed['synthetic'].isna(), 'synthetic'] = True  # Synthetic bars
    return df_reindexed
```

**YOUR DECISION:** 
- [ ] Add `synthetic` column (True for reconstructed bars, False for original)
- [ ] Column name preference: `synthetic` / `is_synthetic` / `reconstructed` / Other: _________
- [ ] Should synthetic bars be excluded from range detection? [ ] Yes  [ ] No
- [ ] Should synthetic bars be excluded from trade execution? [ ] Yes  [ ] No

---

## ❗ CRITICAL ISSUE 3: OHLC Fixing Policy

**Current Assumption:** Option A (Repair deterministically)

**Impact:** Silent data distortion if wrong policy chosen

**Options:**

### Option A: Repair Deterministically (Current)
- Swap high/low if `high < low`
- Clip open/close to [low, high] bounds
- **Pros:** Data always usable, no rows lost
- **Cons:** May mask data quality issues, introduces synthetic corrections
- **Use Case:** When data must be processed and minor errors are acceptable

### Option B: Mark Invalid Rows and Skip Them
```python
df['invalid_ohlc'] = (high < low) | (open < low) | (close > high) | ...
df_valid = df[~df['invalid_ohlc']]
```
- **Pros:** Preserves original data, explicit quality tracking
- **Cons:** May lose important data points
- **Use Case:** When data quality is more important than completeness

### Option C: Raise Exception
```python
if invalid_ohlc_exists:
    raise ValueError("Invalid OHLC relationships found - data quality issue")
```
- **Pros:** Forces explicit data quality decisions
- **Cons:** Stops processing, requires manual intervention
- **Use Case:** When data quality is critical and errors indicate problems

**YOUR DECISION:** [ ] Option A  [ ] Option B  [ ] Option C  [ ] Other: _________

---

## ⚠️ MEDIUM ISSUE 4: Timezone Localization Policy

**Current Assumption:** Naive timestamps → Chicago timezone

**Risk:** If raw data sometimes contains naive UTC, this will cause incorrect timezone assignment

**Options:**

### Option A: Assume Naive = Chicago (Current)
```python
df['timestamp'] = df['timestamp'].dt.tz_localize(CHICAGO_TZ)
```
- **Pros:** Simple, works if data is always Chicago
- **Cons:** Wrong if data is sometimes UTC or other timezone
- **Use Case:** When you're certain all naive timestamps are Chicago

### Option B: Treat Naive as Error
```python
if has_naive_timestamps:
    raise ValueError("Naive timestamps found - timezone must be explicit")
```
- **Pros:** Forces explicit timezone specification
- **Cons:** Stops processing, requires manual intervention
- **Use Case:** When timezone ambiguity is unacceptable

### Option C: User-Defined Default Timezone
```python
def __init__(self, default_timezone=CHICAGO_TZ):
    self.default_timezone = default_timezone
```
- **Pros:** Flexible, explicit
- **Cons:** Requires user to know correct default
- **Use Case:** When data sources have different timezones

**YOUR DECISION:** 
- [ ] Option A (Assume Chicago)
- [ ] Option B (Error on naive)
- [ ] Option C (User-defined default)
- [ ] Other: _________

---

## ⚠️ MEDIUM ISSUE 5: Missing Bar Fill Method

**Current Assumption:** Forward fill

**Options:**

### Option A: Forward Fill (Current)
```python
df_reindexed = df_reindexed.ffill()
```
- **Pros:** Standard, simple, preserves last known value
- **Cons:** May create flat periods, doesn't reflect market movement
- **Use Case:** When last known price is best estimate

### Option B: Midpoint Fill
```python
df_reindexed['close'] = (df_reindexed['high'] + df_reindexed['low']) / 2
```
- **Pros:** More realistic for missing data
- **Cons:** Assumes price was at midpoint (may not be true)
- **Use Case:** When you want to avoid flat periods

### Option C: Linear Interpolation
```python
df_reindexed[numeric_cols] = df_reindexed[numeric_cols].interpolate(method='linear')
```
- **Pros:** Smooth transitions, more realistic
- **Cons:** May create prices that never existed
- **Use Case:** When smooth price movement is important

### Option D: Brownian Bridge (Random Path)
```python
# Generate realistic price path between known points
```
- **Pros:** Most realistic for simulation
- **Cons:** Non-deterministic, complex
- **Use Case:** When you need realistic price paths for backtesting

### Option E: LastCloseAcrossGaps
```python
# Use last close for all OHLC in gap
df_reindexed['open'] = df_reindexed['close'].ffill()
df_reindexed['high'] = df_reindexed['close'].ffill()
df_reindexed['low'] = df_reindexed['close'].ffill()
```
- **Pros:** Conservative, no synthetic price movement
- **Cons:** Creates flat periods
- **Use Case:** When you want to avoid any synthetic price movement

**YOUR DECISION:** 
- [ ] Option A (Forward Fill)
- [ ] Option B (Midpoint Fill)
- [ ] Option C (Linear Interpolation)
- [ ] Option D (Brownian Bridge)
- [ ] Option E (LastCloseAcrossGaps)
- [ ] Other: _________

---

## ⚠️ MEDIUM ISSUE 6: DST Test Determinism

**Current State:** Flexible time difference window (60-3700 seconds)

**Problem:** Too loose, could let errors slip through

**Required:** Explicit deterministic DST test

**Proposed Fix:**
```python
def test_dst_correctness(self, chicago_timestamp):
    # Create timestamps around DST transition
    # 1:59 AM CST -> 3:00 AM CDT (Spring forward)
    # Expected: 1 hour + 1 minute = 3660 seconds
    
    # OR if timestamps are already timezone-aware:
    # Expected: 1 minute = 60 seconds (clock time difference)
    
    # Need explicit expected value based on your DST handling policy
```

**YOUR DECISION:**
- [ ] DST test should expect exact 3660 seconds (1 hour + 1 minute)
- [ ] DST test should expect exact 60 seconds (1 minute clock time)
- [ ] DST test should verify timestamps remain sorted and valid (current)
- [ ] Other: _________

---

## Summary

Please review each decision above and mark your choices. Once confirmed, I will:

1. Update DataManager implementation to match your policies
2. Update all tests to reflect your policies
3. Add explicit validation and error handling
4. Document the policies in code comments
5. Ensure all behavior is deterministic and testable

**Status:** ⏸️ **BLOCKED - Awaiting Policy Decisions**









