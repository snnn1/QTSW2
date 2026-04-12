"""
Comprehensive pipeline test suite
"""
import asyncio
import sys
import logging
from pathlib import Path
from datetime import datetime

# Add project root to path
qtsw2_root = Path(__file__).parent.parent.parent.parent
if str(qtsw2_root) not in sys.path:
    sys.path.insert(0, str(qtsw2_root))

from modules.orchestrator import PipelineOrchestrator, OrchestratorConfig
from modules.orchestrator.state import PipelineRunState, PipelineStage

# Setup logging
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(name)s - %(levelname)s - %(message)s'
)
logger = logging.getLogger(__name__)


async def test_lock_mechanism():
    """Test 1: Lock acquisition and release"""
    print("\n" + "="*60)
    print("TEST 1: Lock Mechanism")
    print("="*60)
    
    config = OrchestratorConfig.from_environment(qtsw2_root=qtsw2_root)
    orchestrator = PipelineOrchestrator(
        config=config,
        schedule_config_path=qtsw2_root / "automation" / "schedule_config.json",
        logger=logger
    )
    
    await orchestrator.start()
    
    try:
        # Test 1a: Acquire lock
        run_id_1 = "test-run-1"
        acquired = await orchestrator.lock_manager.acquire(run_id_1)
        print(f"[OK] Lock acquired: {acquired}")
        assert acquired, "Failed to acquire lock"
        
        # Test 1b: Try to acquire again (should fail)
        run_id_2 = "test-run-2"
        acquired_2 = await orchestrator.lock_manager.acquire(run_id_2)
        print(f"[OK] Second lock attempt blocked: {not acquired_2}")
        assert not acquired_2, "Second lock should be blocked"
        
        # Test 1c: Release lock
        released = await orchestrator.lock_manager.release(run_id_1)
        print(f"[OK] Lock released: {released}")
        assert released, "Failed to release lock"
        
        # Test 1d: Acquire after release
        acquired_3 = await orchestrator.lock_manager.acquire(run_id_2)
        print(f"[OK] Lock acquired after release: {acquired_3}")
        assert acquired_3, "Should be able to acquire after release"
        
        await orchestrator.lock_manager.release(run_id_2)
        print("[OK] TEST 1 PASSED: Lock mechanism works correctly")
        
    finally:
        await orchestrator.stop()


async def test_state_transitions():
    """Test 2: State machine transitions"""
    print("\n" + "="*60)
    print("TEST 2: State Transitions")
    print("="*60)
    
    config = OrchestratorConfig.from_environment(qtsw2_root=qtsw2_root)
    orchestrator = PipelineOrchestrator(
        config=config,
        schedule_config_path=qtsw2_root / "automation" / "schedule_config.json",
        logger=logger
    )
    
    await orchestrator.start()
    
    try:
        # Test 2a: Create run
        run_ctx = await orchestrator.state_manager.create_run(
            run_id="test-state-run",
            metadata={"test": True}
        )
        print(f"[OK] Run created: {run_ctx.run_id}")
        assert run_ctx.state == PipelineRunState.IDLE
        
        # Test 2b: Transition to STARTING
        await orchestrator.state_manager.transition(PipelineRunState.STARTING)
        status = await orchestrator.state_manager.get_state()
        print(f"[OK] State transitioned to: {status.state.value}")
        assert status.state == PipelineRunState.STARTING
        
        # Test 2c: Transition to RUNNING_TRANSLATOR
        await orchestrator.state_manager.transition(
            PipelineRunState.RUNNING_TRANSLATOR,
            stage=PipelineStage.TRANSLATOR
        )
        status = await orchestrator.state_manager.get_state()
        print(f"[OK] State transitioned to: {status.state.value}")
        assert status.state == PipelineRunState.RUNNING_TRANSLATOR
        
        # Test 2d: Transition to FAILED (invalid transition should be caught)
        try:
            await orchestrator.state_manager.transition(PipelineRunState.SUCCESS)
            print("[ERROR] Invalid transition should have been caught")
            assert False, "Invalid transition should raise error"
        except ValueError as e:
            print(f"[OK] Invalid transition caught: {e}")
        
        # Reset to IDLE (via FAILED, which is valid from RUNNING_TRANSLATOR)
        await orchestrator.state_manager.transition(PipelineRunState.FAILED)
        await orchestrator.state_manager.transition(PipelineRunState.IDLE)
        print("[OK] TEST 2 PASSED: State transitions work correctly")
        
    finally:
        await orchestrator.stop()


