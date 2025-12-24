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
QTSW2_ROOT = Path(__file__).parent.parent.parent
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
        print('  Hint: Start backend with: modules\\matrix\\batch\\RUN_MASTER_MATRIX.bat')
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
    results.append(('Matrix Files', test_matrix_files()))
    results.append(('Matrix Data', test_matrix_data()))
    
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
        return 0
    else:
        print('\n✗ Some tests failed. Check the details above.')
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


