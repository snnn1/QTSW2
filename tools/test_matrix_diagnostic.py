#!/usr/bin/env python3
"""
Master Matrix Diagnostic Test
Tests all components of the Master Matrix system
"""

import sys
import requests
import json
import pandas as pd
import os
from pathlib import Path
from datetime import datetime

# Add project root to path
QTSW2_ROOT = Path(__file__).parent.parent
sys.path.insert(0, str(QTSW2_ROOT))

API_BASE = 'http://localhost:8000/api'

def print_test(name):
    """Print test header"""
    print(f'\n{"="*60}')
    print(f'TEST: {name}')
    print('='*60)

def print_result(success, message, details=None, warning=False):
    """Print test result"""
    if warning:
        status = '⚠ WARN'
    else:
        status = '✓ PASS' if success else '✗ FAIL'
    print(f'{status}: {message}')
    if details:
        print(f'  Details: {details}')

def print_suggestion(suggestion):
    """Print a suggestion for improvement"""
    print(f'  💡 Suggestion: {suggestion}')

def test_backend_connection():
    """Test if backend is running and accessible"""
    print_test('Backend Connection')
    try:
        response = requests.get('http://localhost:8000/', timeout=5)
        if response.status_code == 200:
            data = response.json()
            print_result(True, 'Backend is running and accessible', data.get('message', ''))
            return True
        else:
            print_result(False, f'Backend returned status {response.status_code}')
            return False
    except requests.exceptions.ConnectionError:
        print_result(False, 'Cannot connect to backend - is it running?')
        print('  Hint: Start backend with: batch\\RUN_MASTER_MATRIX.bat')
        return False
    except Exception as e:
        print_result(False, f'Error connecting to backend: {e}')
        return False

def test_matrix_test_endpoint():
    """Test the matrix test endpoint"""
    print_test('Matrix Test Endpoint')
    try:
        response = requests.get(f'{API_BASE}/matrix/test', timeout=5)
        if response.status_code == 200:
            data = response.json()
            print_result(True, 'Matrix test endpoint working', data.get('message'))
            return True
        else:
            print_result(False, f'Endpoint returned status {response.status_code}')
            return False
    except Exception as e:
        print_result(False, f'Error testing endpoint: {e}')
        return False

def test_matrix_files():
    """Test matrix files availability"""
    print_test('Matrix Files Availability')
    try:
        response = requests.get(f'{API_BASE}/matrix/files', timeout=10)
        if response.status_code == 200:
            data = response.json()
            files = data.get('files', [])
            if files:
                print_result(True, f'Found {len(files)} matrix file(s)')
                latest = files[0] if files else None
                if latest:
                    size_mb = latest.get('size', 0) / 1024 / 1024
                    print(f'  Latest file: {latest.get("name")}')
                    print(f'  Size: {size_mb:.2f} MB')
                    print(f'  Modified: {latest.get("modified")}')
                return True
            else:
                print_result(False, 'No matrix files found - build the matrix first')
                print('  Hint: Use the Build Matrix button in the frontend')
                return False
        else:
            print_result(False, f'Endpoint returned status {response.status_code}')
            return False
    except Exception as e:
        print_result(False, f'Error checking files: {e}')
        return False

def test_matrix_data():
    """Test matrix data loading"""
    print_test('Matrix Data Loading')
    try:
        response = requests.get(f'{API_BASE}/matrix/data?limit=10', timeout=30)
        if response.status_code == 200:
            data = response.json()
            total = data.get('total', 0)
            loaded = data.get('loaded', 0)
            file_name = data.get('file', 'unknown')
            
            if total > 0:
                print_result(True, 'Matrix data loaded successfully')
                print(f'  Total records: {total:,}')
                print(f'  Loaded: {loaded:,}')
                print(f'  File: {file_name}')
                print(f'  Streams: {len(data.get("streams", []))}')
                print(f'  Instruments: {len(data.get("instruments", []))}')
                years = data.get('years', [])
                if years:
                    print(f'  Years: {years[:5]}{"..." if len(years) > 5 else ""}')
                return True
            else:
                print_result(False, 'Matrix file is empty')
                return False
        else:
            print_result(False, f'Endpoint returned status {response.status_code}')
            return False
    except Exception as e:
        print_result(False, f'Error loading data: {e}')
        return False

