"""
Test Manual Pipeline Run Through Orchestrator
"""

import asyncio
import sys
import logging
from pathlib import Path
from datetime import datetime

# Add project root to path
qtsw2_root = Path(__file__).parent.parent.parent
if str(qtsw2_root) not in sys.path:
    sys.path.insert(0, str(qtsw2_root))

from dashboard.backend.orchestrator.service import PipelineOrchestrator
from dashboard.backend.orchestrator.config import OrchestratorConfig
from dashboard.backend.orchestrator.state import PipelineRunState


async def test_pipeline_run():
    """Test manual pipeline run"""
    print("="*60)
    print("  TESTING MANUAL PIPELINE RUN")
    print("="*60)
    print()
    
    try:
        # Setup
        print("1. Initializing orchestrator...")
        config = OrchestratorConfig.from_environment(qtsw2_root=qtsw2_root)
        schedule_config_path = qtsw2_root / "automation" / "schedule_config.json"
        
        orchestrator = PipelineOrchestrator(
            config=config,
            schedule_config_path=schedule_config_path
        )
        print("   ✓ Orchestrator created")
        print()
        
        # Start orchestrator (but don't start scheduler)
        print("2. Starting orchestrator services...")
        # We'll manually start the pipeline, so we don't need scheduler/watchdog
        print("   ✓ Ready")
        print()
        
        # Check current status
        print("3. Checking current status...")
        status = await orchestrator.get_status()
        if status:
            print(f"   → Current state: {status.state.value}")
            print(f"   → Current stage: {status.current_stage.value if status.current_stage else 'None'}")
        else:
            print("   → No active run (system idle)")
        print()
        
        # Start pipeline
        print("4. Starting manual pipeline run...")
        try:
            run_ctx = await orchestrator.start_pipeline(manual=True)
            print(f"   ✓ Pipeline started: {run_ctx.run_id[:8]}")
            print(f"   → State: {run_ctx.state.value}")
            print()
            
            # Monitor progress
            print("5. Monitoring pipeline progress...")
            print("   (Waiting for completion or failure...)")
            print()
            
            max_wait = 120  # Wait up to 2 minutes
            check_interval = 2  # Check every 2 seconds
            waited = 0
            
            while waited < max_wait:
                await asyncio.sleep(check_interval)
                waited += check_interval
                
                status = await orchestrator.get_status()
                if not status:
                    print("   → Pipeline run completed (status cleared)")
                    break
                
                current_state = status.state.value
                current_stage = status.current_stage.value if status.current_stage else "None"
                
                print(f"   [{waited}s] State: {current_state}, Stage: {current_stage}")
                
                if status.error:
                    print(f"   → Error: {status.error}")
                
                # Check if finished
                if current_state in ["success", "failed", "stopped"]:
                    print()
                    print("="*60)
                    print("  PIPELINE COMPLETED")
                    print("="*60)
                    print(f"Final state: {current_state}")
                    print(f"Final stage: {current_stage}")
                    if status.error:
                        print(f"Error: {status.error}")
                    print()
                    break
            
            if waited >= max_wait:
                print()
                print("   ⚠ Timeout waiting for pipeline completion")
                print(f"   → Final state: {status.state.value if status else 'Unknown'}")
            
            # Get snapshot
            print("6. Getting final snapshot...")
            snapshot = await orchestrator.get_snapshot()
            if snapshot:
                recent_events = snapshot.get("recent_events", [])
                print(f"   → Recent events: {len(recent_events)}")
                
                # Show last 10 events
                print()
                print("   Last 10 events:")
                for event in recent_events[-10:]:
                    stage = event.get("stage", "unknown")
                    event_type = event.get("event", "unknown")
                    msg = event.get("msg", "No message")
                    timestamp = event.get("timestamp", "Unknown")
                    print(f"     [{timestamp}] {stage}/{event_type}: {msg}")
            
        except Exception as e:
            print(f"   ❌ Failed to start pipeline: {e}")
            import traceback
            traceback.print_exc()
        
        finally:
            # Cleanup
            print()
            print("7. Cleaning up...")
            await orchestrator.stop()
            print("   ✓ Orchestrator stopped")
        
    except Exception as e:
        print(f"\n❌ Exception during test: {e}")
        import traceback
        traceback.print_exc()


if __name__ == "__main__":
    asyncio.run(test_pipeline_run())

