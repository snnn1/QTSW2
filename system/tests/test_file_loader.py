"""
Unit Tests for translator.file_loader module
Tests for detect_file_format, load_single_file, get_file_years, and get_data_files
"""

import pytest
import pandas as pd
from pathlib import Path
import tempfile
from datetime import datetime

# Import functions to test
from translator.file_loader import (
    detect_file_format,
    load_single_file,
    get_file_years,
    get_data_files
)


class TestDetectFileFormat:
    """Tests for detect_file_format() function"""
    
    def test_detect_csv_with_header(self):
        """Test: Detects CSV format with header"""
        with tempfile.TemporaryDirectory() as tmpdir:
            test_file = Path(tmpdir) / "test.csv"
            test_file.write_text("Date,Time,Open,High,Low,Close,Volume\n")
            
            result = detect_file_format(test_file)
            
            assert result['has_header'] is True
            assert result['separator'] == ','
    
    def test_detect_csv_without_header(self):
        """Test: Detects CSV format without header"""
        with tempfile.TemporaryDirectory() as tmpdir:
            test_file = Path(tmpdir) / "test.csv"
            test_file.write_text("20240115 093000,4825.0,4826.0,4824.0,4825.5,1000000\n")
            
            result = detect_file_format(test_file)
            
            assert result['has_header'] is False
            assert result['separator'] == ','
    
    def test_detect_semicolon_separator(self):
        """Test: Detects semicolon separator"""
        with tempfile.TemporaryDirectory() as tmpdir:
            test_file = Path(tmpdir) / "test.csv"
            test_file.write_text("Date;Time;Open;High;Low;Close;Volume\n")
            
            result = detect_file_format(test_file)
            
            assert result['separator'] == ';'
    
    def test_detect_txt_file(self):
        """Test: Detects TXT file format"""
        with tempfile.TemporaryDirectory() as tmpdir:
            test_file = Path(tmpdir) / "test.txt"
            test_file.write_text("Date,Time,Open,High,Low,Close,Volume\n")
            
            result = detect_file_format(test_file)
            
            assert result['has_header'] is True
            assert result['separator'] == ','


