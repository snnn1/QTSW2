#!/usr/bin/env python3
"""
Master Matrix Startup Diagnostic Test
Tests all components of the startup process to identify issues
"""

import sys
import socket
import subprocess
import time
import requests
from pathlib import Path
from datetime import datetime

# Add project root to path
# test_startup.py is at: modules/matrix/tests/test_startup.py
# Go up 3 levels: tests -> matrix -> modules -> QTSW2_ROOT
QTSW2_ROOT = Path(__file__).parent.parent.parent.parent
sys.path.insert(0, str(QTSW2_ROOT))

def print_test(name):
    """Print test header"""
    print(f'\n{"="*60}')
    print(f'TEST: {name}')
    print('='*60)

def print_result(success, message, details=None, warning=False):
    """Print test result"""
    if warning:
        status = '[WARN]'
    else:
        status = '[PASS]' if success else '[FAIL]'
    print(f'{status}: {message}')
    if details:
        print(f'  Details: {details}')

def test_project_structure():
    """Test 1: Project structure and paths"""
    print_test('1. Project Structure')
    issues = []
    
    # Check project root
    if not QTSW2_ROOT.exists():
        issues.append(f"Project root not found: {QTSW2_ROOT}")
    else:
        print_result(True, f'Project root exists: {QTSW2_ROOT}')
    
    # Check matrix module
    matrix_module = QTSW2_ROOT / "modules" / "matrix"
    if not matrix_module.exists():
        issues.append(f"Matrix module not found: {matrix_module}")
    else:
        print_result(True, f'Matrix module exists: {matrix_module}')
    
    # Check required files
    required_files = [
        "modules/matrix/master_matrix.py",
        "modules/matrix/api.py",
        "modules/dashboard/backend/main.py",
        "modules/matrix_timetable_app/frontend/package.json"
    ]
    
    for file_path in required_files:
        full_path = QTSW2_ROOT / file_path
        if not full_path.exists():
            issues.append(f"Required file missing: {file_path}")
        else:
            print_result(True, f'File exists: {file_path}')
    
    if issues:
        print_result(False, f'Found {len(issues)} structure issue(s)')
        for issue in issues:
            print(f'  - {issue}')
        return False
    return True

def test_python_imports():
    """Test 2: Python module imports"""
    print_test('2. Python Module Imports')
    issues = []
    
    # Test matrix module import
    try:
        from modules.matrix import MasterMatrix
        print_result(True, 'MasterMatrix import successful')
    except ImportError as e:
        issues.append(f"MasterMatrix import failed: {e}")
        print_result(False, f'MasterMatrix import failed: {e}')
    
    # Test matrix API import
    try:
        from modules.matrix.api import router
        print_result(True, 'Matrix API router import successful')
    except ImportError as e:
        issues.append(f"Matrix API import failed: {e}")
        print_result(False, f'Matrix API import failed: {e}')
    
    # Test backend main import
    try:
        sys.path.insert(0, str(QTSW2_ROOT / "modules" / "dashboard" / "backend"))
        import main
        print_result(True, 'Backend main import successful')
    except ImportError as e:
        issues.append(f"Backend main import failed: {e}")
        print_result(False, f'Backend main import failed: {e}')
    except Exception as e:
        issues.append(f"Backend main import error: {e}")
        print_result(False, f'Backend main import error: {e}')
    
    if issues:
        print_result(False, f'Found {len(issues)} import issue(s)')
        return False
    return True

def test_port_availability():
    """Test 3: Port availability"""
    print_test('3. Port Availability')
    issues = []
    
    # Check backend ports
    for port in [8000, 8001, 8002, 8003, 8004]:
        sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        result = sock.connect_ex(('localhost', port))
        sock.close()
        
        if result == 0:
            print_result(True, f'Port {port} is in use (backend may be running)', warning=True)
        else:
            print_result(True, f'Port {port} is available')
    
    # Check frontend port
    frontend_port = 5174
    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    result = sock.connect_ex(('localhost', frontend_port))
    sock.close()
    
    if result == 0:
        print_result(True, f'Port {frontend_port} is in use (frontend may be running)', warning=True)
    else:
        print_result(True, f'Port {frontend_port} is available')
    
    return True

def test_backend_startup():
    """Test 4: Backend startup process"""
    print_test('4. Backend Startup Process')
    issues = []
    
    # Check if backend is already running
    try:
        response = requests.get('http://localhost:8000/', timeout=2)
        if response.status_code == 200:
            print_result(True, 'Backend is already running on port 8000')
            return True
    except:
        pass
    
    # Try other ports
    for port in [8001, 8002, 8003]:
        try:
            response = requests.get(f'http://localhost:{port}/', timeout=2)
            if response.status_code == 200:
                print_result(True, f'Backend is already running on port {port}')
                return True
        except:
            pass
    
    print_result(True, 'Backend is not running (will start when batch file runs)')
    return True

