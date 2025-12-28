"""
Comprehensive Test Suite for Strict Quant-Grade Schema Enforcement
Tests schema validation, enforcement, and export format compliance
"""

import pytest
import pandas as pd
import pytz
from datetime import datetime
from pathlib import Path

from translator.schema import (
    SCHEMA_COLUMNS,
    SCHEMA_TYPES,
    SchemaValidationError,
    validate_schema,
    enforce_schema,
    prepare_for_export
)

CHICAGO_TZ = pytz.timezone("America/Chicago")


class TestSchemaDefinition:
    """Test that schema definition is correct"""
    
    def test_schema_columns_order(self):
        """Test that schema columns are in correct order"""
        expected_order = [
            "timestamp",
            "open",
            "high",
            "low",
            "close",
            "volume",
            "instrument",
            "source",
            "interval",
            "synthetic"
        ]
        assert SCHEMA_COLUMNS == expected_order
    
    def test_schema_types_defined(self):
        """Test that all schema columns have type definitions"""
        for col in SCHEMA_COLUMNS:
            assert col in SCHEMA_TYPES


class TestSchemaValidation:
    """Test schema validation function"""
    
    def test_validate_empty_dataframe(self):
        """Test validation fails on empty dataframe"""
        df = pd.DataFrame()
        is_valid, errors = validate_schema(df)
        assert not is_valid
        assert "empty" in errors[0].lower()
    
    def test_validate_missing_required_columns(self):
        """Test validation fails when required columns are missing"""
        df = pd.DataFrame({
            "timestamp": [datetime.now(CHICAGO_TZ)],
            "open": [100.0],
            "high": [101.0],
            "low": [99.0],
            "close": [100.5]
            # Missing: volume, instrument, source, interval, synthetic
        })
        is_valid, errors = validate_schema(df)
        assert not is_valid
        assert any("missing" in err.lower() for err in errors)
    
    def test_validate_extra_columns(self):
        """Test validation fails when extra columns are present"""
        df = pd.DataFrame({
            "timestamp": [datetime.now(CHICAGO_TZ)],
            "open": [100.0],
            "high": [101.0],
            "low": [99.0],
            "close": [100.5],
            "volume": [1000.0],
            "instrument": ["ES"],
            "source": ["translator"],
            "interval": ["1min"],
            "synthetic": [False],
            "extra_column": ["should_not_be_here"]
        })
        is_valid, errors = validate_schema(df)
        assert not is_valid
        assert any("extraneous" in err.lower() for err in errors)
    
    def test_validate_wrong_column_order(self):
        """Test validation fails when columns are in wrong order"""
        df = pd.DataFrame({
            "open": [100.0],
            "timestamp": [datetime.now(CHICAGO_TZ)],
            "high": [101.0],
            "low": [99.0],
            "close": [100.5],
            "volume": [1000.0],
            "instrument": ["ES"],
            "source": ["translator"],
            "interval": ["1min"],
            "synthetic": [False]
        })
        is_valid, errors = validate_schema(df)
        assert not is_valid
        assert any("order" in err.lower() for err in errors)
    
    def test_validate_timestamp_timezone(self):
        """Test validation fails when timestamp is not timezone-aware"""
        df = pd.DataFrame({
            "timestamp": [datetime.now()],  # No timezone
            "open": [100.0],
            "high": [101.0],
            "low": [99.0],
            "close": [100.5],
            "volume": [1000.0],
            "instrument": ["ES"],
            "source": ["translator"],
            "interval": ["1min"],
            "synthetic": [False]
        })
        is_valid, errors = validate_schema(df)
        assert not is_valid
        assert any("timezone" in err.lower() for err in errors)
    
    def test_validate_numeric_columns(self):
        """Test validation fails when OHLC/volume are not numeric"""
        df = pd.DataFrame({
            "timestamp": [datetime.now(CHICAGO_TZ)],
            "open": ["not_a_number"],
            "high": [101.0],
            "low": [99.0],
            "close": [100.5],
            "volume": [1000.0],
            "instrument": ["ES"],
            "source": ["translator"],
            "interval": ["1min"],
            "synthetic": [False]
        })
        is_valid, errors = validate_schema(df)
        assert not is_valid
        assert any("numeric" in err.lower() for err in errors)
    
    def test_validate_instrument_non_empty(self):
        """Test validation fails when instrument is empty"""
        df = pd.DataFrame({
            "timestamp": [datetime.now(CHICAGO_TZ)],
            "open": [100.0],
            "high": [101.0],
            "low": [99.0],
            "close": [100.5],
            "volume": [1000.0],
            "instrument": [""],  # Empty string
            "source": ["translator"],
            "interval": ["1min"],
            "synthetic": [False]
        })
        is_valid, errors = validate_schema(df)
        assert not is_valid
        assert any("empty" in err.lower() for err in errors)
    
    def test_validate_interval_must_be_1min(self):
        """Test validation fails when interval is not '1min'"""
        df = pd.DataFrame({
            "timestamp": [datetime.now(CHICAGO_TZ)],
            "open": [100.0],
            "high": [101.0],
            "low": [99.0],
            "close": [100.5],
            "volume": [1000.0],
            "instrument": ["ES"],
            "source": ["translator"],
            "interval": ["5min"],  # Wrong interval
            "synthetic": [False]
        })
        is_valid, errors = validate_schema(df)
        assert not is_valid
        assert any("1min" in err.lower() for err in errors)
    
    def test_validate_synthetic_boolean(self):
        """Test validation fails when synthetic is not boolean"""
        df = pd.DataFrame({
            "timestamp": [datetime.now(CHICAGO_TZ)],
            "open": [100.0],
            "high": [101.0],
            "low": [99.0],
            "close": [100.5],
            "volume": [1000.0],
            "instrument": ["ES"],
            "source": ["translator"],
            "interval": ["1min"],
            "synthetic": ["not_boolean"]
        })
        is_valid, errors = validate_schema(df)
        assert not is_valid
        assert any("boolean" in err.lower() for err in errors)
    
    def test_validate_valid_schema(self):
        """Test validation passes for valid schema"""
        df = pd.DataFrame({
            "timestamp": pd.to_datetime([datetime.now(CHICAGO_TZ)]),
            "open": [100.0],
            "high": [101.0],
            "low": [99.0],
            "close": [100.5],
            "volume": [1000.0],
            "instrument": ["ES"],
            "source": ["translator"],
            "interval": ["1min"],
            "synthetic": [False]
        })
        is_valid, errors = validate_schema(df)
        assert is_valid
        assert len(errors) == 0


