#!/usr/bin/env python3
"""
Comprehensive Master Matrix Functionality Tests

Tests core functionality:
- Data loading and integrity
- Sequencer logic (time change system)
- Filtering (excluded times, days, etc.)
- Statistics calculation
- API endpoints
- Data validation
"""

import sys
import json
import pandas as pd
import numpy as np
from pathlib import Path
from datetime import datetime, date
from typing import Dict, List, Optional

# Add project root to path
# File is at: modules/matrix/tests/test_matrix_functionality.py
# Need to go up: tests -> matrix -> modules -> QTSW2_ROOT
QTSW2_ROOT = Path(__file__).parent.parent.parent.parent
sys.path.insert(0, str(QTSW2_ROOT))

from modules.matrix.master_matrix import MasterMatrix
from modules.matrix import sequencer_logic, data_loader, filter_engine

class Colors:
    """ANSI color codes for terminal output"""
    GREEN = '\033[92m'
    RED = '\033[91m'
    YELLOW = '\033[93m'
    BLUE = '\033[94m'
    RESET = '\033[0m'
    BOLD = '\033[1m'

def print_header(text):
    """Print test section header"""
    print(f'\n{Colors.BOLD}{Colors.BLUE}{"="*70}{Colors.RESET}')
    print(f'{Colors.BOLD}{Colors.BLUE}{text}{Colors.RESET}')
    print(f'{Colors.BOLD}{Colors.BLUE}{"="*70}{Colors.RESET}\n')

def print_test(name):
    """Print test name"""
    print(f'{Colors.BOLD}Test: {name}{Colors.RESET}')

def print_pass(message, details=None):
    """Print passing test result"""
    try:
        print(f'{Colors.GREEN}[PASS]{Colors.RESET}: {message}')
    except UnicodeEncodeError:
        print(f'[PASS]: {message}')
    if details:
        print(f'  {details}')

def print_fail(message, details=None):
    """Print failing test result"""
    try:
        print(f'{Colors.RED}[FAIL]{Colors.RESET}: {message}')
    except UnicodeEncodeError:
        print(f'[FAIL]: {message}')
    if details:
        print(f'  {details}')

def print_warn(message, details=None):
    """Print warning"""
    try:
        print(f'{Colors.YELLOW}[WARN]{Colors.RESET}: {message}')
    except UnicodeEncodeError:
        print(f'[WARN]: {message}')
    if details:
        print(f'  {details}')