def test_timetable_files():
    """Test timetable files availability"""
    print_test('Timetable Files Availability')
    try:
        response = requests.get(f'{API_BASE}/timetable/files', timeout=10)
        if response.status_code == 200:
            data = response.json()
            files = data.get('files', [])
            if files:
                print_result(True, f'Found {len(files)} timetable file(s)')
                latest = files[0] if files else None
                if latest:
                    print(f'  Latest file: {latest.get("name")}')
                    print(f'  Modified: {latest.get("modified")}')
                return True
            else:
                print_result(False, 'No timetable files found - generate a timetable first')
                print('  Hint: Use the Generate Timetable button in the frontend')
                return False
        else:
            print_result(False, f'Endpoint returned status {response.status_code}')
            return False
    except Exception as e:
        print_result(False, f'Error checking timetable files: {e}')
        return False

def test_debug_log():
    """Test debug log endpoint"""
    print_test('Debug Log Endpoint')
    try:
        response = requests.get(f'{API_BASE}/test-debug-log', timeout=5)
        if response.status_code == 200:
            data = response.json()
            if data.get('status') == 'success':
                print_result(True, 'Debug log endpoint working', data.get('message'))
                return True
            else:
                print_result(False, data.get('message', 'Unknown error'))
                return False
        else:
            print_result(False, f'Endpoint returned status {response.status_code}')
            return False
    except Exception as e:
        print_result(False, f'Error testing debug log: {e}')
        return False

def test_frontend_port():
    """Test if frontend is accessible"""
    print_test('Frontend Port Check')
    try:
        response = requests.get('http://localhost:5174', timeout=5)
        if response.status_code == 200:
            print_result(True, 'Frontend is accessible on port 5174')
            return True
        else:
            print_result(False, f'Frontend returned status {response.status_code}')
            return False
    except requests.exceptions.ConnectionError:
        print_result(False, 'Frontend not running on port 5174')
        print('  Hint: Start frontend with: batch\\RUN_MASTER_MATRIX.bat')
        return False
    except Exception as e:
        print_result(False, f'Error checking frontend: {e}')
        return False

# ============================================================
# ERROR CATEGORY TESTS
# ============================================================

def test_data_loading_errors():
    """Category 1: Data Loading Errors"""
    print_test('1. Data Loading Errors')
    issues = []
    
    # Check analyzer_runs directory exists
    analyzer_runs_dir = QTSW2_ROOT / "data" / "analyzer_runs"
    if not analyzer_runs_dir.exists():
        issues.append("analyzer_runs directory missing")
    else:
        # Check for stream output files
        parquet_files = list(analyzer_runs_dir.rglob("*.parquet"))
        if not parquet_files:
            issues.append("No parquet files found in analyzer_runs")
        else:
            # Check file integrity
            corrupted = 0
            for f in parquet_files[:5]:  # Sample first 5
                try:
                    df = pd.read_parquet(f)
                    if df.empty:
                        corrupted += 1
                        issues.append(f"Empty file: {f.name}")
                except Exception as e:
                    corrupted += 1
                    issues.append(f"Corrupted file: {f.name} - {str(e)[:50]}")
            
            if corrupted == 0:
                print_result(True, f'Found {len(parquet_files)} stream output files, all readable')
            else:
                print_result(False, f'{corrupted} corrupted file(s) found')
    
    # Check sequencer_runs if it exists
    sequencer_runs_dir = QTSW2_ROOT / "data" / "sequencer_runs"
    if sequencer_runs_dir.exists():
        seq_files = list(sequencer_runs_dir.rglob("*.parquet"))
        if seq_files:
            print(f'  Found {len(seq_files)} sequencer output files')
    
    if issues:
        print_result(False, f'Found {len(issues)} data loading issue(s)')
        for issue in issues[:3]:  # Show first 3
            print(f'  - {issue}')
        return False
    return True