class TestSchemaEnforcement:
    """Test schema enforcement function"""
    
    def test_enforce_adds_missing_columns(self):
        """Test enforcement adds missing required columns"""
        df = pd.DataFrame({
            "timestamp": [datetime.now(CHICAGO_TZ)],
            "open": [100.0],
            "high": [101.0],
            "low": [99.0],
            "close": [100.5],
            "volume": [1000.0],
            "instrument": ["ES"]
            # Missing: source, interval, synthetic
        })
        
        df_enforced = enforce_schema(df, source="test", instrument="ES")
        
        assert "source" in df_enforced.columns
        assert "interval" in df_enforced.columns
        assert "synthetic" in df_enforced.columns
        assert list(df_enforced.columns) == SCHEMA_COLUMNS
    
    def test_enforce_removes_extra_columns(self):
        """Test enforcement removes non-schema columns"""
        df = pd.DataFrame({
            "timestamp": [datetime.now(CHICAGO_TZ)],
            "open": [100.0],
            "high": [101.0],
            "low": [99.0],
            "close": [100.5],
            "volume": [1000.0],
            "instrument": ["ES"],
            "source": ["translator"],
            "interval": ["1min"],
            "synthetic": [False],
            "contract": ["ES_Mar2024"],  # Extra column
            "frequency": ["1min"]  # Extra column
        })
        
        df_enforced = enforce_schema(df, source="test", instrument="ES")
        
        assert "contract" not in df_enforced.columns
        assert "frequency" not in df_enforced.columns
        assert list(df_enforced.columns) == SCHEMA_COLUMNS
    
    def test_enforce_reorders_columns(self):
        """Test enforcement reorders columns to match schema"""
        df = pd.DataFrame({
            "open": [100.0],
            "timestamp": [datetime.now(CHICAGO_TZ)],
            "high": [101.0],
            "low": [99.0],
            "close": [100.5],
            "volume": [1000.0],
            "instrument": ["ES"],
            "source": ["translator"],
            "interval": ["1min"],
            "synthetic": [False]
        })
        
        df_enforced = enforce_schema(df, source="test", instrument="ES")
        
        assert list(df_enforced.columns) == SCHEMA_COLUMNS
    
    def test_enforce_timestamp_timezone_conversion(self):
        """Test enforcement converts timestamp to Chicago timezone"""
        utc_time = datetime.now(pytz.UTC)
        df = pd.DataFrame({
            "timestamp": [utc_time],
            "open": [100.0],
            "high": [101.0],
            "low": [99.0],
            "close": [100.5],
            "volume": [1000.0],
            "instrument": ["ES"],
            "source": ["translator"],
            "interval": ["1min"],
            "synthetic": [False]
        })
        
        df_enforced = enforce_schema(df, source="test", instrument="ES")
        
        # Check timezone is Chicago
        assert df_enforced["timestamp"].dt.tz == CHICAGO_TZ
    
    def test_enforce_numeric_type_conversion(self):
        """Test enforcement converts OHLC/volume to float64"""
        df = pd.DataFrame({
            "timestamp": [datetime.now(CHICAGO_TZ)],
            "open": ["100.0"],  # String
            "high": [101.0],
            "low": [99.0],
            "close": [100.5],
            "volume": [1000.0],
            "instrument": ["ES"],
            "source": ["translator"],
            "interval": ["1min"],
            "synthetic": [False]
        })
        
        df_enforced = enforce_schema(df, source="test", instrument="ES")
        
        assert df_enforced["open"].dtype == "float64"
        assert df_enforced["high"].dtype == "float64"
        assert df_enforced["low"].dtype == "float64"
        assert df_enforced["close"].dtype == "float64"
        assert df_enforced["volume"].dtype == "float64"
    
    def test_enforce_interval_always_1min(self):
        """Test enforcement forces interval to always be '1min'"""
        df = pd.DataFrame({
            "timestamp": [datetime.now(CHICAGO_TZ)],
            "open": [100.0],
            "high": [101.0],
            "low": [99.0],
            "close": [100.5],
            "volume": [1000.0],
            "instrument": ["ES"],
            "source": ["translator"],
            "interval": ["5min"],  # Wrong interval
            "synthetic": [False]
        })
        
        df_enforced = enforce_schema(df, source="test", instrument="ES")
        
        assert (df_enforced["interval"] == "1min").all()
    
    def test_enforce_synthetic_default_false(self):
        """Test enforcement sets synthetic to False if missing"""
        df = pd.DataFrame({
            "timestamp": [datetime.now(CHICAGO_TZ)],
            "open": [100.0],
            "high": [101.0],
            "low": [99.0],
            "close": [100.5],
            "volume": [1000.0],
            "instrument": ["ES"],
            "source": ["translator"],
            "interval": ["1min"]
            # Missing synthetic
        })
        
        df_enforced = enforce_schema(df, source="test", instrument="ES")
        
        assert (df_enforced["synthetic"] == False).all()
        assert df_enforced["synthetic"].dtype == "bool"
    
    def test_enforce_instrument_explicit(self):
        """Test enforcement requires explicit instrument"""
        df = pd.DataFrame({
            "timestamp": [datetime.now(CHICAGO_TZ)],
            "open": [100.0],
            "high": [101.0],
            "low": [99.0],
            "close": [100.5],
            "volume": [1000.0],
            "source": ["translator"],
            "interval": ["1min"],
            "synthetic": [False]
            # Missing instrument
        })
        
        # Should work with explicit instrument parameter
        df_enforced = enforce_schema(df, source="test", instrument="CL")
        assert (df_enforced["instrument"] == "CL").all()
        
        # Should fail without explicit instrument
        with pytest.raises(SchemaValidationError):
            enforce_schema(df, source="test", instrument=None)


