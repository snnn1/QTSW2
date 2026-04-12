"""
Tests for matrix optimization (Phase 1-5).

- SCF filter in filter_engine
- rs_value/points in schema_normalizer
- build_timetable_dataframe_from_master_matrix
- MatrixState get/invalidate
"""

import sys
import pandas as pd
import numpy as np
from pathlib import Path

QTSW2_ROOT = Path(__file__).resolve().parent.parent.parent.parent
sys.path.insert(0, str(QTSW2_ROOT))


def test_scf_filter_blocks_s1_when_above_threshold():
    """SCF filter blocks S1 trades when scf_s1 >= threshold."""
    from modules.matrix.filter_engine import _apply_scf_filter

    df = pd.DataFrame({
        "Stream": ["ES1", "ES2"],
        "Session": ["S1", "S2"],
        "scf_s1": [0.6, 0.3],
        "scf_s2": [0.3, 0.3],
        "filter_reasons": ["", ""],
        "final_allowed": [True, True],
    })
    result = _apply_scf_filter(df, scf_threshold=0.5)
    assert result.loc[0, "final_allowed"] == False
    assert "scf_s1_blocked" in str(result.loc[0, "filter_reasons"])
    assert result.loc[1, "final_allowed"] == True


def test_scf_filter_blocks_s2_when_above_threshold():
    """SCF filter blocks S2 trades when scf_s2 >= threshold."""
    from modules.matrix.filter_engine import _apply_scf_filter

    df = pd.DataFrame({
        "Stream": ["ES1", "ES2"],
        "Session": ["S1", "S2"],
        "scf_s1": [0.3, 0.3],
        "scf_s2": [0.3, 0.6],
        "filter_reasons": ["", ""],
        "final_allowed": [True, True],
    })
    result = _apply_scf_filter(df, scf_threshold=0.5)
    assert result.loc[0, "final_allowed"] == True
    assert result.loc[1, "final_allowed"] == False
    assert "scf_s2_blocked" in str(result.loc[1, "filter_reasons"])


def test_schema_normalizer_populates_rs_value_and_points():
    """Schema normalizer populates rs_value and points from sequencer columns."""
    from modules.matrix.schema_normalizer import create_derived_columns

    df = pd.DataFrame({
        "Time": ["07:30", "08:00", "07:30"],
        "07:30 Rolling": [1.5, 0.0, 2.0],
        "08:00 Rolling": [0.0, 1.0, 0.0],
        "07:30 Points": [1, 0, 1],
        "08:00 Points": [0, 1, 0],
        "Date": ["2025-01-02"] * 3,
        "Target": [10.0] * 3,
        "Range": [5.0] * 3,
        "StopLoss": [15.0] * 3,
        "Peak": [12.0] * 3,
        "Direction": ["Long"] * 3,
        "Result": ["WIN", "LOSS", "WIN"],
        "Stream": ["ES1"] * 3,
        "Instrument": ["ES"] * 3,
        "Session": ["S1"] * 3,
        "Profit": [100.0, -50.0, 100.0],
        "trade_date": pd.to_datetime(["2025-01-02"] * 3),
    })
    result = create_derived_columns(df)
    assert "rs_value" in result.columns
    assert "points" in result.columns
    assert result.loc[0, "rs_value"] == 1.5
    assert result.loc[0, "points"] == 1
    assert result.loc[1, "rs_value"] == 1.0
    assert result.loc[1, "points"] == 1


def test_build_timetable_dataframe_from_master_matrix():
    """build_timetable_dataframe_from_master_matrix returns correct structure."""
    from modules.timetable.timetable_engine import TimetableEngine

    df = pd.DataFrame({
        "Stream": ["ES1", "ES2"],
        "trade_date": pd.to_datetime(["2025-11-21", "2025-11-21"]),
        "Time": ["09:00", "10:30"],
        "Session": ["S1", "S2"],
        "Instrument": ["ES", "ES"],
        "final_allowed": [True, False],
        "scf_s1": [0.2, 0.2],
        "scf_s2": [0.2, 0.6],
    })
    engine = TimetableEngine()
    result = engine.build_timetable_dataframe_from_master_matrix(
        df, trade_date="2025-11-21", execution_mode=False
    )
    assert not result.empty
    assert "stream_id" in result.columns
    assert "selected_time" in result.columns
    assert "allowed" in result.columns
    assert "scf_s1" in result.columns
    assert "scf_s2" in result.columns
    assert len(result) >= 2


def test_matrix_state_invalidate():
    """MatrixState invalidate clears state."""
    from modules.matrix.matrix_state import invalidate_matrix_state, get_matrix_state

    invalidate_matrix_state()
    df, path, from_cache = get_matrix_state("data/master_matrix")
    if path is not None and df is not None:
        assert from_cache is False
    invalidate_matrix_state()


def run_tests():
    """Run all optimization tests."""
    tests = [
        test_scf_filter_blocks_s1_when_above_threshold,
        test_scf_filter_blocks_s2_when_above_threshold,
        test_schema_normalizer_populates_rs_value_and_points,
        test_build_timetable_dataframe_from_master_matrix,
        test_matrix_state_invalidate,
    ]
    passed = 0
    for t in tests:
        try:
            t()
            print(f"[PASS] {t.__name__}")
            passed += 1
        except Exception as e:
            print(f"[FAIL] {t.__name__}: {e}")
    print(f"\n{passed}/{len(tests)} tests passed")
    return passed == len(tests)


if __name__ == "__main__":
    success = run_tests()
    sys.exit(0 if success else 1)
