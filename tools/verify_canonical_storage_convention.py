"""
Verify Canonical Storage Convention

HARD RULE: All persisted historical data must be stored by canonical instrument, never execution instrument.

This script verifies:
1. Translator outputs (data/translated/{canonical}/...)
2. Analyzer outputs (Instrument column in parquet files)
3. Sequencer outputs (Instrument column in parquet files)
4. Merger outputs (data/analyzed/{canonical}{session}/...)
5. Snapshot files (data/translated_test/{canonical}/...)

If this convention holds:
- SIM hydration ✔
- DRYRUN hydration ✔
- Analyzer parity ✔
- Matrix parity ✔

If it doesn't:
- DRYRUN will miss files (SIM will still work)
"""

import json
import pandas as pd
from pathlib import Path
from typing import Dict, List, Tuple, Set
from collections import defaultdict

# Canonical instrument mapping (execution -> canonical)
CANONICAL_MAP = {
    "MES": "ES",
    "MNQ": "NQ",
    "MYM": "YM",
    "MCL": "CL",
    "MNG": "NG",
    "MGC": "GC",
    "M2K": "RTY",
}

# All canonical instruments
CANONICAL_INSTRUMENTS = {"ES", "NQ", "YM", "CL", "NG", "GC", "RTY"}

# All execution instruments (micros)
EXECUTION_INSTRUMENTS = set(CANONICAL_MAP.keys())

def get_canonical(instrument: str) -> str:
    """Get canonical instrument for a given execution instrument."""
    return CANONICAL_MAP.get(instrument.upper(), instrument.upper())

def verify_translator_outputs(project_root: Path) -> Tuple[bool, List[str]]:
    """Verify translator outputs use canonical instrument names in paths."""
    issues = []
    translated_dir = project_root / "data" / "translated"
    
    if not translated_dir.exists():
        return True, []  # No data to verify
    
    # Check all instrument directories
    for instrument_dir in translated_dir.iterdir():
        if not instrument_dir.is_dir():
            continue
        
        instrument = instrument_dir.name.upper()
        
        # Check if this is an execution instrument (micro)
        if instrument in EXECUTION_INSTRUMENTS:
            canonical = get_canonical(instrument)
            issues.append(
                f"[FAIL] Translator output uses execution instrument '{instrument}' "
                f"instead of canonical '{canonical}': {instrument_dir}"
            )
    
    return len(issues) == 0, issues

def verify_analyzer_outputs(project_root: Path) -> Tuple[bool, List[str]]:
    """Verify analyzer outputs use canonical instrument names in Instrument column."""
    issues = []
    
    # Check analyzer_temp
    analyzer_temp_dir = project_root / "data" / "analyzer_temp"
    if analyzer_temp_dir.exists():
        for parquet_file in analyzer_temp_dir.rglob("*.parquet"):
            try:
                df = pd.read_parquet(parquet_file)
                if "Instrument" in df.columns:
                    instruments = df["Instrument"].str.upper().unique()
                    for inst in instruments:
                        if inst in EXECUTION_INSTRUMENTS:
                            canonical = get_canonical(inst)
                            issues.append(
                                f"[FAIL] Analyzer output contains execution instrument '{inst}' "
                                f"instead of canonical '{canonical}' in Instrument column: {parquet_file}"
                            )
            except Exception as e:
                issues.append(f"[WARN] Could not read analyzer file {parquet_file}: {e}")
    
    # Check manual_analyzer_runs
    manual_runs_dir = project_root / "data" / "manual_analyzer_runs"
    if manual_runs_dir.exists():
        for parquet_file in manual_runs_dir.rglob("*.parquet"):
            try:
                df = pd.read_parquet(parquet_file)
                if "Instrument" in df.columns:
                    instruments = df["Instrument"].str.upper().unique()
                    for inst in instruments:
                        if inst in EXECUTION_INSTRUMENTS:
                            canonical = get_canonical(inst)
                            issues.append(
                                f"[FAIL] Analyzer output contains execution instrument '{inst}' "
                                f"instead of canonical '{canonical}' in Instrument column: {parquet_file}"
                            )
            except Exception as e:
                issues.append(f"[WARN] Could not read analyzer file {parquet_file}: {e}")
    
    return len(issues) == 0, issues

