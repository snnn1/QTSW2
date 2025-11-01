# Folder Structure Guide

## 📁 Complete Directory Layout

```
QTSW2/
│
├── 📂 translator/              # Backend Module
│   ├── __init__.py            # Module exports
│   ├── core.py                # Core processing: process_data(), root_symbol()
│   └── file_loader.py         # File ops: load_single_file(), detect_file_format()
│
├── 📂 scripts/                 # Frontend Applications
│   └── translate_raw_app.py   # Streamlit web UI (UI only, no processing logic)
│
├── 📂 tools/                   # Command-Line Tools
│   └── translate_raw.py        # CLI tool for translation
│
├── 📂 tests/                   # Unit Tests
│   ├── __init__.py
│   ├── test_core.py           # Tests for core.py functions
│   ├── test_file_loader.py    # Tests for file_loader.py functions
│   └── README.md              # Test documentation
│
├── 📂 docs/                    # Documentation
│   ├── DATA_TRANSLATOR_README.md  # Usage guide
│   ├── WHAT_IT_DOES.md        # System explanation
│   └── FOLDER_STRUCTURE.md    # This file
│
├── 📂 data/                    # Data Storage
│   ├── raw/                   # Input: Place raw data files here
│   └── processed/             # Output: Processed files saved here
│
├── 📂 batch/                   # Batch File Launchers
│   ├── Data Translator App.bat  # Launch Streamlit app
│   └── RUN_TESTS.bat          # Run unit tests
│
├── 📄 requirements.txt         # Python dependencies
├── 📄 README.md               # Main project readme
└── 📄 .gitignore              # Git ignore rules
```

## 🔍 What Goes Where

### Backend Code (`translator/`)
- **All processing logic**
- **No UI dependencies** - Can be used anywhere
- **Pure Python functions**
- **Thoroughly tested**

### Frontend Code (`scripts/`)
- **UI components only**
- **Imports from `translator/`**
- **Streamlit widgets and display**

### Data Files (`data/`)
- **`raw/`** - Drop raw CSV/TXT files here
- **`processed/`** - Clean parquet files appear here

### Batch Files (`batch/`)
- **Easy launchers** - Double-click to run
- **Set working directory correctly**

## 🎯 Principles

1. **Separation of Concerns**
   - Backend = Logic
   - Frontend = UI
   - Tests = Verification

2. **Reusability**
   - Backend can be used by multiple frontends
   - CLI and Web app share same logic

3. **Testability**
   - Backend functions are easy to test
   - Tests in dedicated folder

4. **Organization**
   - Related files grouped together
   - Clear naming conventions
   - Documentation in `docs/`

