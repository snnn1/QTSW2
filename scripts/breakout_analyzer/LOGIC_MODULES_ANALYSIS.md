# Logic Modules Analysis - Active vs Legacy

## Summary
**Total Modules:** 15 (after cleanup + new DataManager)  
**Actively Used:** 15  
**Legacy/Unused:** 0 (all removed)

---

## ✅ ACTIVELY USED MODULES (14)

### Core Engine Modules (Used in `breakout_core/engine.py`):
1. **`config_logic.py`** ✅
   - **Status:** Core - Used everywhere
   - **Usage:** `RunParams`, `ConfigManager`
   - **Imported by:** engine.py, all scripts, tests

2. **`utility_logic.py`** ✅
   - **Status:** Core - Utility functions
   - **Usage:** `UtilityManager`
   - **Imported by:** engine.py

3. **`validation_logic.py`** ✅
   - **Status:** Core - Data validation
   - **Usage:** `ValidationManager`
   - **Imported by:** engine.py

4. **`instrument_logic.py`** ✅
   - **Status:** Core - Instrument handling
   - **Usage:** `InstrumentManager`
   - **Imported by:** engine.py

5. **`time_logic.py`** ✅
   - **Status:** Core - Time utilities
   - **Usage:** `TimeManager`
   - **Imported by:** engine.py

6. **`debug_logic.py`** ✅
   - **Status:** Core - Debug output
   - **Usage:** `DebugManager`
   - **Imported by:** engine.py

7. **`range_logic.py`** ✅
   - **Status:** Core - Range detection
   - **Usage:** `RangeDetector`, `SlotRange`
   - **Imported by:** engine.py

8. **`entry_logic.py`** ✅
   - **Status:** Core - Entry detection
   - **Usage:** `EntryDetector`, `EntryResult`
   - **Imported by:** engine.py, tests

9. **`price_tracking_logic.py`** ✅
   - **Status:** Core - Trade execution
   - **Usage:** `PriceTracker`, `TradeExecution`
   - **Imported by:** engine.py
   - **Note:** Integrates MFE and break-even logic

10. **`result_logic.py`** ✅
    - **Status:** Core - Result formatting
    - **Usage:** `ResultProcessor`
    - **Imported by:** engine.py

11. **`loss_logic.py`** ✅
    - **Status:** Core - Stop loss calculation
    - **Usage:** `LossManager`, `StopLossConfig`
    - **Imported by:** engine.py, entry_logic.py

### Supporting Modules (Used by core modules):
12. **`execution_logic.py`** ✅
    - **Status:** Active - Trade execution simulation
    - **Usage:** `ExecutionSimulator`
    - **Imported by:** price_tracking_logic.py
    - **Note:** Extracted from price_tracking_logic during refactoring

13. **`break_even_logic.py`** ✅
    - **Status:** Active - Break-even management
    - **Usage:** `BreakEvenManager`
    - **Imported by:** price_tracking_logic.py
    - **Note:** Handles T1 trigger and stop loss adjustment

14. **`mfe_logic.py`** ⚠️
    - **Status:** Partially Active - Standalone MFE calculator
    - **Usage:** `MFECalculator`, `MFEResult`
    - **Imported by:** None (standalone module)
    - **Note:** MFE calculation is now integrated into `price_tracking_logic.py`, but this module still exists as standalone option

15. **`data_logic.py`** ✅ NEW
    - **Status:** Active - Centralized data management
    - **Usage:** `DataManager`
    - **Imported by:** To be integrated (replaces scattered data operations)
    - **Note:** Consolidates data loading, cleaning, timezone normalization, session cutting, outlier detection, and missing bar reconstruction

---

## ⚠️ LEGACY/UNUSED MODULES (7) - **REMOVED**

### Time Slot Switching (Disabled Feature):
15. **`time_slot_logic.py`** ⚠️
    - **Status:** Legacy - Time slot switching disabled
    - **Usage:** `TimeSlotManager`
    - **Imported by:** analyzer_time_slot_integration.py (also legacy)
    - **Note:** Feature was disabled in refactoring, code kept for reference

16. **`analyzer_time_slot_integration.py`** ⚠️
    - **Status:** Legacy - Integration layer for disabled feature
    - **Usage:** `AnalyzerTimeSlotIntegration`
    - **Imported by:** example_separate_sessions.py (example only)
    - **Note:** Time slot switching feature is disabled, this is unused

### Target Change Logic (Disabled Feature):
17. **`target_change_logic.py`** ⚠️
    - **Status:** Legacy - Dynamic targets disabled
    - **Usage:** `TargetChangeManager`
    - **Imported by:** None
    - **Note:** Dynamic target changes were disabled in refactoring

18. **`rolling_target_change_logic.py`** ⚠️
    - **Status:** Legacy - Rolling targets disabled
    - **Usage:** Rolling target logic
    - **Imported by:** None
    - **Note:** Feature disabled, code kept for reference

19. **`rolling_target_change_logic_fixed.py`** ⚠️
    - **Status:** Legacy - Fixed version of rolling targets
    - **Usage:** Fixed rolling target logic
    - **Imported by:** None
    - **Note:** Appears to be an attempt to fix rolling targets, but feature is disabled

### Custom Triggers (Removed):
20. **`custom_triggers/integration.py`** ✅ REMOVED
21. **`custom_triggers/ui.py`** ✅ REMOVED
22. **`custom_triggers/__init__.py`** ✅ REMOVED (entire directory removed)

---

## 📊 Usage Statistics

### By Category:
- **Core Engine:** 11 modules (52%)
- **Supporting:** 3 modules (14%)
- **Legacy/Unused:** 7 modules (33%)

### Import Analysis:
- **Imported by engine.py:** 11 modules
- **Imported by other modules:** 3 modules
- **Not imported anywhere:** 7 modules

---

## 🎯 Recommendations

### ✅ REMOVED (2025-01-XX):
1. ✅ `analyzer_time_slot_integration.py` - Removed
2. ✅ `target_change_logic.py` - Removed
3. ✅ `rolling_target_change_logic.py` - Removed
4. ✅ `rolling_target_change_logic_fixed.py` - Removed
5. ✅ `custom_triggers/integration.py` - Removed
6. ✅ `custom_triggers/ui.py` - Removed
7. ✅ `time_slot_logic.py` - Removed

### Keep for Reference:
1. `mfe_logic.py` - Standalone option, may be useful (MFE also integrated into price_tracking_logic)

### Consider Consolidation:
- `mfe_logic.py` could be removed if MFE calculation in `price_tracking_logic.py` is sufficient

---

## 🔍 Verification Commands

To verify which modules are actually imported:
```bash
# Find all imports of logic modules
grep -r "from logic\." scripts/breakout_analyzer/
grep -r "import logic\." scripts/breakout_analyzer/

# Check for unused imports
pylint scripts/breakout_analyzer/logic/*.py
```

---

**Last Updated:** 2025-01-XX  
**Analysis Method:** Import analysis + codebase search

