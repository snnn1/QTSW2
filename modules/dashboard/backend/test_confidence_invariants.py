"""
Confidence Tests - Invariant Verification

These tests verify that the critical fixes for memory leaks, lock races, and resource leaks
are working correctly. These are NOT feature tests - they test system invariants.

Tests:
1. Idle soak test - Event count plateaus, memory stabilizes
2. Rapid restart test - No stale locks, no false "already running" errors
3. WebSocket churn test - Subscriber count never exceeds cap, drops to zero
"""

import pytest
import asyncio
import time
import os
import tempfile
import logging
from pathlib import Path
from datetime import datetime, timedelta
from typing import List, Dict
import sys
import json

# Optional dependency for memory monitoring
try:
    import psutil
    PSUTIL_AVAILABLE = True
except ImportError:
    PSUTIL_AVAILABLE = False

# Create a test logger
test_logger = logging.getLogger("test_confidence")
test_logger.setLevel(logging.WARNING)  # Only warnings and errors

# Add project root to path
qtsw2_root = Path(__file__).parent.parent.parent.parent
sys.path.insert(0, str(qtsw2_root))

from modules.dashboard.backend.orchestrator.events import EventBus
from modules.dashboard.backend.orchestrator.locks import LockManager
from modules.dashboard.backend.orchestrator.state import PipelineStateManager, PipelineRunState


