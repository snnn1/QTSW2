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

## 📋 Standard Workflow (QTSW2)

### Default Branch: **dev**

**Always work on `dev` branch for new features and changes.**

```bash
# Make sure you're in QTSW2
cd C:\Users\jakej\QTSW2

# Verify you're on dev branch
git branch
# Should show: * dev

# Stage changes
git add <files>

# Commit to dev
git commit -m "Description"

# Push to dev branch
git push origin dev
```

### Merge to main (when feature is done and tested):

```bash
# Switch to main
git checkout main

# Pull latest main
git pull origin main

# Merge dev into main
git merge dev

# Push to main
git push origin main

# Switch back to dev for continued work
git checkout dev
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
- [ ] You're on `dev` branch (check with `git branch`)
- [ ] `git remote -v` shows QTSW2 repository
- [ ] Files being committed belong to QTSW2 project
- [ ] No QTSW files are staged

## 🔀 Branch Strategy

- **`dev`**: Development branch - all new work happens here
- **`main`**: Production branch - only merge tested features from `dev`

**Rule:** When asked to choose between main or dev, always choose **dev**.

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

