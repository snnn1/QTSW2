# Folder Organization Plan

## Current Root Level Files (24 files)

### Test Files (5 files)
- `test_full_pipeline.py`
- `test_pipeline_data_range.py`
- `test_pipeline_stress.py`
- `test_pipeline_batch.bat`
- `test_stress_results.json`

### Utility Scripts (4 files)
- `cleanup_and_reset_jsonl.py`
- `reduce_jsonl_file.py`
- `restart_backend.py`
- `run_matrix_and_timetable.py`

### PowerShell Scripts (2 files)
- `check_duplicate_tasks.ps1`
- `FIX_TASK_PERMISSIONS.ps1`

### Documentation (12 files)
- `COMPLETE_CLEANUP_SUMMARY.md`
- `CURRENT_SYSTEM_EXPLANATION.md`
- `GIT_WORKFLOW.md`
- `MASTER_MATRIX_AND_TIMETABLE_README.md`
- `OBSOLETE_CODE_REMOVED.md`
- `PROJECT_ANALYSIS.md`
- `README.md` ⭐ (keep in root)
- `SCHEDULER_ARCHITECTURE.md`
- `SCHEDULER_COMMUNICATION_FLOW.md`
- `SYSTEM_COMPLETE_RUNDOWN.md`

### Other (3 files)
- `QUICK_CHECK.bat`
- `requirements.txt` ⭐ (keep in root)
- `.gitignore` ⭐ (keep in root)

---

## Proposed Folder Structure

### 1. Create `tests/integration/` folder
**Move:**
- `test_full_pipeline.py`
- `test_pipeline_data_range.py`
- `test_pipeline_stress.py`
- `test_pipeline_batch.bat`
- `test_stress_results.json`

**Reason**: These are integration/end-to-end tests, separate from unit tests

### 2. Create `scripts/maintenance/` folder
**Move:**
- `cleanup_and_reset_jsonl.py`
- `reduce_jsonl_file.py`
- `restart_backend.py`
- `run_matrix_and_timetable.py`

**Reason**: These are maintenance/utility scripts

### 3. Create `scripts/system/` folder (or keep in root)
**Move:**
- `check_duplicate_tasks.ps1`
- `FIX_TASK_PERMISSIONS.ps1`

**Reason**: System-level PowerShell scripts

**Alternative**: Keep in root if they're system-level utilities

### 4. Create `docs/system/` folder
**Move:**
- `COMPLETE_CLEANUP_SUMMARY.md`
- `CURRENT_SYSTEM_EXPLANATION.md`
- `PROJECT_ANALYSIS.md`
- `SYSTEM_COMPLETE_RUNDOWN.md`
- `OBSOLETE_CODE_REMOVED.md`

**Reason**: System-level documentation

### 5. Create `docs/scheduler/` folder
**Move:**
- `SCHEDULER_ARCHITECTURE.md`
- `SCHEDULER_COMMUNICATION_FLOW.md`

**Reason**: Scheduler-specific documentation

### 6. Keep in Root
- `README.md` ⭐ (main project readme)
- `requirements.txt` ⭐ (Python dependencies)
- `.gitignore` ⭐ (git configuration)
- `MASTER_MATRIX_AND_TIMETABLE_README.md` (or move to `docs/`)
- `GIT_WORKFLOW.md` (or move to `docs/`)
- `QUICK_CHECK.bat` (quick utility, can stay in root)

---

## Final Proposed Structure

```
QTSW2/
├── README.md                    ⭐ (keep)
├── requirements.txt             ⭐ (keep)
├── .gitignore                   ⭐ (keep)
├── QUICK_CHECK.bat              (quick utility)
│
├── tests/
│   ├── integration/             🆕 NEW
│   │   ├── test_full_pipeline.py
│   │   ├── test_pipeline_data_range.py
│   │   ├── test_pipeline_stress.py
│   │   ├── test_pipeline_batch.bat
│   │   └── test_stress_results.json
│   └── [existing unit tests]
│
├── scripts/
│   ├── maintenance/             🆕 NEW
│   │   ├── cleanup_and_reset_jsonl.py
│   │   ├── reduce_jsonl_file.py
│   │   ├── restart_backend.py
│   │   └── run_matrix_and_timetable.py
│   ├── system/                  🆕 NEW (or keep in root)
│   │   ├── check_duplicate_tasks.ps1
│   │   └── FIX_TASK_PERMISSIONS.ps1
│   └── [existing scripts]
│
├── docs/
│   ├── system/                  🆕 NEW
│   │   ├── COMPLETE_CLEANUP_SUMMARY.md
│   │   ├── CURRENT_SYSTEM_EXPLANATION.md
│   │   ├── PROJECT_ANALYSIS.md
│   │   ├── SYSTEM_COMPLETE_RUNDOWN.md
│   │   └── OBSOLETE_CODE_REMOVED.md
│   ├── scheduler/               🆕 NEW
│   │   ├── SCHEDULER_ARCHITECTURE.md
│   │   └── SCHEDULER_COMMUNICATION_FLOW.md
│   └── [existing docs]
│
└── [all other folders unchanged]
```

---

## Benefits

1. **Cleaner Root**: Only essential files (README, requirements.txt, .gitignore)
2. **Better Organization**: Related files grouped together
3. **Easier Navigation**: Clear folder structure
4. **Maintainability**: Easier to find and manage files

---

## Implementation

Should I proceed with creating these folders and moving the files?

