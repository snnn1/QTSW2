"""
Unit Tests for translator.contract_rollover module
Tests for contract rollover and continuous series creation
"""

import pytest
import pandas as pd
from datetime import datetime, timedelta

from translator.contract_rollover import (
    parse_contract_month,
    detect_multiple_contracts,
    create_continuous_series,
    needs_rollover,
    calculate_rollover_date
)


class TestParseContractMonth:
    """Tests for parse_contract_month() function"""
    
    def test_parse_es_u2024(self):
        """Test: Parses ES_U2024 contract"""
        result = parse_contract_month("ES_U2024")
        assert result == ("ES", "U", 2024)
    
    def test_parse_nq_z2024(self):
        """Test: Parses NQ_Z2024 contract"""
        result = parse_contract_month("NQ_Z2024")
        assert result == ("NQ", "Z", 2024)
    
    def test_parse_esz24(self):
        """Test: Parses ESZ24 (no separator)"""
        result = parse_contract_month("ESZ24")
        assert result == ("ES", "Z", 2024)
    
    def test_parse_with_timestamp(self):
        """Test: Parses contract with timestamp suffix"""
        result = parse_contract_month("MinuteDataExport_ES_U2024_20250920")
        assert result == ("ES", "U", 2024)
    
    def test_parse_invalid(self):
        """Test: Returns None for invalid contract name"""
        result = parse_contract_month("Invalid_Contract")
        assert result is None


class TestDetectMultipleContracts:
    """Tests for detect_multiple_contracts() function"""
    
    def test_detects_multiple_contracts(self):
        """Test: Detects when multiple contracts exist per instrument"""
        df = pd.DataFrame({
            'instrument': ['ES', 'ES', 'ES', 'NQ'],
            'contract': ['ES_U2024', 'ES_Z2024', 'ES_U2024', 'NQ_Z2024']
        })
        
        result = detect_multiple_contracts(df)
        
        assert 'ES' in result
        assert len(result['ES']) == 2  # U2024 and Z2024
        assert 'NQ' not in result  # Only one contract
    
    def test_returns_empty_if_single_contract(self):
        """Test: Returns empty dict if single contract per instrument"""
        df = pd.DataFrame({
            'instrument': ['ES', 'ES', 'NQ'],
            'contract': ['ES_U2024', 'ES_U2024', 'NQ_Z2024']
        })
        
        result = detect_multiple_contracts(df)
        assert result == {}


class TestNeedsRollover:
    """Tests for needs_rollover() function"""
    
    def test_returns_true_for_multiple_contracts(self):
        """Test: Returns True when multiple contracts detected"""
        df = pd.DataFrame({
            'instrument': ['ES', 'ES'],
            'contract': ['ES_U2024', 'ES_Z2024']
        })
        
        assert needs_rollover(df) is True
    
    def test_returns_false_for_single_contract(self):
        """Test: Returns False when single contract per instrument"""
        df = pd.DataFrame({
            'instrument': ['ES', 'ES'],
            'contract': ['ES_U2024', 'ES_U2024']
        })
        
        assert needs_rollover(df) is False


class TestCreateContinuousSeries:
    """Tests for create_continuous_series() function"""
    
    def create_contract_data(self, month_code: str, year: int, start_date: datetime) -> pd.DataFrame:
        """Helper: Create sample data for a contract"""
        dates = [start_date + timedelta(days=i) for i in range(10)]
        contract_name = f"ES_{month_code}{year}"
        
        return pd.DataFrame({
            'timestamp': dates,
            'open': [4825.0 + i for i in range(10)],
            'high': [4826.0 + i for i in range(10)],
            'low': [4824.0 + i for i in range(10)],
            'close': [4825.5 + i for i in range(10)],
            'volume': [1000000] * 10,
            'instrument': ['ES'] * 10,
            'contract': [contract_name] * 10
        })
    
    def test_creates_continuous_series(self):
        """Test: Merges multiple contracts into continuous series"""
        # Create two contracts with overlapping dates
        df1 = self.create_contract_data('U', 2024, datetime(2024, 8, 1))
        df2 = self.create_contract_data('Z', 2024, datetime(2024, 9, 15))
        
        df = pd.concat([df1, df2], ignore_index=True)
        
        result = create_continuous_series(df, back_adjust=True)
        
        # Should have data from both contracts
        assert len(result) > 0
        assert 'contract' in result.columns
        # Should be sorted by timestamp
        assert result['timestamp'].is_monotonic_increasing
    
    def test_single_contract_unchanged(self):
        """Test: Single contract data remains unchanged"""
        df = self.create_contract_data('U', 2024, datetime(2024, 8, 1))
        
        result = create_continuous_series(df, back_adjust=True)
        
        # Should be same length (no rollover needed)
        assert len(result) == len(df)


class TestCalculateRolloverDate:
    """Tests for calculate_rollover_date() function"""
    
    def test_calculates_september_rollover(self):
        """Test: Calculates rollover date for September contract"""
        rollover = calculate_rollover_date('U', 2024, days_before_expiration=14)
        
        assert rollover.year == 2024
        assert rollover.month == 9  # September
        assert isinstance(rollover, datetime)