def verify_merger_outputs(project_root: Path) -> Tuple[bool, List[str]]:
    """Verify merger outputs use canonical instrument names in paths and Instrument column."""
    issues = []
    analyzed_dir = project_root / "data" / "analyzed"
    
    if not analyzed_dir.exists():
        return True, []
    
    # Check directory names (should be {canonical}{session})
    for instrument_session_dir in analyzed_dir.iterdir():
        if not instrument_session_dir.is_dir():
            continue
        
        dir_name = instrument_session_dir.name.upper()
        
        # Extract instrument (remove session suffix: 1 or 2)
        if dir_name.endswith("1") or dir_name.endswith("2"):
            instrument = dir_name[:-1]
            if instrument in EXECUTION_INSTRUMENTS:
                canonical = get_canonical(instrument)
                issues.append(
                    f"[FAIL] Merger output directory uses execution instrument '{instrument}' "
                    f"instead of canonical '{canonical}': {instrument_session_dir}"
                )
        
        # Check Instrument column in parquet files
        for parquet_file in instrument_session_dir.rglob("*.parquet"):
            try:
                df = pd.read_parquet(parquet_file)
                if "Instrument" in df.columns:
                    instruments = df["Instrument"].str.upper().unique()
                    for inst in instruments:
                        if inst in EXECUTION_INSTRUMENTS:
                            canonical = get_canonical(inst)
                            issues.append(
                                f"[FAIL] Merger output contains execution instrument '{inst}' "
                                f"instead of canonical '{canonical}' in Instrument column: {parquet_file}"
                            )
            except Exception as e:
                issues.append(f"[WARN] Could not read merger file {parquet_file}: {e}")
    
    return len(issues) == 0, issues

def verify_snapshot_files(project_root: Path) -> Tuple[bool, List[str]]:
    """Verify snapshot files use canonical instrument names in paths."""
    issues = []
    snapshot_dir = project_root / "data" / "translated_test"
    
    if not snapshot_dir.exists():
        return True, []  # No snapshot to verify
    
    # Check all instrument directories
    for instrument_dir in snapshot_dir.iterdir():
        if not instrument_dir.is_dir():
            continue
        
        instrument = instrument_dir.name.upper()
        
        # Check if this is an execution instrument (micro)
        if instrument in EXECUTION_INSTRUMENTS:
            canonical = get_canonical(instrument)
            issues.append(
                f"[FAIL] Snapshot uses execution instrument '{instrument}' "
                f"instead of canonical '{canonical}': {instrument_dir}"
            )
    
    return len(issues) == 0, issues

def verify_sequencer_outputs(project_root: Path) -> Tuple[bool, List[str]]:
    """Verify sequencer outputs use canonical instrument names in Instrument column."""
    issues = []
    
    # Check sequencer_temp
    sequencer_temp_dir = project_root / "data" / "sequencer_temp"
    if sequencer_temp_dir.exists():
        for parquet_file in sequencer_temp_dir.rglob("*.parquet"):
            try:
                df = pd.read_parquet(parquet_file)
                if "Instrument" in df.columns:
                    instruments = df["Instrument"].str.upper().unique()
                    for inst in instruments:
                        if inst in EXECUTION_INSTRUMENTS:
                            canonical = get_canonical(inst)
                            issues.append(
                                f"[FAIL] Sequencer output contains execution instrument '{inst}' "
                                f"instead of canonical '{canonical}' in Instrument column: {parquet_file}"
                            )
            except Exception as e:
                issues.append(f"[WARN] Could not read sequencer file {parquet_file}: {e}")
    
    return len(issues) == 0, issues

