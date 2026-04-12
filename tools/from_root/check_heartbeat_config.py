#!/usr/bin/env python3
"""
Check heartbeat configuration and why they might not be logging.
"""
import json
from pathlib import Path

def main():
    print("="*80)
    print("HEARTBEAT CONFIGURATION CHECK")
    print("="*80)
    
    # Check logging config
    config_paths = [
        "configs/robot/logging_config.json",
        "modules/robot/configs/logging_config.json"
    ]
    
    logging_config = None
    for path in config_paths:
        config_file = Path(path)
        if config_file.exists():
            try:
                with open(config_file, 'r') as f:
                    logging_config = json.load(f)
                print(f"\n  Found logging config: {path}")
                break
            except:
                pass
    
    if logging_config:
        print(f"\n  Logging configuration:")
        enable_diagnostic = logging_config.get('enable_diagnostic_logs', False)
        print(f"    enable_diagnostic_logs: {enable_diagnostic}")
        
        if enable_diagnostic:
            print(f"    [OK] Diagnostic logs enabled - heartbeats should be logging")
        else:
            print(f"    [WARN] Diagnostic logs DISABLED - ENGINE_BAR_HEARTBEAT and ENGINE_TICK_HEARTBEAT won't log")
            print(f"           These are DEBUG level events that require enable_diagnostic_logs = true")
        
        # Check rate limits
        rate_limits = logging_config.get('diagnostic_rate_limits', {})
        if rate_limits:
            print(f"\n    Diagnostic rate limits:")
            for key, value in rate_limits.items():
                print(f"      {key}: {value}")
    else:
        print(f"\n  [WARN] No logging config found")
        print(f"         Default: enable_diagnostic_logs = false")
        print(f"         Heartbeats (DEBUG level) won't log unless enabled")
    
    # Summary
    print("\n" + "="*80)
    print("HEARTBEAT STATUS:")
    print("="*80)
    
    print(f"\n  Heartbeat event types:")
    print(f"    ENGINE_HEARTBEAT: INFO level (should always log)")
    print(f"    ENGINE_TICK_HEARTBEAT: DEBUG level (requires enable_diagnostic_logs)")
    print(f"    ENGINE_BAR_HEARTBEAT: DEBUG level (requires enable_diagnostic_logs)")
    print(f"    HEARTBEAT (stream): INFO level (should always log)")
    
    print(f"\n  Why no heartbeats found:")
    print(f"    1. ENGINE_HEARTBEAT: May not be implemented/logged")
    print(f"    2. ENGINE_TICK_HEARTBEAT: Requires enable_diagnostic_logs = true")
    print(f"    3. ENGINE_BAR_HEARTBEAT: Requires enable_diagnostic_logs = true")
    print(f"    4. HEARTBEAT (stream): Rate-limited to once per 7 minutes per stream")
    print(f"       - May not have fired yet if streams just started")
    print(f"       - Only logs when stream is in certain states")

if __name__ == "__main__":
    main()