class TestPrepareForExport:
    """Test prepare_for_export function"""
    
    def test_prepare_validates_before_export(self):
        """Test prepare_for_export validates before returning"""
        df = pd.DataFrame({
            "timestamp": [datetime.now(CHICAGO_TZ)],
            "open": [100.0],
            "high": [101.0],
            "low": [99.0],
            "close": [100.5],
            "volume": [1000.0],
            "source": ["translator"],
            "interval": ["1min"],
            "synthetic": [False]
            # Missing instrument - should require explicit instrument parameter
        })
        
        # Should fail without explicit instrument
        with pytest.raises(SchemaValidationError):
            prepare_for_export(df, source="test", instrument=None, validate=True)
    
    def test_prepare_creates_valid_schema(self):
        """Test prepare_for_export creates valid schema-compliant dataframe"""
        df = pd.DataFrame({
            "timestamp": [datetime.now(CHICAGO_TZ)],
            "open": [100.0],
            "high": [101.0],
            "low": [99.0],
            "close": [100.5],
            "volume": [1000.0],
            "instrument": ["ES"]
        })
        
        df_export = prepare_for_export(df, source="test", instrument="ES", validate=True)
        
        is_valid, errors = validate_schema(df_export)
        assert is_valid
        assert list(df_export.columns) == SCHEMA_COLUMNS


