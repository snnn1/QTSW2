# Matrix Deletion Plan
**Date:** 2025  
**Status:** Active - First Deletion Complete, Framework Ready

---

## Purpose

This document identifies subsystems/modes that can be safely deleted based on:
- Usage log analysis (instrumentation data)
- Contract audit findings (`MATRIX_DEFENSIVE_LOGIC_AUDIT.md`)
- Golden test verification

---

## Deletion Candidates

Based on audit findings (`MATRIX_DEFENSIVE_LOGIC_AUDIT.md`) and usage analysis.

### Candidate 1: Stream Filtering Safety Check (REDUNDANT)

**Location:** `modules/matrix/master_matrix.py` lines 295-297

**Evidence:**
- Audit: REDUNDANT - Sequencer logic already filters by stream list
- Usage logs: [To be analyzed]
- Contract: Sequencer guarantees stream filtering

**Deletion Steps:**
1. Remove redundant stream filter check in `_rebuild_partial()`
2. Verify sequencer logic handles stream filtering correctly

**Expected Blast Radius:**
- No impact - sequencer already filters streams
- ~3 lines removed

**Verification:**
- Golden tests must pass
- Partial rebuild tests must pass

---

### Candidate 2: Date Repair Strategies 1-3 (CONDITIONAL)

**Location:** `modules/matrix/master_matrix.py` lines 676-716 (Strategy 1-3)

**Evidence:**
- Audit: CONDITIONAL - Removable once analyzer enforces valid dates
- Contract: Analyzer guarantees valid dates (Invariant 1)
- Current: Compensates for invalid/malformed dates

**Deletion Steps:**
1. Remove Strategy 1 (format parsing) - lines 676-688
2. Remove Strategy 2 (stream median inference) - lines 697-709
3. Remove Strategy 3 (global fallback) - lines 711-716
4. Keep sentinel date preservation (required for sorting)

**Expected Blast Radius:**
- No impact on contract-valid data
- Invalid data will fail fast (as intended)
- ~40 lines removed

**Prerequisites:**
- AnalyzerOutputValidator enforces Invariant 1 (Valid Date Column)

**Verification:**
- Golden tests must pass
- Invalid date data should fail fast

---

### Candidate 3: Missing Column Defaults (CONDITIONAL)

**Location:** `modules/matrix/schema_normalizer.py` lines 68-82

**Evidence:**
- Audit: CONDITIONAL - Removable once analyzer enforces consistent schema
- Contract: Analyzer guarantees required schema (Invariant 3)
- Current: Adds missing columns with defaults

**Deletion Steps:**
1. Remove missing required column defaults
2. Remove missing optional column defaults
3. Keep derived column creation (business logic)

**Expected Blast Radius:**
- No impact on contract-valid data
- Missing columns will fail fast
- ~15 lines removed

**Prerequisites:**
- AnalyzerOutputValidator enforces Invariant 3 (Required Schema)

**Verification:**
- Golden tests must pass
- Missing column data should fail fast

---

### Candidate 4: None Value Checking (CONDITIONAL)

**Location:** `modules/matrix/master_matrix.py` lines 795-806

**Evidence:**
- Audit: CONDITIONAL - Removable once analyzer enforces non-None sort columns
- Contract: Analyzer guarantees non-None sort columns (Invariant 6)
- Current: Checks and logs None values before sorting

**Deletion Steps:**
1. Remove None checking logic (lines 795-806)
2. Keep sentinel filling logic (required for sorting)

**Expected Blast Radius:**
- No impact on contract-valid data
- None values will fail fast
- ~12 lines removed

**Prerequisites:**
- AnalyzerOutputValidator enforces Invariant 6 (Non-None Sort Columns)

**Verification:**
- Golden tests must pass
- None value data should fail fast

---

## Execution Summary

### Deleted Subsystems

| Subsystem | Lines Removed | Reason | Date | Verification |
|-----------|---------------|--------|------|-------------|
| Stream Filtering Safety Check | 3 | REDUNDANT - Sequencer already filters | 2025-01-05 | Golden test PASS |

### Verification Results

- **Golden tests:** ALL PASSING
  - Full rebuild: `6e8aae0b986817fe5ab1801f9f0fdc8ee8367a297f99827c6d2e487267290080`
  - Authoritative rebuild: `6e8aae0b986817fe5ab1801f9f0fdc8ee8367a297f99827c6d2e487267290080`
  - Partial rebuild: `40e6bd12d08884fca8445e2b33a8a5d97fe6f8685ddf6b42f78dbc30b9d7027f`
- **Existing test suite:** [To be run]
- **Usage logs:** Generated during test runs

### Deletion Details

**Commit 1: Remove Redundant Stream Filtering Safety Check**
- **File:** `modules/matrix/master_matrix.py`
- **Lines removed:** 145-147 (redundant filter check)
- **Replaced with:** Comment explaining sequencer already filters
- **Impact:** None - sequencer logic guarantees stream filtering
- **Verification:** Golden test passes, hash unchanged

---

**Last Updated:** 2025
