# QTSW2 Project Analysis - What We Need

## 📋 Project Overview

**QTSW2** is a quantitative trading data pipeline system with:
- **Data Translator**: Converts raw trading data to standardized formats
- **Pipeline Dashboard**: Real-time web dashboard for monitoring and control
- **Automated Scheduler**: Runs data pipeline on schedule
- **Breakout Analyzer**: Analyzes trading data
- **Master Matrix**: Matrix and timetable engine
- **Multiple Streamlit Apps**: Translator, Analyzer, Sequential Processor

---

## ✅ What's Already in Place

### 1. **Python Dependencies** (`requirements.txt`)
- ✅ Core: numpy, pandas, pyarrow, python-dateutil, pytz, tzdata
- ✅ Web: streamlit, fastapi, uvicorn, websockets
- ✅ Testing: pytest, pytest-cov

### 2. **Frontend Dependencies** (`dashboard/frontend/package.json`)
- ✅ React 18.2.0
- ✅ Vite 5.0.8
- ✅ Tailwind CSS 3.3.6
- ✅ **node_modules exists** (dependencies installed)

### 3. **Configuration Files**
- ✅ `automation/schedule_config.json` - Schedule configuration
- ✅ `.gitignore` - Git ignore rules
- ✅ `dashboard/frontend/vite.config.js` - Vite config
- ✅ `dashboard/frontend/tailwind.config.js` - Tailwind config
- ✅ `dashboard/frontend/postcss.config.js` - PostCSS config

### 4. **Directory Structure**
- ✅ `dashboard/backend/` - FastAPI backend
- ✅ `dashboard/frontend/` - React frontend
- ✅ `automation/` - Scheduler and pipeline
- ✅ `translator/` - Data translation logic
- ✅ `scripts/` - Streamlit apps
- ✅ `tools/` - CLI tools
- ✅ `data/` - Data directories (720 files)
- ✅ `logs/` - Log files
- ✅ `batch/` - Batch launcher scripts

### 5. **Batch Files for Easy Launch**
- ✅ `batch/START_DASHBOARD.bat` - Start dashboard
- ✅ `batch/START_ORCHESTRATOR.bat` - Start orchestrator
- ✅ `batch/RUN_*` - Various component launchers

---

## ⚠️ Potential Issues & Missing Items

### 1. **Environment Variables / Configuration**
- ❓ **No `.env` file found** - May need environment variables for:
  - API keys (if using external data sources)
  - Database connections (if applicable)
  - Port configurations (currently hardcoded)
  - Path configurations (some hardcoded in code)

### 2. **Hardcoded Paths**
Found in `automation/daily_data_pipeline_scheduler.py`:
```python
QTSW_ROOT = Path(r"C:\Users\jakej\QTSW")  # Hardcoded
QTSW2_ROOT = Path(r"C:\Users\jakej\QTSW2")  # Hardcoded
```
- ⚠️ These should use relative paths or environment variables

### 3. **Port Configuration**
- Backend: Port **8001** (in START_DASHBOARD.bat) vs **8000** (in docs)
- Frontend: Port **5173** (standard Vite)
- ⚠️ **Inconsistency**: Batch file uses 8001, documentation says 8000

### 4. **Missing Documentation**
- ❓ No comprehensive setup guide at root level
- ❓ No troubleshooting guide for common issues
- ❓ No architecture diagram
- ❓ No API documentation (though FastAPI provides `/docs`)

### 5. **Python Package Structure**
- ❓ No `setup.py` or `pyproject.toml` for proper package installation
- ❓ No virtual environment setup instructions
- ❓ No version pinning for critical dependencies

### 6. **Data Directory Structure**
- ✅ `data/` exists with 720 files
- ❓ May need `.gitkeep` files to preserve structure (mentioned in .gitignore)
- ❓ No clear documentation on data format requirements

### 7. **Logging Configuration**
- ✅ `automation/logs/` exists (497 files)
- ❓ No centralized logging configuration
- ❓ No log rotation strategy documented

### 8. **Testing**
- ✅ `tests/` directory exists
- ❓ No CI/CD configuration
- ❓ No test coverage reports visible

---

## 🔧 What We Should Add/Improve

### High Priority

1. **Create `.env.example` file**
   - Template for environment variables
   - Document required vs optional variables

2. **Fix Port Inconsistency**
   - Standardize on port 8000 or 8001
   - Update all documentation and batch files

3. **Create Root-Level Setup Guide**
   - Quick start instructions
   - Prerequisites checklist
   - Installation steps
   - Common issues and solutions

4. **Add Virtual Environment Setup**
   - Instructions for creating venv
   - Activation scripts for Windows

5. **Fix Hardcoded Paths**
   - Use relative paths from project root
   - Or use environment variables

### Medium Priority

6. **Add `pyproject.toml` or `setup.py`**
   - Proper Python package structure
   - Installable package

7. **Create Architecture Documentation**
   - System overview diagram
   - Component interaction diagram
   - Data flow diagram

8. **Add Logging Configuration**
   - Centralized logging setup
   - Log rotation configuration
   - Log level management

9. **Create Data Format Documentation**
   - Expected input formats
   - Output format specifications
   - Example data files

### Low Priority

10. **Add CI/CD Configuration**
    - GitHub Actions or similar
    - Automated testing
    - Deployment scripts

11. **Add Health Check Endpoints**
    - System status endpoint
    - Dependency check endpoint

12. **Add Monitoring/Alerting**
    - Error alerting
    - Performance monitoring

---

## 📝 Quick Checklist for New Setup

### Python Environment
- [ ] Python 3.8+ installed
- [ ] Virtual environment created
- [ ] `pip install -r requirements.txt` executed
- [ ] All imports work (test with `python -c "import fastapi"`)

### Node.js Environment
- [ ] Node.js 16+ installed
- [ ] `cd dashboard/frontend && npm install` executed
- [ ] Frontend dependencies installed

### Configuration
- [ ] `automation/schedule_config.json` exists
- [ ] Ports 8000/8001 and 5173 are available
- [ ] Data directories exist (`data/raw/`, `data/processed/`)
- [ ] Log directories exist (`automation/logs/events/`)

### Testing
- [ ] Backend starts: `python -m uvicorn dashboard.backend.main:app --port 8000`
- [ ] Frontend starts: `cd dashboard/frontend && npm run dev`
- [ ] Dashboard accessible at http://localhost:5173
- [ ] API docs accessible at http://localhost:8000/docs

---

## 🎯 Recommended Next Steps

1. **Create `.env.example`** with all configurable variables
2. **Fix port inconsistency** (standardize on 8000)
3. **Create `SETUP.md`** at root with comprehensive setup instructions
4. **Fix hardcoded paths** in scheduler
5. **Add virtual environment setup** instructions
6. **Create quick start script** that checks prerequisites

---

## 📊 Project Health Summary

| Category | Status | Notes |
|----------|--------|-------|
| **Python Dependencies** | ✅ Good | All in requirements.txt |
| **Frontend Dependencies** | ✅ Good | Installed, package.json complete |
| **Configuration** | ⚠️ Needs Work | Hardcoded paths, no .env |
| **Documentation** | ⚠️ Scattered | Many docs, but no central guide |
| **Testing** | ✅ Present | Tests directory exists |
| **Batch Scripts** | ✅ Good | Many launcher scripts |
| **Code Structure** | ✅ Good | Well organized |

**Overall**: Project is well-structured and mostly complete, but needs:
- Better configuration management
- Centralized documentation
- Fix for hardcoded paths and port inconsistency

