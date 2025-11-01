# QTSW2 - Data Translator System

A clean, organized system for translating raw trading data files into processed, standardized formats.

## 📁 Folder Structure

```
QTSW2/
├── translator/           # Backend processing logic
│   ├── __init__.py
│   ├── core.py          # Core processing functions
│   └── file_loader.py   # File loading and format detection
│
├── scripts/              # Frontend applications
│   └── translate_raw_app.py  # Streamlit web UI
│
├── tools/                # Command-line tools
│   └── translate_raw.py  # CLI tool for translation
│
├── tests/                # Unit tests
│   ├── test_core.py
│   └── test_file_loader.py
│
├── docs/                 # Documentation
│   ├── DATA_TRANSLATOR_README.md
│   └── WHAT_IT_DOES.md
│
├── data/                 # Data folders (created when needed)
│   ├── raw/             # Raw input files
│   └── processed/       # Processed output files
│
├── batch/                # Batch file launchers
│   ├── Data Translator App.bat
│   └── RUN_TESTS.bat
│
├── requirements.txt      # Python dependencies
└── README.md           # This file
```

## 🚀 Quick Start

### 1. Install Dependencies
```bash
pip install -r requirements.txt
```

### 2. Run the Web App
Double-click: `batch/Data Translator App.bat`

Or command line:
```bash
streamlit run scripts/translate_raw_app.py
```

### 3. Run CLI Tool
```bash
python tools/translate_raw.py --input data/raw --output data/processed
```

### 4. Run Tests
Double-click: `batch/RUN_TESTS.bat`

Or command line:
```bash
pytest tests/ -v
```

## 📚 Documentation

- **Data Translator Overview**: `docs/WHAT_IT_DOES.md`
- **Usage Guide**: `docs/DATA_TRANSLATOR_README.md`
- **Tests Guide**: `tests/README.md`

## 🏗️ Architecture

### Backend (`translator/`)
- **Pure Python functions** - No UI dependencies
- **Reusable** - Can be used by CLI, web app, or other tools
- **Testable** - All functions have unit tests

### Frontend (`scripts/`)
- **Streamlit web interface** - User-friendly GUI
- **Calls backend functions** - Clean separation

### CLI Tool (`tools/`)
- **Command-line interface** - For automation/scripts
- **Can use backend** - Or has its own implementation

## 📝 Data Folders

Create these folders as needed:
- `data/raw/` - Place raw data files here
- `data/processed/` - Processed files will be saved here

## 🧪 Testing

Run all tests:
```bash
pytest tests/ -v
```

Run specific test:
```bash
pytest tests/test_core.py::TestRootSymbol::test_extract_es_from_minute_data_export
```

## 🔧 Development

### Backend Functions
Located in `translator/`:
- `core.py` - Main processing logic
- `file_loader.py` - File operations

### Frontend App
Located in `scripts/translate_raw_app.py`
- Only UI code - imports from `translator`

## 📋 Requirements

See `requirements.txt` for all dependencies.