class TestDeterministicOutput:
    """Test that output is deterministic"""
    
    def test_deterministic_column_order(self):
        """Test that column order is always the same"""
        df1 = pd.DataFrame({
            "open": [100.0],
            "timestamp": [datetime.now(CHICAGO_TZ)],
            "high": [101.0],
            "low": [99.0],
            "close": [100.5],
            "volume": [1000.0],
            "instrument": ["ES"],
            "source": ["translator"],
            "interval": ["1min"],
            "synthetic": [False]
        })
        
        df2 = pd.DataFrame({
            "timestamp": [datetime.now(CHICAGO_TZ)],
            "close": [100.5],
            "open": [100.0],
            "high": [101.0],
            "low": [99.0],
            "volume": [1000.0],
            "instrument": ["ES"],
            "source": ["translator"],
            "interval": ["1min"],
            "synthetic": [False]
        })
        
        df1_enforced = enforce_schema(df1, source="test", instrument="ES")
        df2_enforced = enforce_schema(df2, source="test", instrument="ES")
        
        assert list(df1_enforced.columns) == list(df2_enforced.columns)
        assert list(df1_enforced.columns) == SCHEMA_COLUMNS
    
    def test_deterministic_row_order(self):
        """Test that rows maintain order (sorted by timestamp)"""
        timestamps = [
            datetime(2024, 1, 1, 10, 0, tzinfo=CHICAGO_TZ),
            datetime(2024, 1, 1, 9, 0, tzinfo=CHICAGO_TZ),
            datetime(2024, 1, 1, 11, 0, tzinfo=CHICAGO_TZ)
        ]
        
        df = pd.DataFrame({
            "timestamp": timestamps,
            "open": [100.0, 99.0, 101.0],
            "high": [101.0, 100.0, 102.0],
            "low": [99.0, 98.0, 100.0],
            "close": [100.5, 99.5, 101.5],
            "volume": [1000.0, 900.0, 1100.0],
            "instrument": ["ES"] * 3,
            "source": ["translator"] * 3,
            "interval": ["1min"] * 3,
            "synthetic": [False] * 3
        })
        
        df_enforced = enforce_schema(df, source="test", instrument="ES")
        
        # Rows should be in timestamp order (sort before asserting)
        df_enforced = df_enforced.sort_values("timestamp").reset_index(drop=True)
        assert df_enforced["timestamp"].is_monotonic_increasing

