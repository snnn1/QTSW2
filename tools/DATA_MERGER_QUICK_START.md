# Data Merger Quick Start

## Quick Run

```bash
# Windows
batch\RUN_DATA_MERGER.bat

# Or directly
python modules\merger\merger.py
```

## What It Does

1. **Scans** `data/analyzer_temp/YYYY-MM-DD/` and `data/sequencer_temp/YYYY-MM-DD/`
2. **Merges** daily files into monthly files:
   - `data/analyzer_runs/<instrument>/<year>/<instrument>_an_<year>_<month>.parquet`
   - `data/sequencer_runs/<instrument>/<year>/<instrument>_seq_<year>_<month>.parquet`
3. **Removes duplicates** and **sorts** data
4. **Deletes** processed daily temp folders
5. **Tracks** processed folders (idempotent)

## Folder Structure Created

```
data/
├── analyzer_temp/          ✓ Created
├── sequencer_temp/         ✓ Created
├── analyzer_runs/          ✓ Exists
│   └── <instrument>/
│       └── <year>/
└── sequencer_runs/         ✓ Exists
    └── <instrument>/
        └── <year>/
```

## Example Output

After processing daily files:
```
data/analyzer_runs/ES/2025/ES_an_2025_01.parquet
data/analyzer_runs/NQ/2025/NQ_an_2025_01.parquet
data/sequencer_runs/ES/2025/ES_seq_2025_01.parquet
```

## Logs

- Console output: Real-time processing status
- Log file: `data_merger.log` (in project root)
- Processed log: `data/merger_processed.json` (tracks processed folders)

## Notes

- **Idempotent**: Safe to run multiple times
- **Automatic**: Detects instruments, handles errors, skips corrupted files
- **Clean**: Deletes temp folders after successful merge

