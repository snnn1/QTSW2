# Raw Data Translator App

A user-friendly GUI application for converting raw trading data into processed, organized files.

## How to Launch

### Option 1: Double-click the App (Easiest)
- Double-click `Data Translator App.bat`
- The app will open in your web browser automatically

### Option 2: Command Line
```bash
streamlit run scripts\translate_raw_app.py
```

## What the App Does

1. **Loads raw data** from your `data_raw` folder
2. **Converts formats** from various sources (CSV, TXT, DAT)
3. **Separates by years** into individual files
4. **Creates organized output** in `data_processed` folder

## App Features

### Folder Settings
- **Input Folder**: Where your raw data files are located
- **Output Folder**: Where processed files will be saved

### Processing Options

#### Year Separation Modes:
- **Separate by years**: Creates individual files for each instrument and year (ES_2024.parquet, NQ_2024.parquet, etc.)
- **No separation**: Creates single merged file with instrument and date range (ES_NQ_2024-2025.parquet)
- **Specific years only**: Process only selected years (e.g., 2024 2025)

#### Output Format:
- **Parquet only**: Fastest option, smaller files
- **Both Parquet and CSV**: More compatible but slower

## Data Preview

The app shows:
- Number of data files found
- List of files and their sizes
- Preview of first few lines
- Data summary (rows, date range, instruments)

## Processing

1. **Click "Start Processing"** button
2. **Watch progress** in real-time
3. **See results** as files are created
4. **View summary** of what was processed

## Output Files

The app creates organized files like:
```
data_processed/
├── ES_2024.parquet        # ES 2024 data only
├── NQ_2024.parquet        # NQ 2024 data only
├── ES_2025.parquet        # ES 2025 data only
├── NQ_2025.parquet        # NQ 2025 data only
├── ES_NQ_2024-2025.parquet # Complete dataset with date range
└── ES_NQ_2024-2025.csv    # Complete dataset (if CSV enabled)
```

## Use Cases

### For Large Datasets (6M+ rows):
- Use "Specific years only" mode
- Select recent years (2024, 2025)
- Choose "Parquet only" for fastest processing

### For Analysis:
- Use "Separate by years" mode
- Choose "Both Parquet and CSV"
- Process all years for complete historical data

### For Quick Processing:
- Use "No separation" mode
- Choose "Parquet only"
- Get single merged file quickly

## Advanced Options

The app automatically:
- Detects different data formats
- Handles timezone conversion (UTC to Chicago)
- Removes duplicate entries
- Validates data integrity
- Shows processing progress
- Creates organized file structure

## Troubleshooting

### App won't start:
- Make sure Python and Streamlit are installed
- Check that `scripts/translate_raw_app.py` exists

### No data files found:
- Verify files are in the input folder
- Supported formats: .csv, .txt, .dat
- Check file permissions

### Processing errors:
- Check data file format
- Ensure sufficient disk space
- Verify folder permissions

## Support

The app is built on the same engine as the command-line tool, so all the same data formats and processing logic apply.

For command-line usage, see the original `translate_raw.py` script.

