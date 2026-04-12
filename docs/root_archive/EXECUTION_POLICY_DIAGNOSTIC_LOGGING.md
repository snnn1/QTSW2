# Execution Policy Diagnostic Logging

## Changes Made

Added comprehensive diagnostic logging to definitively identify what file is being read and what values are parsed.

### Step 1: Absolute Path Logging ✅

**Location**: `modules/robot/core/RobotEngine.cs` (lines 398-402)

**Change**: Resolve and log the absolute path of the policy file

```csharp
// Resolve absolute path for definitive logging
var absolutePolicyPath = Path.IsPathRooted(_executionPolicyPath) 
    ? _executionPolicyPath 
    : Path.Combine(_root, _executionPolicyPath);
absolutePolicyPath = Path.GetFullPath(absolutePolicyPath);
```

**Log Event**: `EXECUTION_POLICY_LOADED`
- `file_path`: Absolute path (e.g., `C:\Users\jakej\QTSW2\configs\execution_policy.json`)
- `file_path_relative`: Original relative path
- `file_size_bytes`: File size in bytes

### Step 2: Raw Parsed Values Logging ✅

**Location**: `modules/robot/core/RobotEngine.cs` (lines 424-452)

**Change**: Log raw parsed JSON values immediately after deserialization

**Log Event**: `EXECUTION_POLICY_PARSED`
- `parsed_values`: Dictionary of all canonical markets → execution instruments → policy values
- Includes `enabled`, `base_size`, `max_size` for each instrument
- **Not derived, not cached** - raw values from JSON deserialization

**Example Output**:
```json
{
  "event": "EXECUTION_POLICY_PARSED",
  "data": {
    "parsed_values": {
      "RTY": {
        "M2K": {
          "enabled": true,
          "base_size": 2,
          "max_size": 2
        }
      }
    }
  }
}
```

### Step 3: File Hash (SHA-256) ✅

**Location**: `modules/robot/core/RobotEngine.cs` (lines 406-422)

**Change**: Already existed, now uses absolute path

**Log Event**: `EXECUTION_POLICY_LOADED`
- `file_hash`: SHA-256 hash of file contents (lowercase hex, no dashes)

## What This Solves

### Before
- ❓ Was the robot reading the right file?
- ❓ What values did it actually parse?
- ❓ Could there be a path resolution issue?

### After
- ✅ **Absolute path** tells you exactly which file was read
- ✅ **Parsed values** show exactly what was deserialized (no ambiguity)
- ✅ **File hash** allows mathematical verification (compare to file you're editing)

## Usage

After restarting the robot, check logs for:

1. **`EXECUTION_POLICY_PARSED`** - Shows raw parsed values
   ```bash
   grep "EXECUTION_POLICY_PARSED" logs/robot/robot_ENGINE.jsonl
   ```

2. **`EXECUTION_POLICY_LOADED`** - Shows file path and hash
   ```bash
   grep "EXECUTION_POLICY_LOADED" logs/robot/robot_ENGINE.jsonl
   ```

3. **Compare file hash**:
   ```powershell
   # Get hash from log
   $logHash = "..."
   
   # Compute hash of current file
   $fileBytes = [System.IO.File]::ReadAllBytes("configs\execution_policy.json")
   $sha256 = [System.Security.Cryptography.SHA256]::Create()
   $hashBytes = $sha256.ComputeHash($fileBytes)
   $fileHash = [System.BitConverter]::ToString($hashBytes).Replace("-", "").ToLowerInvariant()
   
   # Compare
   Write-Host "Log hash: $logHash"
   Write-Host "File hash: $fileHash"
   Write-Host "Match: $($logHash -eq $fileHash)"
   ```

## Expected Log Output

```json
{
  "event": "EXECUTION_POLICY_PARSED",
  "data": {
    "parsed_values": {
      "RTY": {
        "M2K": {
          "enabled": true,
          "base_size": 2,
          "max_size": 2
        }
      },
      "ES": {
        "MES": {
          "enabled": true,
          "base_size": 2,
          "max_size": 2
        }
      }
    }
  }
}

{
  "event": "EXECUTION_POLICY_LOADED",
  "data": {
    "file_path": "C:\\Users\\jakej\\QTSW2\\configs\\execution_policy.json",
    "file_path_relative": "configs\\execution_policy.json",
    "file_hash": "a1b2c3d4e5f6...",
    "file_size_bytes": 1955,
    "schema_id": "qtsw2.execution_policy"
  }
}
```

## Next Steps

1. **Restart robot** to pick up new logging
2. **Check logs** for `EXECUTION_POLICY_PARSED` event
3. **Verify**:
   - File path matches what you're editing
   - Parsed `base_size` matches file content
   - File hash matches current file

If parsed values don't match file content:
- File wasn't saved when robot started
- Robot reading from different location
- File system caching issue
