"""
Unit Tests for translator.core module
Tests for root_symbol, infer_contract_from_filename, and process_data
"""

import pytest
import pandas as pd
from pathlib import Path
from datetime import datetime
import tempfile
import shutil

# Import functions to test
from translator.core import root_symbol, infer_contract_from_filename, process_data


class TestRootSymbol:
    """Tests for root_symbol() function"""
    
    def test_extract_es_from_minute_data_export(self):
        """Test: Extracts 'ES' from MinuteDataExport_ES filename"""
        contract = "MinuteDataExport_ES_20250920_082412_UTC"
        result = root_symbol(contract)
        assert result == "ES"
    
    def test_extract_nq_from_minute_data_export(self):
        """Test: Extracts 'NQ' from MinuteDataExport_NQ filename"""
        contract = "MinuteDataExport_NQ_20250920_082412_UTC"
        result = root_symbol(contract)
        assert result == "NQ"
    
    def test_extract_ym_from_minute_data_export(self):
        """Test: Extracts 'YM' from MinuteDataExport_YM filename"""
        contract = "MinuteDataExport_YM_20250920_082412_UTC"
        result = root_symbol(contract)
        assert result == "YM"
    
    def test_extract_cl_from_minute_data_export(self):
        """Test: Extracts 'CL' from MinuteDataExport_CL filename"""
        contract = "MinuteDataExport_CL_20250920_082412_UTC"
        result = root_symbol(contract)
        assert result == "CL"
    
    def test_extract_ng_from_minute_data_export(self):
        """Test: Extracts 'NG' from MinuteDataExport_NG filename"""
        contract = "MinuteDataExport_NG_20250920_082412_UTC"
        result = root_symbol(contract)
        assert result == "NG"
    
    def test_extract_gc_from_minute_data_export(self):
        """Test: Extracts 'GC' from MinuteDataExport_GC filename"""
        contract = "MinuteDataExport_GC_20250920_082412_UTC"
        result = root_symbol(contract)
        assert result == "GC"
    
    def test_extract_from_simple_filename(self):
        """Test: Extracts symbol from simple filename"""
        contract = "ES_Mar2024"
        result = root_symbol(contract)
        assert result == "ES"
    
    def test_extract_from_uppercase(self):
        """Test: Returns uppercase"""
        contract = "es_contract"
        result = root_symbol(contract)
        assert result == "ES"


class TestInferContractFromFilename:
    """Tests for infer_contract_from_filename() function"""
    
    def test_extract_contract_from_path(self):
        """Test: Extracts contract name from Path object"""
        filepath = Path("MinuteDataExport_ES_20250920.csv")
        result = infer_contract_from_filename(filepath)
        assert result == "MinuteDataExport_ES_20250920"
    
    def test_extract_contract_with_extension(self):
        """Test: Removes extension correctly"""
        filepath = Path("ES_data.txt")
        result = infer_contract_from_filename(filepath)
        assert result == "ES_data"
    
    def test_extract_contract_no_extension(self):
        """Test: Handles filename without extension"""
        filepath = Path("NQ_raw_data")
        result = infer_contract_from_filename(filepath)
        assert result == "NQ_raw_data"


