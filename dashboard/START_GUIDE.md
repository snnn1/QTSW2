# How to Start - PowerShell vs Batch File

## Both Work the Same!

Both files do the exact same thing - just different formats.

## Option 1: Batch File (.bat) ✅ **Easiest**

**Double-click:**
```
dashboard\START_ORCHESTRATOR.bat
```

**Or run from command prompt:**
```cmd
dashboard\START_ORCHESTRATOR.bat
```

**Pros:**
- ✅ Works everywhere (even old Windows)
- ✅ No execution policy issues
- ✅ Simple double-click

**Cons:**
- ❌ Less colorful output
- ❌ Basic error handling

## Option 2: PowerShell (.ps1) ✅ **Better Output**

**Right-click → Run with PowerShell:**
```
dashboard\START_ORCHESTRATOR.ps1
```

**Or run from PowerShell:**
```powershell
cd dashboard
.\START_ORCHESTRATOR.ps1
```

**If you get execution policy error:**
```powershell
Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope CurrentUser
```

**Pros:**
- ✅ Colored output (easier to read)
- ✅ Better error messages
- ✅ More modern

**Cons:**
- ❌ Might need to set execution policy
- ❌ Requires PowerShell (not available on very old Windows)

## Recommendation

**Use the .bat file** - it's simpler and works everywhere.

**Use the .ps1 file** if you prefer colored output and better error messages.

## Manual Start (Either Way)

If both scripts fail, just run manually:

```bash
cd dashboard\backend
python -m uvicorn main:app --reload
```

## Which One Should You Use?

- **Windows 10/11**: Either works, .ps1 has better output
- **Older Windows**: Use .bat
- **Just want it to work**: Use .bat
- **Want pretty colors**: Use .ps1

---

**TL;DR: Just double-click `START_ORCHESTRATOR.bat` - it's the easiest!**

