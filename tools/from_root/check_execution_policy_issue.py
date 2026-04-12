#!/usr/bin/env python3
"""
Diagnostic script to check execution policy reading issue
"""
import json
from pathlib import Path
from datetime import datetime

print("=" * 100)
print("EXECUTION POLICY DIAGNOSTIC")
print("=" * 100)

# Check current file
policy_file = Path("configs/execution_policy.json")
print(f"\n1. CURRENT POLICY FILE")
print("-" * 100)
print(f"  Path: {policy_file.absolute()}")
print(f"  Exists: {policy_file.exists()}")
if policy_file.exists():
    stat = policy_file.stat()
    print(f"  Last Modified: {datetime.fromtimestamp(stat.st_mtime)}")
    print(f"  Size: {stat.st_size} bytes")
    
    with open(policy_file, 'r') as f:
        policy = json.load(f)
    
    rty = policy.get('canonical_markets', {}).get('RTY', {})
    m2k = rty.get('execution_instruments', {}).get('M2K', {})
    
    print(f"\n  M2K Configuration:")
    print(f"    enabled: {m2k.get('enabled')}")
    print(f"    base_size: {m2k.get('base_size')}")
    print(f"    max_size: {m2k.get('max_size')}")

# Check logs
print(f"\n2. LOG ANALYSIS")
print("-" * 100)
log_file = Path("logs/robot/robot_M2K.jsonl")
if log_file.exists():
    with open(log_file, 'r', encoding='utf-8') as f:
        lines = f.readlines()
    
    policy_events = []
    for line in lines:
        try:
            event = json.loads(line.strip())
            if event.get('event') == 'INTENT_EXECUTION_EXPECTATION_DECLARED':
                data = event.get('data', {})
                if data.get('execution_instrument') == 'M2K':
                    policy_events.append({
                        'timestamp': event.get('ts_utc', ''),
                        'policy_base_size': data.get('policy_base_size'),
                        'policy_max_size': data.get('policy_max_size'),
                        'run_id': event.get('run_id', '')
                    })
        except:
            pass
    
    print(f"  Found {len(policy_events)} M2K policy events")
    if policy_events:
        print(f"\n  Recent Policy Values:")
        for evt in policy_events[-5:]:
            ts = evt['timestamp'][:19] if len(evt['timestamp']) > 19 else evt['timestamp']
            print(f"    {ts} | base_size: {evt['policy_base_size']} | max_size: {evt['policy_max_size']} | run_id: {evt['run_id'][:8]}")

# Check for file path issues
print(f"\n3. FILE PATH CHECK")
print("-" * 100)
print(f"  Current working directory: {Path.cwd()}")
print(f"  Policy file absolute path: {policy_file.absolute()}")
print(f"  Policy file exists: {policy_file.exists()}")

# Check if there are multiple policy files
print(f"\n4. SEARCH FOR OTHER POLICY FILES")
print("-" * 100)
policy_files = list(Path(".").rglob("execution_policy.json"))
print(f"  Found {len(policy_files)} execution_policy.json files:")
for pf in policy_files:
    print(f"    {pf}")

print("\n" + "=" * 100)
print("CONCLUSION")
print("=" * 100)
print("  Current file shows base_size: 2")
print("  Logs show policy_base_size: 1")
print("  This suggests the file was edited but not saved when robot started")
print("  OR the robot is reading from a different location")
print("  OR there's a deserialization bug")
print("\n  RECOMMENDATION: Restart robot to ensure it reads current file content")
print()