class TestLoadSingleFile:
    """Tests for load_single_file() function"""
    
    def create_test_csv_with_header(self, filepath: Path):
        """Helper: Create test CSV with header"""
        content = """Date,Time,Open,High,Low,Close,Volume,Instrument
2024-01-15,09:30,4825.00,4826.50,4824.25,4825.75,1250000,ES
2024-01-15,09:31,4825.75,4826.00,4825.50,4825.50,980000,ES
2024-01-15,09:32,4825.50,4826.25,4825.25,4826.00,1100000,ES"""
        filepath.write_text(content)
    
    def test_load_single_file_correct_schema(self):
        """Test: Reads file into DataFrame with correct schema"""
        with tempfile.TemporaryDirectory() as tmpdir:
            test_file = Path(tmpdir) / "test_ES.csv"
            self.create_test_csv_with_header(test_file)
            
            result_df = load_single_file(test_file)
            
            # Check result is not None
            assert result_df is not None
            
            # Check required columns exist
            required_columns = ['timestamp', 'open', 'high', 'low', 'close', 'volume', 'instrument']
            for col in required_columns:
                assert col in result_df.columns, f"Missing column: {col}"
            
            # Check timestamp is datetime
            assert pd.api.types.is_datetime64_any_dtype(result_df['timestamp'])
    
    def test_load_single_file_timestamps_parsed_as_utc(self):
        """Test: Timestamps are parsed and converted from UTC to Chicago"""
        with tempfile.TemporaryDirectory() as tmpdir:
            test_file = Path(tmpdir) / "test_ES.csv"
            self.create_test_csv_with_header(test_file)
            
            result_df = load_single_file(test_file)
            
            assert result_df is not None
            
            # Check timestamps have timezone info
            first_timestamp = result_df['timestamp'].iloc[0]
            
            # Should be timezone-aware
            assert first_timestamp.tz is not None
            
            # Should be in Chicago timezone (after conversion)
            assert str(first_timestamp.tz) == 'America/Chicago'
    
    def test_load_single_file_numeric_columns(self):
        """Test: Numeric columns are properly converted"""
        with tempfile.TemporaryDirectory() as tmpdir:
            test_file = Path(tmpdir) / "test_ES.csv"
            self.create_test_csv_with_header(test_file)
            
            result_df = load_single_file(test_file)
            
            assert result_df is not None
            
            # Check numeric columns are numeric
            numeric_cols = ['open', 'high', 'low', 'close', 'volume']
            for col in numeric_cols:
                assert pd.api.types.is_numeric_dtype(result_df[col]), f"{col} should be numeric"
    
    def test_load_single_file_instrument_extraction(self):
        """Test: Instrument is extracted from filename"""
        with tempfile.TemporaryDirectory() as tmpdir:
            test_file = Path(tmpdir) / "MinuteDataExport_ES_2024.csv"
            self.create_test_csv_with_header(test_file)
            
            result_df = load_single_file(test_file)
            
            assert result_df is not None
            assert 'instrument' in result_df.columns
            # Should extract ES from filename
            assert result_df['instrument'].iloc[0] == 'ES'
    
    def test_load_single_file_detects_frequency_for_minute_data(self):
        """Test: load_single_file() detects and stores frequency for minute data"""
        with tempfile.TemporaryDirectory() as tmpdir:
            test_file = Path(tmpdir) / "test_minute.csv"
            # Create minute data (1-minute intervals)
            content = """Date,Time,Open,High,Low,Close,Volume,Instrument
2024-01-15,09:30,4825.00,4826.50,4824.25,4825.75,1250000,ES
2024-01-15,09:31,4825.75,4826.00,4825.50,4825.50,980000,ES
2024-01-15,09:32,4825.50,4826.25,4825.25,4826.00,1100000,ES
2024-01-15,09:33,4826.00,4826.50,4825.75,4826.25,1050000,ES"""
            test_file.write_text(content)
            
            result_df = load_single_file(test_file)
            
            assert result_df is not None
            # Should have frequency column
            assert 'frequency' in result_df.columns
            # Should detect as minute data
            assert result_df['frequency'].iloc[0] == '1min'
            # Should store in attrs
            assert result_df.attrs.get('frequency') == '1min'
            assert result_df.attrs.get('data_type') == 'minute'
    
    def test_load_single_file_detects_frequency_for_tick_data(self):
        """Test: load_single_file() detects and stores frequency for tick data"""
        with tempfile.TemporaryDirectory() as tmpdir:
            test_file = Path(tmpdir) / "test_tick.csv"
            # Create tick data (sub-second intervals)
            # Note: We'll create timestamps with seconds only, then pandas will handle sub-seconds
            content = """Date,Time,Open,High,Low,Close,Volume,Instrument
2024-01-15,09:30:00,4825.00,4826.50,4824.25,4825.75,1250,ES
2024-01-15,09:30:00,4825.75,4826.00,4825.50,4825.50,980,ES
2024-01-15,09:30:01,4825.50,4826.25,4825.25,4826.00,1100,ES
2024-01-15,09:30:01,4826.00,4826.50,4825.75,4826.25,1050,ES
2024-01-15,09:30:02,4826.25,4826.75,4826.00,4826.50,1200,ES"""
            test_file.write_text(content)
            
            result_df = load_single_file(test_file)
            
            assert result_df is not None
            # Should have frequency column
            assert 'frequency' in result_df.columns
            # Should detect as tick data (time differences < 60 seconds)
            assert result_df['frequency'].iloc[0] == 'tick'
            # Should store in attrs
            assert result_df.attrs.get('frequency') == 'tick'
            assert result_df.attrs.get('data_type') == 'tick'