def test_sequencer_logic_errors():
    """Category 2: Sequencer Logic Errors"""
    print_test('2. Sequencer Logic Errors')
    try:
        # Load sample matrix data to check for duplicates
        response = requests.get(f'{API_BASE}/matrix/data?limit=500', timeout=45)
        if response.status_code == 200:
            data = response.json()
            records = data.get('data', [])
            
            if not records:
                print_result(False, 'No data to check')
                return False
            
            # Check for duplicate trades (same stream, same date)
            seen = {}
            duplicates = []
            missing_dates = []
            
            for record in records:
                stream = record.get('Stream', '')
                date = record.get('trade_date') or record.get('Date', '')
                key = f"{stream}_{date}"
                
                if key in seen:
                    duplicates.append(key)
                else:
                    seen[key] = record
            
            # Check date coverage
            dates = set()
            for record in records:
                date = record.get('trade_date') or record.get('Date', '')
                if date:
                    dates.add(str(date)[:10])  # Just the date part
            
            issues = []
            if duplicates:
                issues.append(f"Found {len(duplicates)} potential duplicate trades (same stream+date)")
            
            if len(dates) < 10:
                issues.append(f"Only {len(dates)} unique dates found (may indicate missing days)")
            
            if issues:
                print_result(False, f'Found {len(issues)} sequencer logic issue(s)')
                for issue in issues:
                    print(f'  - {issue}')
                return False
            else:
                print_result(True, f'No duplicate trades detected, {len(dates)} unique dates found')
                return True
        else:
            print_result(False, f'Cannot check sequencer logic - API returned {response.status_code}')
            return False
    except Exception as e:
        print_result(False, f'Error checking sequencer logic: {e}')
        return False

def test_schema_normalization_errors():
    """Category 3: Schema Normalization Errors"""
    print_test('3. Schema Normalization Errors')
    try:
        response = requests.get(f'{API_BASE}/matrix/data?limit=100', timeout=30)
        if response.status_code == 200:
            data = response.json()
            records = data.get('data', [])
            
            if not records:
                print_result(False, 'No data to check schema')
                return False
            
            # Expected columns from analyzer schema
            expected_cols = ['TradeID', 'Date', 'Time', 'Instrument', 'Stream', 'Session', 
                           'Direction', 'EntryPrice', 'ExitPrice', 'Result', 'Profit']
            
            # Check first record for required columns
            sample = records[0]
            missing_cols = []
            wrong_types = []
            
            for col in expected_cols:
                if col not in sample:
                    missing_cols.append(col)
            
            # Check data types
            date_cols = ['Date', 'trade_date']
            for col in date_cols:
                if col in sample and sample[col]:
                    # Should be ISO format string or null
                    if not isinstance(sample[col], (str, type(None))):
                        wrong_types.append(f"{col} has wrong type")
            
            issues = []
            if missing_cols:
                issues.append(f"Missing columns: {', '.join(missing_cols[:5])}")
            if wrong_types:
                issues.append(f"Type issues: {', '.join(wrong_types)}")
            
            if issues:
                print_result(False, f'Found {len(issues)} schema issue(s)')
                for issue in issues:
                    print(f'  - {issue}')
                return False
            else:
                print_result(True, f'Schema normalized correctly, {len(sample)} columns found')
                return True
        else:
            print_result(False, f'Cannot check schema - API returned {response.status_code}')
            return False
    except Exception as e:
        print_result(False, f'Error checking schema: {e}')
        return False

def test_file_io_errors():
    """Category 4: File I/O Errors"""
    print_test('4. File I/O Errors')
    issues = []
    
    # Check matrix output directory
    matrix_dir = QTSW2_ROOT / "data" / "master_matrix"
    if matrix_dir.exists():
        # Check if directory is writable
        test_file = matrix_dir / ".test_write"
        try:
            test_file.write_text("test")
            test_file.unlink()
        except PermissionError:
            issues.append("Matrix directory not writable")
        except Exception as e:
            issues.append(f"Cannot write to matrix directory: {e}")
    
    # Check disk space (rough estimate)
    try:
        import shutil
        total, used, free = shutil.disk_usage(QTSW2_ROOT)
        free_gb = free / (1024**3)
        if free_gb < 1:
            issues.append(f"Low disk space: {free_gb:.2f} GB free")
    except:
        pass  # Skip if can't check
    
    # Check latest matrix file can be read
    try:
        response = requests.get(f'{API_BASE}/matrix/files', timeout=10)
        if response.status_code == 200:
            files = response.json().get('files', [])
            if files:
                latest = files[0]
                file_path = Path(latest.get('path', ''))
                if file_path.exists():
                    try:
                        df = pd.read_parquet(file_path)
                        if df.empty:
                            issues.append("Latest matrix file is empty")
                    except Exception as e:
                        issues.append(f"Cannot read latest matrix file: {str(e)[:50]}")
    except:
        pass
    
    if issues:
        print_result(False, f'Found {len(issues)} file I/O issue(s)')
        for issue in issues:
            print(f'  - {issue}')
        return False
    else:
        print_result(True, 'File I/O operations working correctly')
        return True