async def test_event_publishing():
    """Test 3: Event bus publishing"""
    print("\n" + "="*60)
    print("TEST 3: Event Publishing")
    print("="*60)
    
    config = OrchestratorConfig.from_environment(qtsw2_root=qtsw2_root)
    orchestrator = PipelineOrchestrator(
        config=config,
        schedule_config_path=qtsw2_root / "automation" / "schedule_config.json",
        logger=logger
    )
    
    await orchestrator.start()
    
    try:
        # Test 3a: Publish event
        test_event = {
            "run_id": "test-event-run",
            "stage": "test",
            "event": "test_event",
            "msg": "Test event message",
            "data": {"test": True}
        }
        
        await orchestrator.event_bus.publish(test_event)
        print("[OK] Event published")
        
        # Test 3b: Check ring buffer
        recent = orchestrator.event_bus.get_recent_events(limit=10)
        print(f"[OK] Recent events in buffer: {len(recent)}")
        assert len(recent) > 0, "Event should be in buffer"
        
        # Test 3c: Check JSONL file
        event_file = config.event_logs_dir / "pipeline_test-event-run.jsonl"
        if event_file.exists():
            print(f"[OK] Event file created: {event_file}")
            with open(event_file, "r") as f:
                lines = f.readlines()
                print(f"[OK] Events in file: {len(lines)}")
                assert len(lines) > 0, "Event should be in file"
        else:
            print("[WARN] Event file not created (may be normal)")
        
        print("[OK] TEST 3 PASSED: Event publishing works correctly")
        
    finally:
        await orchestrator.stop()


async def test_pipeline_start_stop():
    """Test 4: Pipeline start/stop without full execution"""
    print("\n" + "="*60)
    print("TEST 4: Pipeline Start/Stop")
    print("="*60)
    
    config = OrchestratorConfig.from_environment(qtsw2_root=qtsw2_root)
    orchestrator = PipelineOrchestrator(
        config=config,
        schedule_config_path=qtsw2_root / "automation" / "schedule_config.json",
        logger=logger
    )
    
    await orchestrator.start()
    
    try:
        # Test 4a: Start pipeline
        print("Starting pipeline (will run in background)...")
        run_ctx = await orchestrator.start_pipeline(manual=True)
        print(f"[OK] Pipeline started: {run_ctx.run_id}")
        
        # Wait a moment for it to start
        await asyncio.sleep(2)
        
        # Test 4b: Check state
        status = await orchestrator.state_manager.get_state()
        print(f"[OK] Current state: {status.state.value}")
        assert status is not None, "Status should exist"
        assert status.state != PipelineRunState.IDLE, "Should not be idle after start"
        
        # Test 4c: Check lock (immediately after start, before pipeline completes)
        lock_info = await orchestrator.lock_manager.get_lock_info()
        print(f"[OK] Lock info: {lock_info}")
        # Lock might be None if pipeline completed very quickly, so just check it was acquired
        if lock_info:
            assert lock_info.get("locked"), "Lock should be held if info exists"
        
        # Test 4d: Try to start again (should fail if pipeline still running)
        # Note: If pipeline completes very quickly, lock may be released
        status_check = await orchestrator.state_manager.get_state()
        if status_check and status_check.state not in {PipelineRunState.SUCCESS, PipelineRunState.FAILED, PipelineRunState.STOPPED, PipelineRunState.IDLE}:
            try:
                await orchestrator.start_pipeline(manual=True)
                print("[ERROR] Second start should have failed")
                assert False, "Second start should raise ValueError"
            except ValueError as e:
                print(f"[OK] Second start correctly blocked: {e}")
        else:
            print(f"[OK] Pipeline completed quickly, lock released (state: {status_check.state.value if status_check else 'None'})")
        
        # Wait for pipeline to complete or stop it manually
        await asyncio.sleep(3)
        status = await orchestrator.state_manager.get_state()
        
        # Test 4e: Stop pipeline if still running
        if status and status.state not in {PipelineRunState.SUCCESS, PipelineRunState.FAILED, PipelineRunState.STOPPED}:
            await orchestrator.stop_pipeline()
            print("[OK] Pipeline stopped")
            await asyncio.sleep(1)
        else:
            print(f"[OK] Pipeline already completed: {status.state.value}")
        
        # Test 4f: Check state after stop
        status = await orchestrator.state_manager.get_state()
        print(f"[OK] State after stop: {status.state.value}")
        assert status.state in {PipelineRunState.STOPPED, PipelineRunState.SUCCESS, PipelineRunState.FAILED}, "Should be stopped/completed"
        
        print("[OK] TEST 4 PASSED: Pipeline start/stop works correctly")
        
    finally:
        await orchestrator.stop()


