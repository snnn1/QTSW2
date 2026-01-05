"""
Golden Output Tests

Tests that matrix output matches baseline hashes exactly.
These tests ensure that refactoring doesn't change output.
"""

import sys
import json
import hashlib
from pathlib import Path
import pandas as pd

# Add project root to path
QTSW2_ROOT = Path(__file__).parent.parent.parent.parent
sys.path.insert(0, str(QTSW2_ROOT))

from modules.matrix.master_matrix import MasterMatrix

# Test directories
FIXTURE_DIR = Path(__file__).parent / "fixtures" / "data" / "analyzed"
GOLDEN_HASHES_FILE = Path(__file__).parent / "golden_hashes.json"


def compute_output_hash(df: pd.DataFrame) -> str:
    """
    Compute deterministic hash of matrix output.
    
    Args:
        df: Matrix DataFrame
        
    Returns:
        SHA256 hash as hex string
    """
    if df.empty:
        return hashlib.sha256(b"empty").hexdigest()
    
    # Sort by canonical order
    df_sorted = df.sort_values(
        by=['trade_date', 'entry_time', 'Instrument', 'Stream'],
        ascending=[True, True, True, True]
    ).reset_index(drop=True)
    
    # Convert to parquet bytes (deterministic)
    parquet_bytes = df_sorted.to_parquet(index=False)
    
    # Compute hash
    return hashlib.sha256(parquet_bytes).hexdigest()


def load_golden_hash(test_name: str) -> str:
    """Load golden hash from file."""
    if not GOLDEN_HASHES_FILE.exists():
        return None
    
    with open(GOLDEN_HASHES_FILE, 'r') as f:
        hashes = json.load(f)
        return hashes.get(test_name)


def save_golden_hash(test_name: str, hash_value: str):
    """Save golden hash to file."""
    hashes = {}
    if GOLDEN_HASHES_FILE.exists():
        with open(GOLDEN_HASHES_FILE, 'r') as f:
            hashes = json.load(f)
    
    hashes[test_name] = hash_value
    
    GOLDEN_HASHES_FILE.parent.mkdir(parents=True, exist_ok=True)
    with open(GOLDEN_HASHES_FILE, 'w') as f:
        json.dump(hashes, f, indent=2)


def test_full_rebuild_golden():
    """Test full rebuild output matches golden hash."""
    matrix = MasterMatrix(analyzer_runs_dir=str(FIXTURE_DIR))
    df = matrix.build_master_matrix()
    
    hash_value = compute_output_hash(df)
    golden_hash = load_golden_hash("full_rebuild")
    
    if golden_hash is None:
        # First run - save as golden
        save_golden_hash("full_rebuild", hash_value)
        print(f"[GOLDEN] Saved full_rebuild hash: {hash_value}")
        assert True, "Golden hash saved (first run)"
    else:
        assert hash_value == golden_hash, f"Hash mismatch! Got {hash_value}, expected {golden_hash}"
        print(f"[PASS] Full rebuild hash matches: {hash_value}")


def test_window_update_golden():
    """Test window update output matches golden hash."""
    # First build initial matrix
    matrix = MasterMatrix(analyzer_runs_dir=str(FIXTURE_DIR))
    matrix.build_master_matrix()
    
    # Then run window update
    df, _ = matrix.build_master_matrix_window_update(reprocess_days=5)
    
    hash_value = compute_output_hash(df)
    golden_hash = load_golden_hash("window_update")
    
    if golden_hash is None:
        save_golden_hash("window_update", hash_value)
        print(f"[GOLDEN] Saved window_update hash: {hash_value}")
        assert True, "Golden hash saved (first run)"
    else:
        assert hash_value == golden_hash, f"Hash mismatch! Got {hash_value}, expected {golden_hash}"
        print(f"[PASS] Window update hash matches: {hash_value}")


def test_authoritative_rebuild_golden():
    """Test full rebuild output matches golden hash (full rebuild is always authoritative)."""
    matrix = MasterMatrix(analyzer_runs_dir=str(FIXTURE_DIR))
    df = matrix.build_master_matrix()  # Full rebuild is always authoritative
    
    hash_value = compute_output_hash(df)
    golden_hash = load_golden_hash("authoritative_rebuild")
    
    if golden_hash is None:
        save_golden_hash("authoritative_rebuild", hash_value)
        print(f"[GOLDEN] Saved authoritative_rebuild hash: {hash_value}")
        assert True, "Golden hash saved (first run)"
    else:
        assert hash_value == golden_hash, f"Hash mismatch! Got {hash_value}, expected {golden_hash}"
        print(f"[PASS] Authoritative rebuild hash matches: {hash_value}")


def test_partial_rebuild_golden():
    """Test partial rebuild output matches golden hash."""
    matrix = MasterMatrix(analyzer_runs_dir=str(FIXTURE_DIR))
    df = matrix.build_master_matrix(streams=['ES1'])
    
    hash_value = compute_output_hash(df)
    golden_hash = load_golden_hash("partial_rebuild")
    
    if golden_hash is None:
        save_golden_hash("partial_rebuild", hash_value)
        print(f"[GOLDEN] Saved partial_rebuild hash: {hash_value}")
        assert True, "Golden hash saved (first run)"
    else:
        assert hash_value == golden_hash, f"Hash mismatch! Got {hash_value}, expected {golden_hash}"
        print(f"[PASS] Partial rebuild hash matches: {hash_value}")


if __name__ == "__main__":
    # Create fixture data first
    from fixtures.analyzer_output_fixture import save_fixture_parquet
    save_fixture_parquet()
    
    # Run tests
    print("\n=== Running Golden Tests ===\n")
    
    test_full_rebuild_golden()
    test_authoritative_rebuild_golden()
    test_partial_rebuild_golden()
    # Window update removed - replaced by rolling resequence
    
    print("\n=== All Golden Tests Passed ===\n")