def test_api_backend_errors():
    """Category 5: API / Backend Errors"""
    print_test('5. API / Backend Errors')
    issues = []
    
    # Test matrix build endpoint (without actually building)
    try:
        # Just check if endpoint exists and returns proper error for empty request
        response = requests.post(f'{API_BASE}/matrix/build', 
                               json={}, timeout=3)
        # Should return 422 (validation error) or 200, not 500
        if response.status_code == 500:
            issues.append("Matrix build endpoint returns 500 error")
    except requests.exceptions.Timeout:
        # Timeout is expected for build endpoint - it's a long operation
        pass  # Don't count timeout as an error for build endpoint
    except:
        pass  # Expected to fail with validation error
    
    # Test timetable generate endpoint
    try:
        response = requests.post(f'{API_BASE}/timetable/generate',
                               json={"scf_threshold": 0.5}, timeout=5)
        if response.status_code == 500:
            issues.append("Timetable generate endpoint returns 500 error")
    except:
        pass
    
    # Check for error responses in recent API calls
    endpoints = [
        f'{API_BASE}/matrix/files',
        f'{API_BASE}/matrix/data?limit=1',
        f'{API_BASE}/timetable/files'
    ]
    
    for endpoint in endpoints:
        try:
            response = requests.get(endpoint, timeout=10)
            if response.status_code >= 500:
                issues.append(f"{endpoint} returns {response.status_code}")
        except:
            pass
    
    if issues:
        print_result(False, f'Found {len(issues)} API/backend issue(s)')
        for issue in issues[:3]:
            print(f'  - {issue}')
        return False
    else:
        print_result(True, 'All API endpoints responding correctly')
        return True

def test_frontend_errors():
    """Category 6: Frontend Errors"""
    print_test('6. Frontend Errors')
    issues = []
    
    # Check frontend is serving JSON correctly
    try:
        response = requests.get('http://localhost:5174', timeout=5)
        if response.status_code == 200:
            # Check if it's HTML (good) or error
            content_type = response.headers.get('content-type', '')
            if 'text/html' not in content_type:
                issues.append(f"Unexpected content type: {content_type}")
        else:
            issues.append(f"Frontend returned {response.status_code}")
    except requests.exceptions.ConnectionError:
        issues.append("Frontend not accessible")
    except Exception as e:
        issues.append(f"Error checking frontend: {e}")
    
    # Test API response format (should be valid JSON)
    try:
        response = requests.get(f'{API_BASE}/matrix/data?limit=1', timeout=10)
        if response.status_code == 200:
            try:
                data = response.json()
                # Check for unexpected nulls in required fields
                if 'data' in data and data['data']:
                    record = data['data'][0]
                    required_fields = ['Stream', 'Instrument']
                    for field in required_fields:
                        if field in record and record[field] is None:
                            issues.append(f"Null value in required field: {field}")
            except json.JSONDecodeError:
                issues.append("API response is not valid JSON")
    except:
        pass
    
    if issues:
        print_result(False, f'Found {len(issues)} frontend issue(s)')
        for issue in issues:
            print(f'  - {issue}')
        return False
    else:
        print_result(True, 'Frontend and API communication working correctly')
        return True

