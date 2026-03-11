# How to Fix NINJATRADER Compilation

## Quick Fix

Add `NINJATRADER` to your NinjaTrader Strategy project's conditional compilation symbols.

## Step-by-Step Instructions

### Option 1: Edit .csproj File (Recommended)

1. **Find your NinjaTrader Strategy project file** (usually in `Documents\NinjaTrader 8\bin\Custom\Strategies\` or similar)
2. **Open the `.csproj` file** in a text editor
3. **Find the `<PropertyGroup>` section** (usually near the top)
4. **Add or modify the `DefineConstants` line**:

```xml
<PropertyGroup>
  <TargetFramework>net48</TargetFramework>
  <DefineConstants>NINJATRADER</DefineConstants>
  <!-- other properties -->
</PropertyGroup>
```

**OR if you already have constants defined:**

```xml
<PropertyGroup>
  <TargetFramework>net48</TargetFramework>
  <DefineConstants>$(DefineConstants);NINJATRADER</DefineConstants>
  <!-- other properties -->
</PropertyGroup>
```

5. **Save the file**
6. **Rebuild your NinjaTrader Strategy project**

### Option 2: Visual Studio / IDE

1. **Right-click your NinjaTrader Strategy project** in Solution Explorer
2. **Select Properties**
3. **Go to Build tab**
4. **Find "Conditional compilation symbols"** (or "Define DEBUG constant")
5. **Add `NINJATRADER`** (separate multiple symbols with semicolons)
6. **Click OK**
7. **Rebuild the project**

### Option 3: NinjaTrader Strategy Editor

If NinjaTrader has a built-in editor:
1. Open your strategy in NinjaTrader
2. Look for Build Settings or Project Properties
3. Add `NINJATRADER` to compilation symbols
4. Recompile

## Verify It's Working

After rebuilding, check the NinjaTrader output window or logs. You should see:

✅ **"NINJATRADER preprocessor directive is DEFINED - real NT API will be used"**

❌ **NOT** "WARNING: NINJATRADER preprocessor directive is NOT DEFINED"

## What This Does

When `NINJATRADER` is defined:
- `VerifySimAccountReal()` method is compiled (real NT API)
- `SubmitEntryOrderReal()` method is compiled (real NT API)
- Real NinjaTrader API calls are used instead of mocks

When `NINJATRADER` is NOT defined:
- Mock implementation is used
- Logs show "MOCK - harness mode"
- Orders are not actually placed

## Template File

See `modules/robot/ninjatrader/NINJATRADER_PROJECT_TEMPLATE.csproj` for a complete example.
