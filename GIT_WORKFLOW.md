# Git Workflow - QTSW2 Only

## ⚠️ IMPORTANT: QTSW2 Repository Only

**This workspace is QTSW2. Never commit to QTSW.**

---

## ✅ Correct Workflow

### Always Work in QTSW2
```bash
cd C:\Users\jakej\QTSW2
git status    # Verify you're in QTSW2
git remote -v # Should show: https://github.com/snnn1/QTSW2.git
```

### Commits Should Only Go To QTSW2
- ✅ **QTSW2**: Data translator, DataExporter, pipeline automation
- ❌ **QTSW**: Main analyzer system (don't commit from QTSW2 workspace)

---

## 🛡️ Safety Checks

### Before Any Git Operation:

1. **Check Current Directory**
   ```bash
   pwd
   # Should show: C:\Users\jakej\QTSW2
   ```

2. **Verify Remote**
   ```bash
   git remote -v
   # Should show: origin -> https://github.com/snnn1/QTSW2.git
   ```

3. **Check Branch**
   ```bash
   git branch
   # Should show: * main (or your working branch)
   ```

---

## 📋 Standard Commits (QTSW2)

```bash
# Make sure you're in QTSW2
cd C:\Users\jakej\QTSW2

# Stage changes
git add <files>

# Commit
git commit -m "Description"

# Push to QTSW2
git push origin main
```

---

## 🚫 What NOT To Do

❌ **Don't commit QTSW2 changes from QTSW directory**
❌ **Don't push QTSW2 code to QTSW repository**
❌ **Don't mix QTSW and QTSW2 commits**

---

## 📁 Repository Separation

| Repository | Location | Purpose | Remote |
|------------|----------|---------|--------|
| **QTSW** | `C:\Users\jakej\QTSW` | Main analyzer, breakout engine | `https://github.com/snnn1/QTSW.git` |
| **QTSW2** | `C:\Users\jakej\QTSW2` | Data translator, exporter, ETL | `https://github.com/snnn1/QTSW2.git` |

---

## ✅ Verification Checklist

Before committing, verify:
- [ ] You're in `C:\Users\jakej\QTSW2` directory
- [ ] `git remote -v` shows QTSW2 repository
- [ ] Files being committed belong to QTSW2 project
- [ ] No QTSW files are staged

---

## 🔄 If You Accidentally Stage Wrong Files

```bash
# Unstage all
git reset

# Or unstage specific files
git restore --staged <file>

# Then verify before committing
git status
```

---

**Remember: QTSW2 workspace = QTSW2 repository only!**