def test_data_integrity_errors():
    """Category 7: Data Integrity Errors"""
    print_test('7. Data Integrity Errors')
    try:
        response = requests.get(f'{API_BASE}/matrix/data?limit=500', timeout=45)
        if response.status_code == 200:
            data = response.json()
            records = data.get('data', [])
            
            if not records:
                print_result(False, 'No data to check integrity')
                return False
            
            issues = []
            warnings = []
            
            # Check for missing required values
            required_fields = ['Stream', 'Instrument', 'Date']
            missing_count = {field: 0 for field in required_fields}
            
            # Check for invalid formats
            invalid_times = 0
            invalid_dates = 0
            invalid_profits = 0
            
            # Check data ranges
            profit_values = []
            date_values = []
            
            for record in records:
                # Check required fields
                for field in required_fields:
                    value = record.get(field) or record.get(field.lower())
                    if not value or (isinstance(value, str) and value.strip() == ''):
                        missing_count[field] += 1
                
                # Check time format
                time_val = record.get('Time') or record.get('time')
                if time_val and not isinstance(time_val, (str, type(None))):
                    invalid_times += 1
                
                # Check date format
                date_val = record.get('Date') or record.get('trade_date')
                if date_val:
                    date_values.append(date_val)
                    if not isinstance(date_val, (str, type(None))):
                        invalid_dates += 1
                
                # Check profit values
                profit = record.get('Profit') or record.get('profit')
                if profit is not None:
                    try:
                        profit_float = float(profit)
                        profit_values.append(profit_float)
                    except (ValueError, TypeError):
                        invalid_profits += 1
            
            # Report issues
            for field, count in missing_count.items():
                if count > 0:
                    pct = (count / len(records)) * 100
                    issues.append(f"{count} ({pct:.1f}%) records missing {field}")
            
            if invalid_times > 0:
                issues.append(f"{invalid_times} records with invalid time format")
            if invalid_dates > 0:
                issues.append(f"{invalid_dates} records with invalid date format")
            if invalid_profits > 0:
                issues.append(f"{invalid_profits} records with invalid profit values")
            
            # Check data ranges
            if profit_values:
                min_profit = min(profit_values)
                max_profit = max(profit_values)
                print(f'  Profit range: ${min_profit:,.2f} to ${max_profit:,.2f}')
                
                if abs(min_profit) > 100000 or abs(max_profit) > 100000:
                    warnings.append("Unusually large profit values detected")
            
            if date_values:
                print(f'  Date range: {min(date_values)[:10]} to {max(date_values)[:10]}')
            
            # Show warnings
            for warning in warnings:
                print_result(True, warning, warning=True)
            
            if issues:
                print_result(False, f'Found {len(issues)} data integrity issue(s)')
                for issue in issues[:5]:
                    print(f'  - {issue}')
                print_suggestion("Rebuild matrix to fix data integrity issues")
                return False
            else:
                print_result(True, f'Data integrity check passed for {len(records)} records')
                return True
        else:
            print_result(False, f'Cannot check integrity - API returned {response.status_code}')
            return False
    except requests.exceptions.Timeout:
        print_result(False, 'Timeout loading data for integrity check')
        print_suggestion("Try reducing the data limit or check network performance")
        return False
    except Exception as e:
        print_result(False, f'Error checking data integrity: {e}')
        return False