def test_batch_file():
    """Test 5: Batch file existence and structure"""
    print_test('5. Batch File Configuration')
    issues = []
    
    batch_file = QTSW2_ROOT / "modules" / "matrix" / "batch" / "RUN_MASTER_MATRIX.bat"
    if not batch_file.exists():
        issues.append(f"Batch file not found: {batch_file}")
        print_result(False, f'Batch file not found: {batch_file}')
    else:
        print_result(True, f'Batch file exists: {batch_file}')
        
        # Check batch file content
        try:
            with open(batch_file, 'r', encoding='utf-8') as f:
                content = f.read()
                
            # Check for key components
            checks = {
                'Port checking logic': 'check_port' in content or 'netstat' in content,
                'Backend startup': 'uvicorn' in content,
                'Frontend startup': 'npm run dev' in content,
                'Project root': 'PROJECT_ROOT' in content
            }
            
            for check_name, check_result in checks.items():
                if check_result:
                    print_result(True, f'{check_name} found in batch file')
                else:
                    issues.append(f"{check_name} missing from batch file")
                    print_result(False, f'{check_name} missing from batch file')
        except Exception as e:
            issues.append(f"Error reading batch file: {e}")
            print_result(False, f'Error reading batch file: {e}')
    
    if issues:
        print_result(False, f'Found {len(issues)} batch file issue(s)')
        return False
    return True

def test_frontend_config():
    """Test 6: Frontend configuration"""
    print_test('6. Frontend Configuration')
    issues = []
    
    frontend_dir = QTSW2_ROOT / "modules" / "matrix_timetable_app" / "frontend"
    if not frontend_dir.exists():
        issues.append(f"Frontend directory not found: {frontend_dir}")
        print_result(False, f'Frontend directory not found: {frontend_dir}')
        return False
    
    # Check package.json
    package_json = frontend_dir / "package.json"
    if not package_json.exists():
        issues.append(f"package.json not found: {package_json}")
        print_result(False, f'package.json not found')
    else:
        print_result(True, f'package.json exists')
        
        # Check if node_modules exists
        node_modules = frontend_dir / "node_modules"
        if not node_modules.exists():
            issues.append("node_modules not found - run 'npm install' first")
            print_result(False, 'node_modules not found - dependencies not installed', warning=True)
        else:
            print_result(True, 'node_modules exists')
    
    # Check vite config
    vite_config = frontend_dir / "vite.config.js"
    if not vite_config.exists():
        issues.append(f"vite.config.js not found: {vite_config}")
        print_result(False, f'vite.config.js not found')
    else:
        print_result(True, f'vite.config.js exists')
        
        # Check if port 5174 is configured
        try:
            with open(vite_config, 'r', encoding='utf-8') as f:
                content = f.read()
                if '5174' in content:
                    print_result(True, 'Port 5174 configured in vite.config.js')
                else:
                    issues.append("Port 5174 not found in vite.config.js")
                    print_result(False, 'Port 5174 not configured')
        except Exception as e:
            issues.append(f"Error reading vite.config.js: {e}")
    
    # Check App.jsx for API configuration
    app_jsx = frontend_dir / "src" / "App.jsx"
    if app_jsx.exists():
        try:
            with open(app_jsx, 'r', encoding='utf-8') as f:
                content = f.read()
                if 'VITE_API_PORT' in content or 'API_PORT' in content:
                    print_result(True, 'API port configuration found in App.jsx')
                else:
                    issues.append("API port configuration not found in App.jsx")
                    print_result(False, 'API port configuration missing')
        except Exception as e:
            issues.append(f"Error reading App.jsx: {e}")
    
    if issues:
        print_result(False, f'Found {len(issues)} frontend configuration issue(s)')
        return False
    return True

def test_environment_variables():
    """Test 7: Environment variable handling"""
    print_test('7. Environment Variable Configuration')
    issues = []
    
    # Check if VITE_API_PORT can be set
    import os
    test_port = "8001"
    os.environ['VITE_API_PORT'] = test_port
    
    # Verify it's set
    if os.environ.get('VITE_API_PORT') == test_port:
        print_result(True, 'VITE_API_PORT can be set as environment variable')
    else:
        issues.append("VITE_API_PORT cannot be set")
        print_result(False, 'VITE_API_PORT cannot be set')
    
    # Clean up
    if 'VITE_API_PORT' in os.environ:
        del os.environ['VITE_API_PORT']
    
    return len(issues) == 0

