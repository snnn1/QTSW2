# NinjaTrader Custom Folder Explanation

## Overview
The `Custom` folder (`c:\Users\jakej\OneDrive\Documents\NinjaTrader 8\bin\Custom`) is where NinjaTrader compiles all custom code. It contains **your robot code**, **NinjaTrader default samples**, and **build artifacts**.

---

## ✅ **NEEDED: Your Robot Code**

### 1. **Strategies Folder** (`Strategies/`)
**Status**: ✅ **REQUIRED**

- **`RobotSimStrategy.cs`** ✅ **CRITICAL**
  - Your main trading strategy
  - Hosts `RobotEngine` and connects to NinjaTrader
  - **DO NOT DELETE**

- **`NinjaTraderBarRequest.cs`** ✅ **REQUIRED**
  - Helper class for requesting historical bars
  - Used by `RobotSimStrategy` for pre-hydration
  - **DO NOT DELETE**

- **`HeartbeatStrategy.cs`** ⚠️ **OPTIONAL** (Currently unused)
  - Standalone heartbeat strategy (alternative to `HeartbeatAddOn`)
  - You're using `HeartbeatAddOn` instead, so this is redundant
  - **CAN DELETE** if you're not using it

- **`HeartbeatAddOn.cs`** ⚠️ **MISSING** (Not in Custom folder)
  - Your workspace has `modules/robot/ninjatrader/HeartbeatAddOn.cs`
  - But it's **NOT** in the Custom folder's `AddOns/` directory
  - **NOT** referenced in `NinjaTrader.Custom.csproj`
  - **ACTION NEEDED**: If you want to use `HeartbeatAddOn`, copy it to `AddOns/HeartbeatAddOn.cs` and add it to the `.csproj`

