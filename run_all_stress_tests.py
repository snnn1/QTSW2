#!/usr/bin/env python3
"""
Run all DRYRUN stress tests sequentially.
"""

import subprocess
import sys
from pathlib import Path

TESTS = [
    ("Late-Start Test", "test_late_start_dryrun.py"),
    ("Missing-Data Test", "test_missing_data_dryrun.py"),
    ("Duplicate Bars Test", "test_duplicate_bars_dryrun.py"),
    ("Multi-Day Test", "test_multiday_dryrun.py"),
]

def main():
    """Run all stress tests."""
    print("="*80)
    print("DRYRUN STRESS TEST SUITE")
    print("="*80)
    print()
    
    results = {}
    
    for test_name, test_script in TESTS:
        print(f"\n{'='*80}")
        print(f"Running: {test_name}")
        print(f"{'='*80}\n")
        
        result = subprocess.run(
            [sys.executable, test_script],
            cwd=Path.cwd(),
            capture_output=False
        )
        
        results[test_name] = result.returncode == 0
        
        if result.returncode != 0:
            print(f"\nFAIL: {test_name} FAILED (exit code: {result.returncode})")
        else:
            print(f"\nPASS: {test_name} PASSED")
    
    # Summary
    print("\n" + "="*80)
    print("STRESS TEST SUITE SUMMARY")
    print("="*80)
    
    passed = sum(1 for r in results.values() if r)
    total = len(results)
    
    for test_name, passed_test in results.items():
        status = "PASS" if passed_test else "FAIL"
        print(f"{test_name:30s} {status}")
    
    print(f"\nTotal: {passed}/{total} tests passed")
    
    if passed == total:
        print("\nSUCCESS: All stress tests passed!")
        return 0
    else:
        print(f"\nWARNING: {total - passed} test(s) failed")
        return 1

if __name__ == "__main__":
    sys.exit(main())
