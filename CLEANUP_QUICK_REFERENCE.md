# Quick Cleanup Reference

## 🗑️ Files Safe to Delete Immediately

### Duplicate Code Files
```
dashboard/backend/routers/pipeline_new.py
dashboard/backend/routers/websocket_new.py
dashboard/backend/main_simplified.py
```

### Test/Diagnostic Files
```
dashboard/backend/test_*.py
dashboard/backend/continuous_test.py
dashboard/backend/diagnostic.py
dashboard/backend/run_with_debug.py (if exists)
```

### Historical Documentation (30+ files in dashboard/)
```
dashboard/SCHEDULER_*.md (7 files)
dashboard/REFACTORING_*.md (5 files)
dashboard/MIGRATION_*.md (2 files)
dashboard/*_FIX.md (multiple)
dashboard/TEST_RESULTS.md
dashboard/DRY_RUN_RESULTS.md
dashboard/CLEANUP_*.md
dashboard/COMPLEXITY_ANALYSIS.md
... and more (see CLEANUP_PLAN.md for full list)
```

### Old Log Files
```
automation/logs/pipeline_*.log (older than 30 days)
```

---

## 📊 Summary

- **~240+ files** can be safely deleted
- **~50-100MB** space savings
- **~30+ historical docs** to remove
- **~200+ old log files** to archive/delete

---

## ⚡ Quick PowerShell Cleanup

```powershell
# Run from project root
cd C:\Users\jakej\QTSW2

# Delete duplicate routers
Remove-Item dashboard\backend\routers\*_new.py

# Delete test files
Remove-Item dashboard\backend\test_*.py
Remove-Item dashboard\backend\continuous_test.py
Remove-Item dashboard\backend\diagnostic.py
Remove-Item dashboard\backend\main_simplified.py

# Delete old logs (30+ days)
Get-ChildItem automation\logs\pipeline_*.log | 
  Where-Object {$_.LastWriteTime -lt (Get-Date).AddDays(-30)} | 
  Remove-Item

# Clean Python cache
Get-ChildItem -Path . -Include __pycache__ -Recurse -Directory | Remove-Item -Recurse -Force
```

---

## ✅ Keep These

- All active code files
- Recent documentation (HOW_TO_START.md, SETUP.md, README.md, etc.)
- Recent logs (last 30 days)
- Configuration files
- All files in `orchestrator/` directory

---

**See `CLEANUP_PLAN.md` for detailed information and full file list.**