def main():
    """Run all verification checks."""
    project_root = Path(__file__).parent.parent
    
    print("=" * 80)
    print("CANONICAL STORAGE CONVENTION VERIFICATION")
    print("=" * 80)
    print()
    print("HARD RULE: All persisted historical data must be stored by canonical")
    print("instrument, never execution instrument.")
    print()
    print("Checking:")
    print("  1. Translator outputs (data/translated/{canonical}/...)")
    print("  2. Analyzer outputs (Instrument column)")
    print("  3. Sequencer outputs (Instrument column)")
    print("  4. Merger outputs (paths and Instrument column)")
    print("  5. Snapshot files (data/translated_test/{canonical}/...)")
    print()
    
    all_issues = []
    
    # 1. Translator outputs
    print("1. Checking Translator outputs...")
    ok, issues = verify_translator_outputs(project_root)
    all_issues.extend(issues)
    if ok:
        print("   [OK] Translator outputs use canonical instruments")
    else:
        print(f"   [FAIL] Found {len(issues)} issue(s)")
    print()
    
    # 2. Analyzer outputs
    print("2. Checking Analyzer outputs...")
    ok, issues = verify_analyzer_outputs(project_root)
    all_issues.extend(issues)
    if ok:
        print("   [OK] Analyzer outputs use canonical instruments")
    else:
        print(f"   [FAIL] Found {len(issues)} issue(s)")
    print()
    
    # 3. Sequencer outputs
    print("3. Checking Sequencer outputs...")
    ok, issues = verify_sequencer_outputs(project_root)
    all_issues.extend(issues)
    if ok:
        print("   [OK] Sequencer outputs use canonical instruments")
    else:
        print(f"   [FAIL] Found {len(issues)} issue(s)")
    print()
    
    # 4. Merger outputs
    print("4. Checking Merger outputs...")
    ok, issues = verify_merger_outputs(project_root)
    all_issues.extend(issues)
    if ok:
        print("   [OK] Merger outputs use canonical instruments")
    else:
        print(f"   [FAIL] Found {len(issues)} issue(s)")
    print()
    
    # 5. Snapshot files
    print("5. Checking Snapshot files...")
    ok, issues = verify_snapshot_files(project_root)
    all_issues.extend(issues)
    if ok:
        print("   [OK] Snapshot files use canonical instruments")
    else:
        print(f"   [FAIL] Found {len(issues)} issue(s)")
    print()
    
    # Summary
    print("=" * 80)
    if len(all_issues) == 0:
        print("[PASS] VERIFICATION PASSED")
        print()
        print("All persisted historical data uses canonical instrument names.")
        print()
        print("This ensures:")
        print("  - SIM hydration OK")
        print("  - DRYRUN hydration OK")
        print("  - Analyzer parity OK")
        print("  - Matrix parity OK")
    else:
        print("[FAIL] VERIFICATION FAILED")
        print()
        print(f"Found {len(all_issues)} violation(s) of canonical storage convention:")
        print()
        for issue in all_issues:
            print(f"  {issue}")
        print()
        print("CONSEQUENCES:")
        print("  • DRYRUN will miss files (SIM will still work)")
        print("  • Analyzer parity may fail")
        print("  • Matrix parity may fail")
        print()
        print("ACTION REQUIRED:")
        print("  • Ensure raw CSV files are named by canonical instrument (ES, NQ, YM, etc.)")
        print("  • Ensure translator stores files by canonical instrument")
        print("  • Ensure analyzer outputs use canonical instrument in Instrument column")
        print("  • Ensure sequencer outputs use canonical instrument in Instrument column")
        print("  • Ensure merger outputs use canonical instrument in paths and columns")
    
    print("=" * 80)
    
    return 0 if len(all_issues) == 0 else 1

if __name__ == "__main__":
    exit(main())