- **`@Sample*.cs`** ❌ **NOT NEEDED**
  - NinjaTrader sample strategies
  - `@SampleAtmStrategy.cs`, `@SampleMACrossOver.cs`, etc.
  - **CAN DELETE** (they're just examples)

- **`@Strategy.cs`** ❌ **NOT NEEDED**
  - NinjaTrader base strategy template
  - **CAN DELETE**

### 2. **AddOns Folder** (`AddOns/RobotCore_For_NinjaTrader/`)
**Status**: ✅ **REQUIRED** (This is your entire robot core)

**All files in this folder are NEEDED**:
- `RobotEngine.cs` - Main engine
- `StreamStateMachine.cs` - State management
- `Execution/` folder - All execution adapters
- `RobotLogger.cs`, `RobotLoggingService.cs` - Logging
- `TimeService.cs`, `TimetableContract.cs` - Time handling
- All other `.cs` files - Core functionality

**Exception - Build artifacts** (can delete, will regenerate):
- `bin/` folder (Debug/Release DLLs)
- `obj/` folder (build cache)
- `.dll` files in root (System.Buffers.dll, etc.)

### 3. **Project Files**
**Status**: ✅ **REQUIRED**

- **`NinjaTrader.Custom.csproj`** ✅ **CRITICAL**
  - Main NinjaTrader project file
  - Lists all files to compile
  - **DO NOT DELETE**

- **`Robot.Core.csproj`** ✅ **NEEDED**
  - Your robot core project file (inside `AddOns/RobotCore_For_NinjaTrader/`)
  - Used to build `Robot.Core.dll`
  - **DO NOT DELETE**

- **`Robot.Core.dll`** ⚠️ **OPTIONAL**
  - Compiled DLL (in root `Custom/` folder)
  - Can be regenerated from source
  - **CAN DELETE** if you rebuild from source

---

## ❌ **NOT NEEDED: NinjaTrader Defaults**

These are NinjaTrader's built-in samples and can be deleted if you're not using them:

### 1. **BarsTypes/** ❌
- `@DayBarsType.cs`, `@MinuteBarsType.cs`, etc.
- NinjaTrader default bar type implementations
- **CAN DELETE** (unless you use custom bar types)

### 2. **ChartStyles/** ❌
- `@CandleStyle.cs`, `@LineOnCloseStyle.cs`, etc.
- NinjaTrader default chart style implementations
- **CAN DELETE** (unless you use custom chart styles)

### 3. **DrawingTools/** ❌
- `@Lines.cs`, `@FibonacciTools.cs`, etc.
- NinjaTrader default drawing tool implementations
- **CAN DELETE** (unless you use custom drawing tools)

### 4. **Indicators/** ❌
- `@ADX.cs`, `@Bollinger.cs`, `@CCI.cs`, etc. (100+ files)
- NinjaTrader default indicator implementations
- **CAN DELETE** (unless you use these indicators)
- **Exception**: `Exporter.cs` - Check if this is yours or NinjaTrader's

### 5. **OptimizationFitnesses/** ❌
- `@MaxNetProfit.cs`, `@MaxSharpeRatio.cs`, etc.
- NinjaTrader default optimization fitness functions
- **CAN DELETE** (unless you optimize strategies)

### 6. **Optimizers/** ❌
- Custom optimizer implementations
- **CAN DELETE** (unless you use custom optimizers)

### 7. **MarketAnalyzerColumns/** ❌
- Custom market analyzer column implementations
- **CAN DELETE** (unless you use custom columns)

### 8. **SuperDomColumns/** ❌
- Custom SuperDOM column implementations
- **CAN DELETE** (unless you use custom SuperDOM columns)

### 9. **ShareServices/** ❌
- `@Mail.cs`, `@Twitter.cs`, `@StockTwits.cs`, etc.
- Social media sharing services
- **CAN DELETE** (unless you use these features)

### 10. **ImportTypes/** ❌
- Custom data import type implementations
- **CAN DELETE** (unless you use custom import types)

### 11. **PerformanceMetrics/** ❌
- Custom performance metric implementations
- **CAN DELETE** (unless you use custom metrics)

---

## 🗑️ **NOT NEEDED: Build Artifacts**

These are generated files and can be safely deleted (they'll regenerate):

### 1. **`bin/` folders** ❌
- `AddOns/RobotCore_For_NinjaTrader/bin/Debug/`
- `AddOns/RobotCore_For_NinjaTrader/bin/Release/`
- Contains compiled DLLs and dependencies
- **CAN DELETE** (will regenerate on build)

### 2. **`obj/` folders** ❌
- `AddOns/RobotCore_For_NinjaTrader/obj/`
- Build cache and intermediate files
- **CAN DELETE** (will regenerate on build)

### 3. **`.dll` files in `AddOns/RobotCore_For_NinjaTrader/`** ❌
- `System.Buffers.dll`, `System.Memory.dll`, etc.
- NuGet package DLLs (should be in `bin/` folder)
- **CAN DELETE** (will regenerate on build)

### 4. **`.cache` files** ❌
- Build cache files
- **CAN DELETE**

### 5. **`.pdb` files** ⚠️
- Debug symbols (useful for debugging, but not required)
- **CAN DELETE** (will regenerate on build)

---

## 🌍 **NOT NEEDED: Localization Files**

These are for multi-language support:

### 1. **Language folders** ❌
- `de-DE/`, `es-ES/`, `fr-FR/`, `it-IT/`, `ko-KR/`, `pt-PT/`, `ru-RU/`, `zh-Hans/`
- Localized resource DLLs
- **CAN DELETE** (unless you need non-English support)

### 2. **Resource files** ❌
- `Resource.de-DE.resx`, `Resource.es-ES.resx`, etc.
- Localized resource files
- **CAN DELETE** (unless you need non-English support)

---

## 📋 **Summary: What to Keep vs Delete**

### ✅ **KEEP (Required for Robot)**
```
Custom/
├── Strategies/
│   ├── RobotSimStrategy.cs ✅
│   └── NinjaTraderBarRequest.cs ✅
├── AddOns/
│   └── RobotCore_For_NinjaTrader/
│       ├── *.cs ✅ (all source files)
│       └── Robot.Core.csproj ✅
├── NinjaTrader.Custom.csproj ✅
└── Robot.Core.dll ⚠️ (optional, can rebuild)
```

### ❌ **DELETE (Not Needed)**
```
Custom/
├── Strategies/
│   ├── HeartbeatStrategy.cs ⚠️ (if not using)
│   ├── @Sample*.cs ❌
│   └── @Strategy.cs ❌
├── BarsTypes/ ❌
├── ChartStyles/ ❌
├── DrawingTools/ ❌
├── Indicators/ ❌ (check Exporter.cs first)
├── OptimizationFitnesses/ ❌
├── Optimizers/ ❌
├── MarketAnalyzerColumns/ ❌
├── SuperDomColumns/ ❌
├── ShareServices/ ❌
├── ImportTypes/ ❌
├── PerformanceMetrics/ ❌
├── de-DE/, es-ES/, etc./ ❌
├── Resource.*.resx ❌ (except Resource.resx if needed)
├── bin/ folders ❌
├── obj/ folders ❌
└── .dll files in AddOns/RobotCore_For_NinjaTrader/ ❌
```

---

## 🔍 **How to Check What's Actually Used**

### 1. **Check `.csproj` file**
The `NinjaTrader.Custom.csproj` file lists all files that are compiled. If a file is not listed there, it's not being used.

### 2. **Check for references**
Search for references to files:
```powershell
# Check if HeartbeatStrategy is referenced
Select-String -Path "*.cs" -Pattern "HeartbeatStrategy" -Recurse
```

### 3. **Check NinjaTrader UI**
- Open NinjaTrader
- Check Tools → Strategies → see what strategies appear
- Check Tools → Indicators → see what indicators appear

---

## ⚠️ **Important Notes**

1. **Don't delete while NinjaTrader is running** - Close NinjaTrader before deleting files
2. **Backup first** - Make a backup of the `Custom` folder before cleaning
3. **Test after cleanup** - Rebuild and test your strategy after deleting files
4. **`.csproj` updates** - If you delete files, you may need to remove them from `NinjaTrader.Custom.csproj` manually
5. **HeartbeatAddOn** - `HeartbeatAddOn.cs` is NOT in the Custom folder (it's in your workspace `modules/robot/ninjatrader/`). If you want to use it, you need to copy it to `AddOns/` folder.

---

## 🎯 **Recommended Action**

1. **Keep**: All files in `AddOns/RobotCore_For_NinjaTrader/` (except `bin/` and `obj/`)
2. **Keep**: `Strategies/RobotSimStrategy.cs` and `Strategies/NinjaTraderBarRequest.cs`
3. **Delete**: All `@Sample*.cs` files
4. **Delete**: All default NinjaTrader folders (BarsTypes, ChartStyles, etc.) if not using
5. **Delete**: Build artifacts (`bin/`, `obj/`, scattered `.dll` files)
6. **Delete**: Localization folders if not needed
7. **Optional**: Delete `HeartbeatStrategy.cs` if using `HeartbeatAddOn` instead

This will significantly reduce clutter while keeping everything needed for your robot to function.
