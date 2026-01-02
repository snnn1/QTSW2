# Parity Audit Snapshot System

## Overview

The Parity Audit Snapshot system ensures that parity audits run against frozen, immutable market data snapshots, preventing any mutation or drift of production translated data during testing.

## Architecture

### Snapshot Directory

```
data/translated_test/
├── MANIFEST.json          # Snapshot metadata (required)
├── README.md              # Read-only notice
└── {instrument}/
    └── 1m/
        └── {year}/
            └── {month}/
                └── {instrument}_1m_{date}.parquet
```

### Safety Guarantees

1. **Read-Only Snapshot**: `data/translated_test/` is marked as read-only
2. **Production Data Protection**: Parity runs never read from `data/translated/`
3. **Date Range Validation**: Snapshot dates must match requested audit dates
4. **Manifest Required**: Snapshot must have `MANIFEST.json` to be valid

## Workflow

### Step 1: Create Snapshot

```bash
# Create snapshot for 30 trading days
python scripts/robot/create_translated_snapshot.py --start 2025-01-01 --end 2025-02-15

# Create snapshot for specific instruments
python scripts/robot/create_translated_snapshot.py --start 2025-01-01 --end 2025-02-15 --instruments ES,NQ
```

The snapshot utility:
- Copies files whose timestamps fall within the date range
- Preserves exact directory structure
- Creates `MANIFEST.json` with metadata
- Marks snapshot as read-only

### Step 2: Run Parity Audit

```bash
# Run parity audit (automatically uses snapshot)
python scripts/robot/run_parity_audit.py --start 2025-01-01 --end 2025-02-15
```

The parity audit runner:
- Validates snapshot exists and matches date range
- Ensures Analyzer reads from snapshot (not production)
- Ensures Robot reads from snapshot (not production)
- Writes all outputs to run-specific directory

## Snapshot Manifest

`MANIFEST.json` contains:

```json
{
  "start_date": "2025-01-01",
  "end_date": "2025-02-15",
  "instruments": ["ES", "NQ", "CL"],
  "total_files": 45,
  "total_rows": 1234567,
  "source_path": "data/translated",
  "created_at": "2026-01-01T12:00:00",
  "commit_hash": "abc123...",
  "read_only": true,
  "note": "This snapshot is read-only..."
}
```

## Safety Checks

### Snapshot Validation

The parity audit runner performs these checks:

1. **Snapshot Exists**: `data/translated_test/` must exist
2. **Manifest Exists**: `MANIFEST.json` must be present
3. **Date Range Match**: Snapshot dates must match requested dates
4. **Production Data Protection**: Analyzer/Robot must not read from `data/translated/`

### Fail-Closed Behavior

If any safety check fails:
- Audit aborts immediately
- Clear error message explains the issue
- Instructions provided to fix the problem

## Integration Points

### Analyzer Integration

When running in parity mode:
- **Input**: Reads from `data/translated_test/` (snapshot)
- **Output**: Writes to `docs/robot/parity_runs/{run_id}/analyzer_output/`
- **Never**: Writes to `data/analyzed/`, `data/matrix/`, or production folders

### Robot Integration

When running in parity mode:
- **Input**: Reads bars from `data/translated_test/` (snapshot)
- **Output**: Writes logs to `docs/robot/parity_runs/{run_id}/robot_logs/`
- **Never**: Writes to production logs or `timetable_current.json`

## Reproducibility

Snapshots ensure:
- **Same Inputs**: All parity runs use identical market data
- **No Drift**: Production data updates don't affect parity results
- **Audit Trail**: `MANIFEST.json` records snapshot provenance

## Best Practices

1. **Create Snapshot Once**: Create snapshot before first parity run
2. **Reuse Snapshot**: Use the same snapshot for multiple parity runs
3. **Don't Delete**: Keep snapshots as evidence chain
4. **Version Control**: Consider committing `MANIFEST.json` (not the data files)

## Troubleshooting

### "Snapshot directory not found"

**Solution**: Create snapshot first:
```bash
python scripts/robot/create_translated_snapshot.py --start YYYY-MM-DD --end YYYY-MM-DD
```

### "MANIFEST.json not found"

**Solution**: Snapshot is invalid. Recreate it.

### "Snapshot date range mismatch"

**Solution**: Create a new snapshot with matching dates, or use the snapshot's dates for the audit.

### "SAFETY CHECK FAILED: Parity run attempted to read from production data"

**Solution**: This should never happen if using the parity audit runner. Check that `data_source_dir` is set correctly.

## Related Documentation

- `docs/robot/PARITY_AUDIT.md` - Parity audit workflow
- `scripts/robot/create_translated_snapshot.py` - Snapshot creation script
- `scripts/robot/run_parity_audit.py` - Parity audit runner