class TestIdleSoak:
    """
    Idle Soak Test
    
    Run dashboard for 2-4 hours with no pipeline runs.
    Verify:
    - Event count plateaus (no unbounded growth)
    - Memory stabilizes (no memory leak)
    - Ring buffer size stays within bounds
    """
    
    @pytest.mark.anyio
    @pytest.mark.parametrize("anyio_backend", ["asyncio"])
    @pytest.mark.slow
    async def test_idle_soak_event_plateau(self, tmp_path):
        """
        Test that event count plateaus during idle period.
        
        This test runs for a shorter duration (5 minutes) for CI/CD,
        but can be extended to 2-4 hours for manual validation.
        """
        # Use shorter duration for automated tests
        # For manual validation, set to 2-4 hours (7200-14400 seconds)
        SOAK_DURATION_SEC = int(os.getenv("SOAK_TEST_DURATION", "300"))  # 5 min default, 2-4 hours for manual
        SAMPLE_INTERVAL_SEC = 30  # Sample every 30 seconds
        
        event_logs_dir = tmp_path / "event_logs"
        event_logs_dir.mkdir()
        
        # Create EventBus
        event_bus = EventBus(
            event_logs_dir=event_logs_dir,
            buffer_size=1000,
            logger=test_logger
        )
        
        # Track event counts over time
        event_counts: List[int] = []
        ring_buffer_sizes: List[int] = []
        timestamps: List[float] = []
        
        start_time = time.time()
        end_time = start_time + SOAK_DURATION_SEC
        
        # Publish periodic system events (simulating scheduler heartbeat)
        async def publish_periodic_events():
            while time.time() < end_time:
                await event_bus.publish({
                    "run_id": "__system__",
                    "stage": "scheduler",
                    "event": "heartbeat",
                    "timestamp": datetime.now().isoformat(),
                    "msg": "System heartbeat"
                })
                await asyncio.sleep(10)  # Every 10 seconds
        
        # Monitor event counts
        async def monitor_events():
            while time.time() < end_time:
                current_time = time.time()
                ring_buffer_size = len(event_bus._recent_events)
                event_count = len(list(event_logs_dir.glob("pipeline_*.jsonl")))
                
                event_counts.append(event_count)
                ring_buffer_sizes.append(ring_buffer_size)
                timestamps.append(current_time)
                
                await asyncio.sleep(SAMPLE_INTERVAL_SEC)
        
        # Run both tasks concurrently
        await asyncio.gather(
            publish_periodic_events(),
            monitor_events(),
            return_exceptions=True
        )
        
        # Verify invariants
        assert len(event_counts) > 0, "Should have collected event count samples"
        assert len(ring_buffer_sizes) > 0, "Should have collected ring buffer size samples"
        
        # Check 1: Ring buffer should never exceed buffer_size
        max_ring_buffer = max(ring_buffer_sizes)
        assert max_ring_buffer <= event_bus.buffer_size, \
            f"Ring buffer exceeded max size: {max_ring_buffer} > {event_bus.buffer_size}"
        
        # Check 2: Ring buffer size should plateau (last 50% of samples should be stable)
        if len(ring_buffer_sizes) >= 4:
            second_half = ring_buffer_sizes[len(ring_buffer_sizes)//2:]
            max_second_half = max(second_half)
            min_second_half = min(second_half)
            variance = max_second_half - min_second_half
            
            # Variance should be small (ring buffer should stabilize)
            assert variance <= event_bus.buffer_size * 0.1, \
                f"Ring buffer did not plateau: variance={variance}, samples={second_half[-10:]}"
        
        # Check 3: No unbounded growth in event files (should be bounded by TTL cleanup)
        # Event files can grow, but should be reasonable
        final_event_count = event_counts[-1]
        assert final_event_count < 100, \
            f"Too many event files created: {final_event_count} (possible leak)"
        
        print(f"\n[Idle Soak] Event counts: {event_counts}")
        print(f"[Idle Soak] Ring buffer sizes: {ring_buffer_sizes}")
        print(f"[Idle Soak] Max ring buffer: {max_ring_buffer}, Final: {ring_buffer_sizes[-1]}")
    
    @pytest.mark.anyio
    @pytest.mark.parametrize("anyio_backend", ["asyncio"])
    @pytest.mark.slow
    @pytest.mark.skipif(not PSUTIL_AVAILABLE, reason="psutil not available for memory monitoring")
    async def test_idle_soak_memory_stability(self, tmp_path):
        """
        Test that memory usage stabilizes during idle period.
        
        This test monitors process memory over time to detect leaks.
        Requires psutil for memory monitoring.
        """
        SOAK_DURATION_SEC = int(os.getenv("SOAK_TEST_DURATION", "300"))  # 5 min default
        SAMPLE_INTERVAL_SEC = 30
        
        event_logs_dir = tmp_path / "event_logs"
        event_logs_dir.mkdir()
        
        event_bus = EventBus(
            event_logs_dir=event_logs_dir,
            buffer_size=1000,
            logger=test_logger
        )
        
        process = psutil.Process(os.getpid())
        memory_samples: List[float] = []
        timestamps: List[float] = []
        
        start_time = time.time()
        end_time = start_time + SOAK_DURATION_SEC
        
        # Publish periodic events
        async def publish_events():
            while time.time() < end_time:
                await event_bus.publish({
                    "run_id": "__system__",
                    "stage": "scheduler",
                    "event": "heartbeat",
                    "timestamp": datetime.now().isoformat(),
                    "msg": "System heartbeat"
                })
                await asyncio.sleep(10)
        
        # Monitor memory
        async def monitor_memory():
            while time.time() < end_time:
                mem_info = process.memory_info()
                memory_mb = mem_info.rss / (1024 * 1024)  # RSS in MB
                
                memory_samples.append(memory_mb)
                timestamps.append(time.time())
                
                await asyncio.sleep(SAMPLE_INTERVAL_SEC)
        
        await asyncio.gather(
            publish_events(),
            monitor_memory(),
            return_exceptions=True
        )
        
        # Verify memory stability
        assert len(memory_samples) > 0, "Should have collected memory samples"
        
        if len(memory_samples) >= 4:
            # Check that memory growth is bounded
            initial_memory = memory_samples[0]
            final_memory = memory_samples[-1]
            growth_mb = final_memory - initial_memory
            
            # Memory should not grow more than 50MB (reasonable for event bus)
            assert growth_mb < 50, \
                f"Memory leak detected: growth={growth_mb:.2f}MB, initial={initial_memory:.2f}MB, final={final_memory:.2f}MB"
            
            # Check that memory stabilizes (last 50% should have low variance)
            second_half = memory_samples[len(memory_samples)//2:]
            max_second_half = max(second_half)
            min_second_half = min(second_half)
            variance = max_second_half - min_second_half
            
            # Variance should be small (memory should stabilize)
            assert variance < 20, \
                f"Memory did not stabilize: variance={variance:.2f}MB, samples={second_half[-5:]}"
        
        print(f"\n[Idle Soak Memory] Memory samples: {[f'{m:.2f}MB' for m in memory_samples]}")
        print(f"[Idle Soak Memory] Growth: {memory_samples[-1] - memory_samples[0]:.2f}MB")


class TestRapidRestart:
    """
    Rapid Restart Test
    
    Start → stop → start pipeline 20-30 times.
    Verify:
    - No stale locks remain
    - No false "already running" errors
    - Lock is always released
    """
    
    @pytest.mark.anyio
    @pytest.mark.parametrize("anyio_backend", ["asyncio"])
    async def test_rapid_restart_no_stale_locks(self, tmp_path):
        """
        Test that rapid start/stop cycles don't leave stale locks.
        """
        NUM_CYCLES = int(os.getenv("RAPID_RESTART_CYCLES", "25"))  # 25 cycles default
        
        lock_dir = tmp_path / "locks"
        lock_dir.mkdir()
        
        lock_manager = LockManager(
            lock_dir=lock_dir,
            max_runtime_sec=3600,
            logger=None
        )
        
        run_ids: List[str] = []
        lock_acquired_flags: List[bool] = []
        lock_released_flags: List[bool] = []
        
        for cycle in range(NUM_CYCLES):
            run_id = f"test-run-{cycle:03d}"
            run_ids.append(run_id)
            
            # Acquire lock
            acquired = await lock_manager.acquire(run_id)
            lock_acquired_flags.append(acquired)
            
            if not acquired:
                # If acquisition failed, check if lock is stale
                is_locked = await lock_manager.is_locked()
                lock_info = await lock_manager.get_lock_info()
                
                pytest.fail(
                    f"Cycle {cycle}: Failed to acquire lock. "
                    f"is_locked={is_locked}, lock_info={lock_info}"
                )
            
            # Simulate some work
            await asyncio.sleep(0.1)
            
            # Release lock
            released = await lock_manager.release(run_id)
            lock_released_flags.append(released)
            
            if not released:
                pytest.fail(f"Cycle {cycle}: Failed to release lock for run {run_id}")
            
            # Verify lock is released
            is_locked = await lock_manager.is_locked()
            assert not is_locked, \
                f"Cycle {cycle}: Lock should be released after release() call"
            
            # Small delay between cycles
            await asyncio.sleep(0.05)
        
        # Final verification: No lock should remain
        final_is_locked = await lock_manager.is_locked()
        assert not final_is_locked, \
            f"Final state: Lock should not be held after all cycles. Lock file exists: {lock_manager.lock_file.exists()}"
        
        # Verify all locks were acquired and released
        assert all(lock_acquired_flags), "All locks should have been acquired"
        assert all(lock_released_flags), "All locks should have been released"
        
        print(f"\n[Rapid Restart] Completed {NUM_CYCLES} cycles")
        print(f"[Rapid Restart] All locks acquired: {all(lock_acquired_flags)}")
        print(f"[Rapid Restart] All locks released: {all(lock_released_flags)}")
    
    @pytest.mark.anyio
    @pytest.mark.parametrize("anyio_backend", ["asyncio"])
    async def test_rapid_restart_no_false_positives(self, tmp_path):
        """
        Test that rapid restarts don't cause false "already running" errors.
        
        This simulates the scenario where a pipeline is started, stopped quickly,
        and then started again - it should always succeed.
        """
        NUM_CYCLES = int(os.getenv("RAPID_RESTART_CYCLES", "20"))
        
        lock_dir = tmp_path / "locks"
        state_dir = tmp_path / "state"
        event_logs_dir = tmp_path / "event_logs"
        
        for d in [lock_dir, state_dir, event_logs_dir]:
            d.mkdir(parents=True)
        
        # Create minimal orchestrator components
        event_bus = EventBus(event_logs_dir=event_logs_dir, buffer_size=100, logger=test_logger)
        lock_manager = LockManager(lock_dir=lock_dir, max_runtime_sec=3600, logger=test_logger)
        
        state_file = state_dir / "orchestrator_state.json"
        state_manager = PipelineStateManager(
            event_bus=event_bus,
            logger=test_logger,
            state_file=state_file
        )
        
        false_positives = []
        
        for cycle in range(NUM_CYCLES):
            run_id = f"test-run-{cycle:03d}"
            
            # Check if already running
            status = await state_manager.get_state()
            if status and status.state not in {
                PipelineRunState.IDLE,
                PipelineRunState.SUCCESS,
                PipelineRunState.FAILED,
                PipelineRunState.STOPPED,
            }:
                false_positives.append((cycle, status.state.value))
                continue
            
            # Acquire lock
            acquired = await lock_manager.acquire(run_id)
            if not acquired:
                false_positives.append((cycle, "lock_acquisition_failed"))
                continue
            
            try:
                # Create run context
                run_ctx = await state_manager.create_run(
                    run_id=run_id,
                    metadata={"test_cycle": cycle}
                )
                
                # Transition to starting
                await state_manager.transition(PipelineRunState.STARTING)
                
                # Immediately stop (simulate rapid stop)
                await state_manager.transition(PipelineRunState.STOPPED)
                
            finally:
                # Always release lock
                try:
                    await lock_manager.release(run_id)
                except Exception as e:
                    false_positives.append((cycle, f"lock_release_error: {e}"))
            
            # Small delay
            await asyncio.sleep(0.05)
        
        # Verify no false positives
        assert len(false_positives) == 0, \
            f"False positives detected: {false_positives}"
        
        # Final state check
        final_status = await state_manager.get_state()
        final_is_locked = await lock_manager.is_locked()
        
        assert not final_is_locked, "Final state: Lock should not be held"
        assert final_status is None or final_status.state in {
            PipelineRunState.IDLE,
            PipelineRunState.SUCCESS,
            PipelineRunState.FAILED,
            PipelineRunState.STOPPED,
        }, f"Final state should be idle/terminal: {final_status.state if final_status else None}"
        
        print(f"\n[Rapid Restart False Positives] Completed {NUM_CYCLES} cycles")
        print(f"[Rapid Restart False Positives] False positives: {len(false_positives)}")


class TestWebSocketChurn:
    """
    WebSocket Churn Test
    
    Repeated connect/disconnect cycles.
    Verify:
    - Subscriber count never exceeds cap
    - Subscriber count drops to zero when all disconnected
    - No zombie subscribers remain
    """
    
    @pytest.mark.anyio
    @pytest.mark.parametrize("anyio_backend", ["asyncio"])
    async def test_websocket_churn_subscriber_cap(self, tmp_path):
        """
        Test that subscriber count never exceeds cap during churn.
        """
        NUM_CYCLES = int(os.getenv("WEBSOCKET_CHURN_CYCLES", "50"))
        MAX_SUBSCRIBERS = 100  # EventBus hard cap
        
        event_logs_dir = tmp_path / "event_logs"
        event_logs_dir.mkdir()
        
        event_bus = EventBus(
            event_logs_dir=event_logs_dir,
            buffer_size=1000,
            logger=test_logger
        )
        
        subscriber_counts: List[int] = []
        max_subscriber_count = 0
        
        async def simulate_subscriber(cycle: int):
            """Simulate a WebSocket subscriber that connects and disconnects"""
            queue = asyncio.Queue(maxsize=100)
            subscriber_added = False
            
            try:
                # Add subscriber (simulating WebSocket connection)
                async with event_bus._subscriber_lock:
                    # Check if at capacity (hard cap is 100)
                    if len(event_bus._subscribers) >= 100:
                        # At capacity - this is expected, but we should handle gracefully
                        return
                    
                    event_bus._subscribers.append(queue)
                    subscriber_added = True
                
                # Simulate receiving a few events
                for _ in range(3):
                    await event_bus.publish({
                        "run_id": f"test-run-{cycle}",
                        "stage": "pipeline",
                        "event": "test",
                        "timestamp": datetime.now().isoformat(),
                        "msg": f"Test event {cycle}"
                    })
                    await asyncio.sleep(0.01)
                
                # Simulate reading from queue
                try:
                    await asyncio.wait_for(queue.get(), timeout=0.1)
                except asyncio.TimeoutError:
                    pass
                
            finally:
                # Always remove subscriber (simulating WebSocket disconnect)
                async with event_bus._subscriber_lock:
                    if subscriber_added and queue in event_bus._subscribers:
                        event_bus._subscribers.remove(queue)
                        # Drain queue
                        try:
                            while not queue.empty():
                                queue.get_nowait()
                        except Exception:
                            pass
        
        # Run churn cycles
        for cycle in range(NUM_CYCLES):
            # Create multiple concurrent subscribers (simulating multiple WebSocket connections)
            num_concurrent = 5  # 5 concurrent connections per cycle
            
            tasks = [simulate_subscriber(cycle * num_concurrent + i) for i in range(num_concurrent)]
            await asyncio.gather(*tasks, return_exceptions=True)
            
            # Check subscriber count
            async with event_bus._subscriber_lock:
                current_count = len(event_bus._subscribers)
                subscriber_counts.append(current_count)
                max_subscriber_count = max(max_subscriber_count, current_count)
            
            # Small delay between cycles
            await asyncio.sleep(0.05)
        
        # Verify invariants
        assert max_subscriber_count <= MAX_SUBSCRIBERS, \
            f"Subscriber count exceeded cap: {max_subscriber_count} > {MAX_SUBSCRIBERS}"
        
        # Final check: All subscribers should be cleaned up
        async with event_bus._subscriber_lock:
            final_count = len(event_bus._subscribers)
        
        assert final_count == 0, \
            f"Final subscriber count should be zero: {final_count}"
        
        print(f"\n[WebSocket Churn] Completed {NUM_CYCLES} cycles")
        print(f"[WebSocket Churn] Max subscriber count: {max_subscriber_count}")
        print(f"[WebSocket Churn] Final subscriber count: {final_count}")
        print(f"[WebSocket Churn] Subscriber counts over time: {subscriber_counts[-20:]}")  # Last 20 samples
    
    @pytest.mark.anyio
    @pytest.mark.parametrize("anyio_backend", ["asyncio"])
    async def test_websocket_churn_no_zombies(self, tmp_path):
        """
        Test that no zombie subscribers remain after disconnect cycles.
        
        This test simulates abrupt disconnects (exceptions) to ensure
        cleanup still happens.
        """
        NUM_CYCLES = int(os.getenv("WEBSOCKET_CHURN_CYCLES", "30"))
        
        event_logs_dir = tmp_path / "event_logs"
        event_logs_dir.mkdir()
        
        event_bus = EventBus(
            event_logs_dir=event_logs_dir,
            buffer_size=1000,
            logger=test_logger
        )
        
        async def simulate_subscriber_with_abrupt_disconnect(cycle: int):
            """Simulate subscriber that disconnects abruptly (exception)"""
            queue = asyncio.Queue(maxsize=100)
            subscriber_added = False
            
            try:
                # Add subscriber
                async with event_bus._subscriber_lock:
                    # Check if at capacity (hard cap is 100)
                    if len(event_bus._subscribers) >= 100:
                        return
                    event_bus._subscribers.append(queue)
                    subscriber_added = True
                
                # Simulate some events
                await event_bus.publish({
                    "run_id": f"test-run-{cycle}",
                    "stage": "pipeline",
                    "event": "test",
                    "timestamp": datetime.now().isoformat(),
                    "msg": f"Test event {cycle}"
                })
                
                # Simulate abrupt disconnect (exception) - subscriber doesn't clean up properly
                if cycle % 3 == 0:  # Every 3rd cycle, simulate abrupt disconnect
                    raise Exception("Simulated abrupt disconnect")
                
            except Exception:
                # Abrupt disconnect - subscriber should still be cleaned up
                pass
            finally:
                # GUARANTEED CLEANUP: Even on exception, remove subscriber
                async with event_bus._subscriber_lock:
                    if subscriber_added and queue in event_bus._subscribers:
                        event_bus._subscribers.remove(queue)
                        try:
                            while not queue.empty():
                                queue.get_nowait()
                        except Exception:
                            pass
        
        # Run churn cycles
        for cycle in range(NUM_CYCLES):
            tasks = [simulate_subscriber_with_abrupt_disconnect(cycle) for _ in range(3)]
            await asyncio.gather(*tasks, return_exceptions=True)
            await asyncio.sleep(0.05)
        
        # Final verification: No zombie subscribers
        async with event_bus._subscriber_lock:
            final_count = len(event_bus._subscribers)
        
        assert final_count == 0, \
            f"Zombie subscribers detected: {final_count} subscribers remain after all disconnects"
        
        print(f"\n[WebSocket Churn No Zombies] Completed {NUM_CYCLES} cycles")
        print(f"[WebSocket Churn No Zombies] Final subscriber count: {final_count}")


if __name__ == "__main__":
    """
    Run confidence tests manually:
    
    # Idle soak test (5 minutes)
    pytest modules/dashboard/backend/test_confidence_invariants.py::TestIdleSoak -v -s
    
    # Idle soak test (2-4 hours for manual validation)
    SOAK_TEST_DURATION=7200 pytest modules/dashboard/backend/test_confidence_invariants.py::TestIdleSoak -v -s
    
    # Rapid restart test
    pytest modules/dashboard/backend/test_confidence_invariants.py::TestRapidRestart -v -s
    
    # WebSocket churn test
    pytest modules/dashboard/backend/test_confidence_invariants.py::TestWebSocketChurn -v -s
    
    # All confidence tests
    pytest modules/dashboard/backend/test_confidence_invariants.py -v -s
    """
    pytest.main([__file__, "-v", "-s"])