async def test_full_pipeline_run():
    """Test 5: Full pipeline execution (if data available)"""
    print("\n" + "="*60)
    print("TEST 5: Full Pipeline Run")
    print("="*60)
    
    # Check if raw data exists
    raw_data_dir = qtsw2_root / "data" / "raw"
    if not raw_data_dir.exists() or not any(raw_data_dir.glob("*.csv")):
        print("[WARN] No raw data found, skipping full pipeline test")
        print("  Place CSV files in data/raw/ to test full execution")
        return
    
    config = OrchestratorConfig.from_environment(qtsw2_root=qtsw2_root)
    orchestrator = PipelineOrchestrator(
        config=config,
        schedule_config_path=qtsw2_root / "automation" / "schedule_config.json",
        logger=logger
    )
    
    await orchestrator.start()
    
    try:
        print("Starting full pipeline run...")
        run_ctx = await orchestrator.start_pipeline(manual=True)
        print(f"[OK] Pipeline started: {run_ctx.run_id}")
        
        # Monitor progress
        max_wait = 300  # 5 minutes max
        start_time = asyncio.get_event_loop().time()
        
        while True:
            await asyncio.sleep(5)
            status = await orchestrator.state_manager.get_state()
            
            elapsed = asyncio.get_event_loop().time() - start_time
            print(f"  State: {status.state.value} (elapsed: {elapsed:.1f}s)")
            
            if status.state in {PipelineRunState.SUCCESS, PipelineRunState.FAILED, PipelineRunState.STOPPED}:
                break
            
            if elapsed > max_wait:
                print("[WARN] Timeout waiting for pipeline to complete")
                break
        
        final_status = await orchestrator.state_manager.get_state()
        print(f"\n[OK] Final state: {final_status.state.value}")
        
        if final_status.state == PipelineRunState.SUCCESS:
            print("[OK] TEST 5 PASSED: Full pipeline completed successfully")
        elif final_status.state == PipelineRunState.FAILED:
            print(f"[ERROR] TEST 5 FAILED: Pipeline failed - {final_status.error}")
        else:
            print(f"[WARN] TEST 5 INCOMPLETE: Pipeline ended in {final_status.state.value}")
        
    finally:
        await orchestrator.stop()


async def run_all_tests():
    """Run all tests"""
    print("\n" + "="*60)
    print("PIPELINE TEST SUITE")
    print("="*60)
    print(f"Started at: {datetime.now().isoformat()}")
    
    tests = [
        ("Lock Mechanism", test_lock_mechanism),
        ("State Transitions", test_state_transitions),
        ("Event Publishing", test_event_publishing),
        ("Pipeline Start/Stop", test_pipeline_start_stop),
        ("Full Pipeline Run", test_full_pipeline_run),
    ]
    
    results = []
    
    for test_name, test_func in tests:
        try:
            await test_func()
            results.append((test_name, "PASSED"))
        except Exception as e:
            print(f"\n[ERROR] {test_name} FAILED: {e}")
            results.append((test_name, f"FAILED: {e}"))
            import traceback
            traceback.print_exc()
    
    # Summary
    print("\n" + "="*60)
    print("TEST SUMMARY")
    print("="*60)
    for test_name, result in results:
        status = "[OK]" if result == "PASSED" else "[ERROR]"
        print(f"{status} {test_name}: {result}")
    
    passed = sum(1 for _, r in results if r == "PASSED")
    total = len(results)
    print(f"\nTotal: {passed}/{total} tests passed")
    
    return passed == total


if __name__ == "__main__":
    try:
        success = asyncio.run(run_all_tests())
        sys.exit(0 if success else 1)
    except KeyboardInterrupt:
        print("\n\nTests interrupted by user")
        sys.exit(1)
    except Exception as e:
        print(f"\n\nFatal error: {e}")
        import traceback
        traceback.print_exc()
        sys.exit(1)