class TestGetFileYears:
    """Tests for get_file_years() function"""
    
    def create_test_csv_with_years(self, filepath: Path, years: list = [2024, 2025]):
        """Helper: Create test CSV with data for specific years"""
        rows = []
        for year in years:
            for month in [1, 6]:  # Jan and Jun for each year
                rows.append({
                    'Date': f'{year}-{month:02d}-15',
                    'Time': '09:30',
                    'Open': 4825.0,
                    'High': 4826.0,
                    'Low': 4824.0,
                    'Close': 4825.5,
                    'Volume': 1000000,
                    'Instrument': 'ES'
                })
        
        df = pd.DataFrame(rows)
        df.to_csv(filepath, index=False)
    
    def test_get_file_years_extracts_all_years(self):
        """Test: Extracts all years present in timestamp range"""
        with tempfile.TemporaryDirectory() as tmpdir:
            test_file = Path(tmpdir) / "test_ES.csv"
            # Create file with 2024 and 2025 data
            self.create_test_csv_with_years(test_file, years=[2024, 2025])
            
            result_years = get_file_years(test_file)
            
            # Should return both years
            assert 2024 in result_years
            assert 2025 in result_years
            assert len(result_years) == 2
            assert result_years == [2024, 2025]
    
    def test_get_file_years_single_year(self):
        """Test: Extracts single year correctly"""
        with tempfile.TemporaryDirectory() as tmpdir:
            test_file = Path(tmpdir) / "test_ES.csv"
            self.create_test_csv_with_years(test_file, years=[2024])
            
            result_years = get_file_years(test_file)
            
            assert result_years == [2024]
    
    def test_get_file_years_multiple_years_sorted(self):
        """Test: Returns years in sorted order"""
        with tempfile.TemporaryDirectory() as tmpdir:
            test_file = Path(tmpdir) / "test_ES.csv"
            self.create_test_csv_with_years(test_file, years=[2025, 2023, 2024])
            
            result_years = get_file_years(test_file)
            
            # Should be sorted
            assert result_years == sorted(result_years)
            assert result_years == [2023, 2024, 2025]
    
    def test_get_file_years_no_header_format(self):
        """Test: Extracts years from no-header format"""
        with tempfile.TemporaryDirectory() as tmpdir:
            test_file = Path(tmpdir) / "test.txt"
            # No-header format: YYYYMMDD HHMMSS,open,high,low,close,volume
            content = """20240115 093000,4825.0,4826.0,4824.0,4825.5,1000000
20250615 093000,4825.0,4826.0,4824.0,4825.5,1000000"""
            test_file.write_text(content)
            
            result_years = get_file_years(test_file)
            
            assert 2024 in result_years
            assert 2025 in result_years


class TestGetDataFiles:
    """Tests for get_data_files() function"""
    
    def test_get_data_files_finds_csv_files(self):
        """Test: Finds CSV files in folder"""
        with tempfile.TemporaryDirectory() as tmpdir:
            folder_path = Path(tmpdir)
            
            # Create test files
            (folder_path / "file1.csv").write_text("test")
            (folder_path / "file2.txt").write_text("test")
            (folder_path / "other.txt").write_text("test")
            
            result_files = get_data_files(str(folder_path))
            
            # Should find CSV and TXT files
            file_names = [f.name for f in result_files]
            assert "file1.csv" in file_names
            assert "file2.txt" in file_names
            assert "other.txt" in file_names
    
    def test_get_data_files_returns_empty_if_no_files(self):
        """Test: Returns empty list if no data files"""
        with tempfile.TemporaryDirectory() as tmpdir:
            folder_path = Path(tmpdir)
            
            result_files = get_data_files(str(folder_path))
            
            assert result_files == []
    
    def test_get_data_files_returns_empty_if_folder_not_exists(self):
        """Test: Returns empty list if folder doesn't exist"""
        result_files = get_data_files("nonexistent_folder")
        
        assert result_files == []
    
    def test_get_data_files_sorted_order(self):
        """Test: Returns files in sorted order"""
        with tempfile.TemporaryDirectory() as tmpdir:
            folder_path = Path(tmpdir)
            
            # Create files out of order
            (folder_path / "z_file.csv").write_text("test")
            (folder_path / "a_file.csv").write_text("test")
            (folder_path / "m_file.csv").write_text("test")
            
            result_files = get_data_files(str(folder_path))
            
            file_names = [f.name for f in result_files]
            # Should be sorted
            assert file_names == sorted(file_names)