def test_dependencies():
    """Test 8: Python and Node dependencies"""
    print_test('8. Dependencies Check')
    issues = []
    
    # Check Python packages
    python_packages = ['fastapi', 'uvicorn', 'pandas', 'pydantic']
    for package in python_packages:
        try:
            __import__(package)
            print_result(True, f'Python package {package} is installed')
        except ImportError:
            issues.append(f"Python package {package} not installed")
            print_result(False, f'Python package {package} not installed')
    
    # Check Node.js
    try:
        result = subprocess.run(['node', '--version'], 
                              capture_output=True, 
                              text=True, 
                              timeout=5)
        if result.returncode == 0:
            print_result(True, f'Node.js is installed: {result.stdout.strip()}')
        else:
            issues.append("Node.js not working properly")
            print_result(False, 'Node.js not working properly')
    except FileNotFoundError:
        issues.append("Node.js not installed")
        print_result(False, 'Node.js not installed')
    except Exception as e:
        issues.append(f"Error checking Node.js: {e}")
        print_result(False, f'Error checking Node.js: {e}')
    
    # Check npm (try both direct call and through cmd)
    npm_found = False
    try:
        result = subprocess.run(['npm', '--version'], 
                              capture_output=True, 
                              text=True, 
                              timeout=5,
                              shell=True)
        if result.returncode == 0:
            print_result(True, f'npm is installed: {result.stdout.strip()}')
            npm_found = True
    except:
        pass
    
    # Try alternative method
    if not npm_found:
        try:
            result = subprocess.run(['cmd', '/c', 'npm', '--version'], 
                                  capture_output=True, 
                                  text=True, 
                                  timeout=5)
            if result.returncode == 0:
                print_result(True, f'npm is installed: {result.stdout.strip()}')
                npm_found = True
        except:
            pass
    
    if not npm_found:
        issues.append("npm not found in PATH (may still work if Node.js is installed)")
        print_result(False, 'npm not found in PATH', warning=True)
    
    if issues:
        print_result(False, f'Found {len(issues)} dependency issue(s)')
        return False
    return True

def test_data_directories():
    """Test 9: Data directories"""
    print_test('9. Data Directories')
    issues = []
    
    required_dirs = [
        "data/analyzed",
        "data/master_matrix",
        "logs"
    ]
    
    for dir_path in required_dirs:
        full_path = QTSW2_ROOT / dir_path
        if not full_path.exists():
            # Try to create it
            try:
                full_path.mkdir(parents=True, exist_ok=True)
                print_result(True, f'Created directory: {dir_path}')
            except Exception as e:
                issues.append(f"Cannot create directory {dir_path}: {e}")
                print_result(False, f'Directory missing and cannot create: {dir_path}')
        else:
            print_result(True, f'Directory exists: {dir_path}')
    
    if issues:
        print_result(False, f'Found {len(issues)} directory issue(s)')
        return False
    return True

def main():
    """Run all startup diagnostic tests"""
    print('='*60)
    print('  Master Matrix Startup Diagnostic Test')
    print('='*60)
    print(f'Time: {datetime.now().strftime("%Y-%m-%d %H:%M:%S")}')
    print(f'Project Root: {QTSW2_ROOT}')
    
    results = []
    
    # Run all tests
    results.append(('1. Project Structure', test_project_structure()))
    results.append(('2. Python Imports', test_python_imports()))
    results.append(('3. Port Availability', test_port_availability()))
    results.append(('4. Backend Startup', test_backend_startup()))
    results.append(('5. Batch File', test_batch_file()))
    results.append(('6. Frontend Config', test_frontend_config()))
    results.append(('7. Environment Variables', test_environment_variables()))
    results.append(('8. Dependencies', test_dependencies()))
    results.append(('9. Data Directories', test_data_directories()))
    
    # Summary
    print_test('Test Summary')
    passed = sum(1 for _, result in results if result)
    total = len(results)
    
    for name, result in results:
        status = '[PASS]' if result else '[FAIL]'
        print(f'  {status} {name}')
    
    print(f'\nResults: {passed}/{total} tests passed')
    
    if passed == total:
        print('\n[SUCCESS] All startup tests passed!')
        print('\nSystem is ready to start.')
        print('   Run: modules\\matrix\\batch\\RUN_MASTER_MATRIX.bat')
        return 0
    else:
        print('\n[ERROR] Some startup tests failed.')
        print('\nFix the issues above before starting the system.')
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