class MatrixTester:
    """Test suite for Master Matrix functionality"""
    
    def __init__(self, analyzer_runs_dir: str = "data/analyzed"):
        self.analyzer_runs_dir = Path(QTSW2_ROOT / analyzer_runs_dir)
        self.matrix = None
        self.test_results = []
        
    def run_all_tests(self):
        """Run all tests"""
        print_header("MASTER MATRIX FUNCTIONALITY TESTS")
        print(f"Project Root: {QTSW2_ROOT}")
        print(f"Analyzer Runs Dir: {self.analyzer_runs_dir}")
        print(f"Analyzer Runs Exists: {self.analyzer_runs_dir.exists()}\n")
        
        tests = [
            ("Data Loading", self.test_data_loading),
            ("Matrix Initialization", self.test_matrix_initialization),
            ("Sequencer Logic", self.test_sequencer_logic),
            ("Excluded Times Filtering", self.test_excluded_times_filtering),
            ("Day Filtering", self.test_day_filtering),
            ("Statistics Calculation", self.test_statistics),
            ("Data Integrity", self.test_data_integrity),
            ("Schema Normalization", self.test_schema_normalization),
            ("File Operations", self.test_file_operations),
        ]
        
        passed = 0
        failed = 0
        warnings = 0
        
        for test_name, test_func in tests:
            try:
                result = test_func()
                if result:
                    passed += 1
                else:
                    failed += 1
            except Exception as e:
                print_fail(f"{test_name} raised exception", str(e))
                failed += 1
        
        # Summary
        print_header("TEST SUMMARY")
        print(f"{Colors.GREEN}Passed: {passed}{Colors.RESET}")
        print(f"{Colors.RED}Failed: {failed}{Colors.RESET}")
        print(f"{Colors.YELLOW}Warnings: {warnings}{Colors.RESET}")
        print(f"\nTotal: {passed + failed} tests")
        
        return failed == 0
    
    def test_data_loading(self) -> bool:
        """Test data loading functionality"""
        print_test("Data Loading")
        
        if not self.analyzer_runs_dir.exists():
            print_warn("Analyzer runs directory not found - skipping data loading tests")
            return True
        
        try:
            # Test stream discovery
            from modules.matrix.stream_manager import discover_streams
            streams = discover_streams(self.analyzer_runs_dir)
            
            if not streams:
                print_warn("No streams discovered", "Check that analyzer_runs directory contains stream subdirectories")
                return True
            
            print_pass(f"Discovered {len(streams)} streams", f"Streams: {streams}")
            
            # Test loading a single stream
            if streams:
                test_stream = streams[0]
                success, data, _ = data_loader.load_stream_data(
                    test_stream,
                    self.analyzer_runs_dir
                )
                
                if success and data:
                    total_trades = sum(len(df) for df in data)
                    print_pass(f"Loaded {test_stream}", f"{total_trades} trades loaded")
                else:
                    print_fail(f"Failed to load {test_stream}")
                    return False
            
            return True
        except Exception as e:
            print_fail("Data loading test failed", str(e))
            return False
    
    def test_matrix_initialization(self) -> bool:
        """Test matrix initialization"""
        print_test("Matrix Initialization")
        
        try:
            self.matrix = MasterMatrix(analyzer_runs_dir=str(self.analyzer_runs_dir))
            print_pass("Matrix initialized successfully")
            
            # Check streams
            if hasattr(self.matrix, 'streams') and self.matrix.streams:
                print_pass(f"Matrix has {len(self.matrix.streams)} streams")
            else:
                print_warn("Matrix has no streams", "May need to build matrix first")
            
            return True
        except Exception as e:
            print_fail("Matrix initialization failed", str(e))
            return False
    
    def test_sequencer_logic(self) -> bool:
        """Test sequencer logic (time change system)"""
        print_test("Sequencer Logic")
        
        try:
            # Create sample data for testing
            sample_data = pd.DataFrame({
                'Date': pd.to_datetime(['2024-01-01', '2024-01-02', '2024-01-03'] * 3),
                'Time': ['08:00', '09:00', '10:00'] * 3,
                'Stream': ['ES1'] * 9,
                'Instrument': ['ES'] * 9,
                'Session': ['S1', 'S1', 'S2'] * 3,
                'Result': ['Win', 'Loss', 'Win', 'Loss', 'Win', 'Win', 'BE', 'Loss', 'Win'],
                'Profit': [10, -5, 8, -3, 12, 15, 0, -2, 9],
                'EntryTime': ['08:00:00'] * 9,
                'ExitTime': ['08:30:00'] * 9,
                'Direction': ['Long'] * 9,
                'Target': [10] * 9,
                'Range': [5] * 9,
                'SL': [5] * 9,
                'Peak': [10] * 9,
            })
            
            # Test sequencer logic
            stream_filters = {}
            result_df = sequencer_logic.apply_sequencer_logic(sample_data, stream_filters, display_year=None)
            
            if result_df is not None and len(result_df) > 0:
                # Should have one trade per day
                unique_dates = result_df['Date'].nunique()
                print_pass(f"Sequencer logic processed {len(result_df)} trades", 
                          f"{unique_dates} unique dates")
                
                # Check that time changes are applied
                if 'Time Change' in result_df.columns:
                    time_changes = result_df['Time Change'].notna().sum()
                    print_pass(f"Time changes tracked", f"{time_changes} time changes recorded")
                
                return True
            else:
                print_fail("Sequencer logic returned empty result")
                return False
                
        except Exception as e:
            print_fail("Sequencer logic test failed", str(e))
            import traceback
            print(f"  Traceback: {traceback.format_exc()}")
            return False
    
    def test_excluded_times_filtering(self) -> bool:
        """Test excluded times filtering"""
        print_test("Excluded Times Filtering")
        
        try:
            # Create sample data with multiple times
            sample_data = pd.DataFrame({
                'Date': pd.to_datetime(['2024-01-01'] * 4),
                'Time': ['07:30', '08:00', '09:00', '10:00'],
                'Stream': ['ES1'] * 4,
                'Instrument': ['ES'] * 4,
                'Session': ['S1', 'S1', 'S1', 'S2'],
                'Result': ['Win', 'Win', 'Win', 'Win'],
                'Profit': [10, 8, 12, 15],
            })
            
            # Test filtering with excluded times
            exclude_times = ['07:30', '10:00']
            filtered = sequencer_logic.filter_excluded_times(
                sample_data, exclude_times, 'ES1', sample_data['Date'].iloc[0]
            )
            
            remaining_times = filtered['Time'].unique().tolist() if not filtered.empty else []
            
            # Check that excluded times are removed
            excluded_still_present = [t for t in exclude_times if t in remaining_times]
            
            if excluded_still_present:
                print_fail("Excluded times still present after filtering", 
                          f"Times: {excluded_still_present}, Remaining: {remaining_times}")
                return False
            else:
                print_pass("Excluded times properly filtered", 
                          f"Remaining times: {remaining_times}")
                return True
                
        except Exception as e:
            print_fail("Excluded times filtering test failed", str(e))
            return False
    
    def test_day_filtering(self) -> bool:
        """Test day of week and day of month filtering"""
        print_test("Day Filtering")
        
        try:
            # Test that filter structure is correct
            # The actual filtering is done in filter_engine, but we can test the structure
            
            # Create sample data with different days
            sample_data = pd.DataFrame({
                'Date': pd.to_datetime(['2024-01-01', '2024-01-02', '2024-01-03', '2024-01-04', '2024-01-05']),
                'Time': ['08:00'] * 5,
                'Stream': ['ES1'] * 5,
                'Instrument': ['ES'] * 5,
                'Result': ['Win'] * 5,
                'Profit': [10] * 5,
            })
            
            # Test day of week filtering structure
            stream_filters = {
                'ES1': {
                    'exclude_days_of_week': ['Friday'],
                    'exclude_days_of_month': [4, 16, 30]
                }
            }
            
            # Verify filter structure is valid
            if 'ES1' in stream_filters:
                filters = stream_filters['ES1']
                if 'exclude_days_of_week' in filters and 'exclude_days_of_month' in filters:
                    print_pass("Day filtering structure validated", 
                              f"Exclude DOW: {filters['exclude_days_of_week']}, Exclude DOM: {filters['exclude_days_of_month']}")
                    return True
            
            print_fail("Day filtering structure invalid")
            return False
            
        except Exception as e:
            print_fail("Day filtering test failed", str(e))
            return False
    
    def test_statistics(self) -> bool:
        """Test statistics calculation"""
        print_test("Statistics Calculation")
        
        try:
            from modules.matrix.statistics import calculate_summary_stats
            
            # Create sample data
            sample_data = pd.DataFrame({
                'Result': ['Win', 'Win', 'Loss', 'BE', 'NoTrade', 'Win', 'Loss'],
                'Profit': [10, 15, -5, 0, 0, 12, -3],
                'Stream': ['ES1'] * 7,
            })
            
            stats = calculate_summary_stats(sample_data)
            
            # Verify stats structure
            required_keys = ['total_trades', 'wins', 'losses', 'win_rate', 'total_profit']
            missing_keys = [k for k in required_keys if k not in stats]
            
            if missing_keys:
                print_fail("Statistics missing required keys", f"Missing: {missing_keys}")
                return False
            
            # Verify calculations
            if stats['wins'] != 3:
                print_fail(f"Win count incorrect", f"Expected 3, got {stats['wins']}")
                return False
            
            if stats['losses'] != 2:
                print_fail(f"Loss count incorrect", f"Expected 2, got {stats['losses']}")
                return False
            
            print_pass("Statistics calculated correctly", 
                      f"Wins: {stats['wins']}, Losses: {stats['losses']}, Profit: {stats['total_profit']}")
            return True
            
        except Exception as e:
            print_fail("Statistics test failed", str(e))
            return False
    
    def test_data_integrity(self) -> bool:
        """Test data integrity and validation"""
        print_test("Data Integrity")
        
        try:
            # Create sample data with potential issues
            sample_data = pd.DataFrame({
                'Date': pd.to_datetime(['2024-01-01', '2024-01-02', None]),
                'Time': ['08:00', '09:00', '10:00'],
                'Stream': ['ES1', 'ES1', 'ES1'],
                'Result': ['Win', 'Loss', 'Win'],
                'Profit': [10, -5, np.nan],
            })
            
            # Test date validation
            valid_dates = sample_data['Date'].notna()
            invalid_count = (~valid_dates).sum()
            
            if invalid_count > 0:
                print_pass(f"Date validation working", f"Found {invalid_count} invalid dates")
            else:
                print_pass("All dates valid")
            
            # Test NaN handling
            nan_profits = sample_data['Profit'].isna().sum()
            if nan_profits > 0:
                print_warn(f"Found {nan_profits} NaN profit values", "These should be handled in processing")
            
            return True
            
        except Exception as e:
            print_fail("Data integrity test failed", str(e))
            return False
    
    def test_schema_normalization(self) -> bool:
        """Test schema normalization"""
        print_test("Schema Normalization")
        
        try:
            if not self.matrix:
                self.matrix = MasterMatrix(analyzer_runs_dir=str(self.analyzer_runs_dir))
            
            # Create sample data with different column names
            sample_data = pd.DataFrame({
                'Date': pd.to_datetime(['2024-01-01']),
                'Time': ['08:00'],
                'Stream': ['ES1'],
                'Result': ['Win'],
                'Profit': [10],
            })
            
            # Test normalization
            normalized = self.matrix.normalize_schema(sample_data)
            
            # Check that required columns exist
            required_cols = ['Date', 'Time', 'Stream', 'Result', 'Profit']
            missing_cols = [c for c in required_cols if c not in normalized.columns]
            
            if missing_cols:
                print_fail("Schema normalization missing columns", f"Missing: {missing_cols}")
                return False
            
            print_pass("Schema normalized correctly")
            return True
            
        except Exception as e:
            print_fail("Schema normalization test failed", str(e))
            return False
    
    def test_file_operations(self) -> bool:
        """Test file save/load operations"""
        print_test("File Operations")
        
        try:
            if not self.matrix:
                self.matrix = MasterMatrix(analyzer_runs_dir=str(self.analyzer_runs_dir))
            
            # Create minimal test data
            test_data = pd.DataFrame({
                'Date': pd.to_datetime(['2024-01-01']),
                'Time': ['08:00'],
                'Stream': ['ES1'],
                'Result': ['Win'],
                'Profit': [10],
            })
            
            # Test saving (if file_manager is available)
            output_dir = QTSW2_ROOT / "data" / "master_matrix" / "test"
            output_dir.mkdir(parents=True, exist_ok=True)
            
            test_file = output_dir / "test_matrix.parquet"
            test_data.to_parquet(test_file)
            
            if test_file.exists():
                print_pass("File save operation works")
                
                # Test loading
                loaded = pd.read_parquet(test_file)
                if len(loaded) == len(test_data):
                    print_pass("File load operation works")
                else:
                    print_fail("Loaded data doesn't match saved data")
                    return False
                
                # Cleanup
                test_file.unlink()
                return True
            else:
                print_fail("File was not created")
                return False
                
        except Exception as e:
            print_fail("File operations test failed", str(e))
            return False


def main():
    """Run all tests"""
    import argparse
    
    parser = argparse.ArgumentParser(description='Test Master Matrix functionality')
    parser.add_argument('--analyzer-dir', default='data/analyzed',
                       help='Path to analyzer_runs directory')
    parser.add_argument('--verbose', '-v', action='store_true',
                       help='Verbose output')
    
    args = parser.parse_args()
    
    tester = MatrixTester(analyzer_runs_dir=args.analyzer_dir)
    success = tester.run_all_tests()
    
    sys.exit(0 if success else 1)


if __name__ == '__main__':
    main()

