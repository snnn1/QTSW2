# Unit Tests for Data Translator

## Running Tests

### Run all tests:
```bash
pytest tests/
```

### Run specific test file:
```bash
pytest tests/test_core.py
pytest tests/test_file_loader.py
```

### Run with coverage:
```bash
pytest tests/ --cov=translator --cov-report=html
```

### Run specific test:
```bash
pytest tests/test_core.py::TestRootSymbol::test_extract_es_from_minute_data_export
```

## Test Structure

### `test_core.py`
Tests for:
- `root_symbol()` - Instrument extraction from contract names
- `infer_contract_from_filename()` - Contract name extraction
- `process_data()` - Full data processing pipeline

### `test_file_loader.py`
Tests for:
- `detect_file_format()` - File format detection (CSV/TXT/DAT)
- `load_single_file()` - File loading with schema validation
- `get_file_years()` - Year extraction from timestamps
- `get_data_files()` - File discovery

## Test Coverage

The tests cover:
✅ Instrument symbol extraction  
✅ File format detection  
✅ Data schema validation  
✅ Timezone conversion (UTC → Chicago)  
✅ Duplicate removal  
✅ Year extraction  
✅ File discovery  

## Requirements

Install test dependencies:
```bash
pip install -r requirements.txt
```

This includes:
- `pytest` - Testing framework
- `pytest-cov` - Coverage reporting