def test_performance_issues():
    """Category 8: Performance Issues"""
    print_test('8. Performance Issues')
    issues = []
    warnings = []
    
    # Check file sizes
    try:
        response = requests.get(f'{API_BASE}/matrix/files', timeout=10)
        if response.status_code == 200:
            files = response.json().get('files', [])
            if files:
                latest = files[0]
                size_mb = latest.get('size', 0) / (1024 * 1024)
                
                if size_mb > 100:
                    issues.append(f"Large matrix file: {size_mb:.2f} MB (may cause slow loading)")
                    print_suggestion("Consider filtering by date range or streams to reduce size")
                elif size_mb > 50:
                    print(f'  Matrix file size: {size_mb:.2f} MB (moderate)')
                    warnings.append(f"File size is moderate - consider optimization if loading is slow")
                else:
                    print(f'  Matrix file size: {size_mb:.2f} MB (good)')
    except:
        pass
    
    # Check API response times
    endpoints = [
        (f'{API_BASE}/matrix/files', 5, 'Matrix files list'),
        (f'{API_BASE}/matrix/data?limit=100', 10, 'Matrix data (100 records)'),
    ]
    
    performance_data = []
    
    for endpoint, max_time, name in endpoints:
        try:
            import time
            start = time.time()
            response = requests.get(endpoint, timeout=max_time * 2)
            elapsed = time.time() - start
            
            performance_data.append((name, elapsed, max_time))
            
            if elapsed > max_time * 1.5:
                issues.append(f"{name} took {elapsed:.2f}s (very slow, threshold: {max_time}s)")
            elif elapsed > max_time:
                warnings.append(f"{name} took {elapsed:.2f}s (slow, threshold: {max_time}s)")
            elif response.status_code != 200:
                issues.append(f"{name} returned {response.status_code}")
        except requests.exceptions.Timeout:
            issues.append(f"{name} timed out")
        except:
            pass
    
    # Print performance summary
    if performance_data:
        print('  Performance metrics:')
        for name, elapsed, threshold in performance_data:
            status = '✓' if elapsed <= threshold else '⚠'
            print(f'    {status} {name}: {elapsed:.2f}s (threshold: {threshold}s)')
    
    # Show warnings
    for warning in warnings:
        print_result(True, warning, warning=True)
    
    if issues:
        print_result(False, f'Found {len(issues)} performance issue(s)')
        for issue in issues[:3]:
            print(f'  - {issue}')
        print_suggestion("Consider optimizing queries or reducing data size")
        return False
    else:
        print_result(True, 'Performance checks passed')
        return True

def main():
    """Run all diagnostic tests"""
    print('='*60)
    print('  Master Matrix Diagnostic Test')
    print('='*60)
    print(f'Time: {datetime.now().strftime("%Y-%m-%d %H:%M:%S")}')
    print(f'API Base: {API_BASE}')
    
    # Run basic connectivity tests
    print('\n' + '='*60)
    print('  BASIC CONNECTIVITY TESTS')
    print('='*60)
    results = []
    
    results.append(('Backend Connection', test_backend_connection()))
    results.append(('Matrix Test Endpoint', test_matrix_test_endpoint()))
    results.append(('Debug Log', test_debug_log()))
    results.append(('Matrix Files', test_matrix_files()))
    results.append(('Matrix Data', test_matrix_data()))
    results.append(('Timetable Files', test_timetable_files()))
    results.append(('Frontend Port', test_frontend_port()))
    
    # Run error category tests
    print('\n' + '='*60)
    print('  ERROR CATEGORY TESTS')
    print('='*60)
    
    results.append(('1. Data Loading', test_data_loading_errors()))
    results.append(('2. Sequencer Logic', test_sequencer_logic_errors()))
    results.append(('3. Schema Normalization', test_schema_normalization_errors()))
    results.append(('4. File I/O', test_file_io_errors()))
    results.append(('5. API/Backend', test_api_backend_errors()))
    results.append(('6. Frontend', test_frontend_errors()))
    results.append(('7. Data Integrity', test_data_integrity_errors()))
    results.append(('8. Performance', test_performance_issues()))
    
    # Summary
    print_test('Test Summary')
    passed = sum(1 for _, result in results if result)
    total = len(results)
    
    for name, result in results:
        status = '✓' if result else '✗'
        print(f'  {status} {name}')
    
    print(f'\nResults: {passed}/{total} tests passed')
    
    if passed == total:
        print('\n✓ All tests passed! Matrix is working correctly.')
        print('\n💡 System is healthy and ready for use.')
        return 0
    else:
        print('\n✗ Some tests failed. Check the details above.')
        print('\n💡 Review the suggestions provided for each failed test.')
        print('   Most issues can be resolved by rebuilding the matrix or running the pipeline.')
        return 1

if __name__ == '__main__':
    try:
        exit_code = main()
        sys.exit(exit_code)
    except KeyboardInterrupt:
        print('\n\nTest interrupted by user')
        sys.exit(1)
    except Exception as e:
        print(f'\n\nUnexpected error: {e}')
        import traceback
        traceback.print_exc()
        sys.exit(1)

