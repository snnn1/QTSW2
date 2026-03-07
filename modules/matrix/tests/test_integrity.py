"""Tests for matrix integrity validation."""

import pandas as pd
import pytest
from modules.matrix.integrity import validate_matrix_integrity


def test_validate_empty_df():
    """Empty DataFrame passes."""
    df = pd.DataFrame()
    ok, issues = validate_matrix_integrity(df)
    assert ok is True
    assert issues == []


def test_validate_required_columns():
    """Missing required columns produces issues."""
    df = pd.DataFrame({"Stream": ["ES1"], "Result": ["WIN"]})
    ok, issues = validate_matrix_integrity(df)
    assert ok is False
    assert any("Missing required columns" in i for i in issues)


def test_validate_duplicate_stream_date():
    """Duplicate (stream, trade_date) in final rows produces issue."""
    df = pd.DataFrame({
        "Stream": ["ES1", "ES1"],
        "trade_date": pd.to_datetime(["2025-01-01", "2025-01-01"]),
        "Time": ["07:30", "08:00"],
        "Result": ["WIN", "LOSS"],
        "Profit": [1.0, -2.0],
        "final_allowed": [True, True],
    })
    ok, issues = validate_matrix_integrity(df)
    assert any("Duplicate" in i for i in issues)


def test_validate_scf_out_of_range():
    """SCF values outside [0, 1] produce issue."""
    df = pd.DataFrame({
        "Stream": ["ES1"],
        "trade_date": pd.to_datetime(["2025-01-01"]),
        "Time": ["07:30"],
        "Result": ["WIN"],
        "Profit": [1.0],
        "final_allowed": [True],
        "scf_s1": [1.5],
        "scf_s2": [0.3],
    })
    ok, issues = validate_matrix_integrity(df)
    assert any("scf_s1" in i and "outside" in i for i in issues)


def test_validate_valid_matrix():
    """Valid matrix passes."""
    df = pd.DataFrame({
        "Stream": ["ES1", "ES2"],
        "trade_date": pd.to_datetime(["2025-01-01", "2025-01-02"]),
        "Time": ["07:30", "08:00"],
        "Result": ["WIN", "LOSS"],
        "Profit": [1.0, -2.0],
        "final_allowed": [True, True],
        "scf_s1": [0.3, 0.2],
        "scf_s2": [0.2, 0.2],
    })
    ok, issues = validate_matrix_integrity(df)
    assert ok is True
    assert len(issues) == 0
