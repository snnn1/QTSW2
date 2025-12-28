"""Migrate scheduler state file from old field name to new"""
import json
from pathlib import Path

state_file = Path("automation/logs/scheduler_state.json")
if state_file.exists():
    state = json.load(open(state_file))
    print("Current state file:")
    print(json.dumps(state, indent=2))
    
    if "scheduler_enabled" in state and "last_requested_enabled" not in state:
        print("\nMigrating...")
        state["last_requested_enabled"] = state.pop("scheduler_enabled")
        with open(state_file, "w") as f:
            json.dump(state, f, indent=2)
        print("Migrated!")
        print(json.dumps(state, indent=2))
    else:
        print("\nNo migration needed")
else:
    print("State file not found")