class TestProcessData:
    """Tests for process_data() function"""
    
    def create_test_csv_with_header(self, filepath: Path, years: list = [2024, 2025]):
        """Helper: Create test CSV file with header"""
        data_rows = []
        for year in years:
            # Create sample data for each year
            for month in [1, 6, 12]:  # Jan, Jun, Dec
                timestamp_str = f"{year}-{month:02d}-15 09:30"
                data_rows.append({
                    'Date': f"{year}-{month:02d}-15",
                    'Time': '09:30',
                    'Open': 4825.0,
                    'High': 4826.0,
                    'Low': 4824.0,
                    'Close': 4825.5,
                    'Volume': 1000000,
                    'Instrument': 'ES'
                })
        
        df = pd.DataFrame(data_rows)
        df.to_csv(filepath, index=False)
    
    def test_process_data_utc_to_chicago_conversion(self):
        """Test: Converts UTC timestamps to Chicago time"""
        with tempfile.TemporaryDirectory() as tmpdir:
            input_dir = Path(tmpdir) / "input"
            output_dir = Path(tmpdir) / "output"
            input_dir.mkdir()
            
            # Create test file
            test_file = input_dir / "test_ES.csv"
            self.create_test_csv_with_header(test_file, years=[2024])
            
            # Process
            success, result_df = process_data(
                str(input_dir),
                str(output_dir),
                separate_years=False,
                output_format="parquet",
                selected_files=["test_ES.csv"],
                selected_years=None
            )
            
            assert success is True
            assert result_df is not None
            assert 'timestamp' in result_df.columns
            
            # Check timezone is Chicago
            if hasattr(result_df['timestamp'].iloc[0], 'tz'):
                assert str(result_df['timestamp'].iloc[0].tz) == 'America/Chicago'
    
    def test_process_data_removes_duplicates(self):
        """Test: Removes duplicate rows"""
        with tempfile.TemporaryDirectory() as tmpdir:
            input_dir = Path(tmpdir) / "input"
            output_dir = Path(tmpdir) / "output"
            input_dir.mkdir()
            
            # Create test file with duplicates
            test_file = input_dir / "test_ES.csv"
            rows = []
            # Create 5 unique rows
            for i in range(5):
                rows.append({
                    'Date': '2024-01-15',
                    'Time': f'09:{30+i}',
                    'Open': 4825.0 + i,
                    'High': 4826.0 + i,
                    'Low': 4824.0 + i,
                    'Close': 4825.5 + i,
                    'Volume': 1000000,
                    'Instrument': 'ES'
                })
            # Add duplicate row
            rows.append(rows[0])  # Duplicate of first row
            
            df = pd.DataFrame(rows)
            df.to_csv(test_file, index=False)
            
            # Process
            success, result_df = process_data(
                str(input_dir),
                str(output_dir),
                separate_years=False,
                output_format="parquet",
                selected_files=["test_ES.csv"],
                selected_years=None
            )
            
            assert success is True
            assert result_df is not None
            # Should have 5 rows (duplicate removed)
            assert len(result_df) == 5
    
    def test_process_data_expected_rows(self):
        """Test: Returns expected number of rows"""
        with tempfile.TemporaryDirectory() as tmpdir:
            input_dir = Path(tmpdir) / "input"
            output_dir = Path(tmpdir) / "output"
            input_dir.mkdir()
            
            # Create test file with known number of rows
            test_file = input_dir / "test_ES.csv"
            self.create_test_csv_with_header(test_file, years=[2024])
            
            # Process
            success, result_df = process_data(
                str(input_dir),
                str(output_dir),
                separate_years=False,
                output_format="parquet",
                selected_files=["test_ES.csv"],
                selected_years=None
            )
            
            assert success is True
            assert result_df is not None
            # Should have 3 rows (Jan, Jun, Dec)
            assert len(result_df) == 3
    
    def test_process_data_creates_correct_output_files(self):
        """Test: Creates output files in correct format"""
        with tempfile.TemporaryDirectory() as tmpdir:
            input_dir = Path(tmpdir) / "input"
            output_dir = Path(tmpdir) / "output"
            input_dir.mkdir()
            
            # Create test file with filename that root_symbol can parse correctly
            # Use MinuteDataExport format so root_symbol extracts "ES"
            test_file = input_dir / "MinuteDataExport_ES_2024.csv"
            self.create_test_csv_with_header(test_file, years=[2024])
            
            # Process with parquet format
            success, _ = process_data(
                str(input_dir),
                str(output_dir),
                separate_years=True,
                output_format="parquet",
                selected_files=["MinuteDataExport_ES_2024.csv"],
                selected_years=None
            )
            
            assert success is True
            # Should create ES_2024.parquet
            output_file = Path(output_dir) / "ES_2024.parquet"
            assert output_file.exists(), f"Expected ES_2024.parquet, but it doesn't exist. Files in output: {list(Path(output_dir).glob('*'))}"
            
            # Verify file can be read
            df = pd.read_parquet(output_file)
            assert len(df) == 3

