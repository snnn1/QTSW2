"""
Continuous Testing and Monitoring - Run pipeline repeatedly and fix errors
"""

import asyncio
import sys
import logging
import time
from pathlib import Path
from datetime import datetime
import requests
import json

# Add project root to path
qtsw2_root = Path(__file__).parent.parent.parent
if str(qtsw2_root) not in sys.path:
    sys.path.insert(0, str(qtsw2_root))

# Configure logging
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s | %(levelname)s | %(message)s',
    handlers=[
        logging.StreamHandler(sys.stdout),
        logging.FileHandler(
            qtsw2_root / "logs" / "continuous_test.log",
            mode='a',
            encoding='utf-8'
        )
    ]
)

logger = logging.getLogger(__name__)

BASE_URL = "http://localhost:8001"


class ContinuousTester:
    """Continuously test the pipeline and fix errors"""
    
    def __init__(self):
        self.errors_found = []
        self.fixes_applied = []
        self.test_count = 0
        self.success_count = 0
        self.failure_count = 0
    
    def check_backend(self) -> bool:
        """Check if backend is running"""
        try:
            response = requests.get(f"{BASE_URL}/health", timeout=2)
            return response.status_code == 200
        except Exception as e:
            logger.error(f"Backend not responding: {e}")
            return False
    
    def get_status(self) -> dict:
        """Get current pipeline status"""
        try:
            response = requests.get(f"{BASE_URL}/api/pipeline/status", timeout=5)
            if response.status_code == 200:
                return response.json()
            else:
                logger.warning(f"Status request returned {response.status_code}")
                return {}
        except requests.exceptions.Timeout:
            logger.warning("Status request timed out")
            return {}
        except Exception as e:
            logger.warning(f"Failed to get status: {e}")
            return {}
    
    def reset_pipeline(self) -> bool:
        """Reset pipeline state and clear locks"""
        try:
            response = requests.post(f"{BASE_URL}/api/pipeline/reset", timeout=2)
            return response.status_code == 200
        except Exception as e:
            logger.error(f"Failed to reset: {e}")
            return False
    
    def start_pipeline(self) -> dict:
        """Start a pipeline run"""
        try:
            response = requests.post(
                f"{BASE_URL}/api/pipeline/start?manual=true",
                timeout=5
            )
            if response.status_code == 200:
                return response.json()
            else:
                logger.error(f"Failed to start: {response.status_code} - {response.text}")
                return {}
        except Exception as e:
            logger.error(f"Exception starting pipeline: {e}")
            return {}
    
    def check_for_errors(self, status: dict) -> list:
        """Check status for errors"""
        errors = []
        
        if status.get("error"):
            errors.append(f"Pipeline error: {status.get('error')}")
        
        if status.get("state") == "failed":
            errors.append(f"Pipeline failed: {status.get('error', 'Unknown error')}")
        
        return errors
    
    def fix_error(self, error: str) -> bool:
        """Attempt to fix an error"""
        logger.info(f"Attempting to fix: {error}")
        
        # Fix stale lock errors
        if "lock" in error.lower() or "already running" in error.lower():
            logger.info("  → Clearing stale lock...")
            if self.reset_pipeline():
                self.fixes_applied.append("Cleared stale lock")
                return True
        
        # Fix state errors
        if "state" in error.lower() or "transition" in error.lower():
            logger.info("  → Resetting pipeline state...")
            if self.reset_pipeline():
                self.fixes_applied.append("Reset pipeline state")
                return True
        
        return False
    
    async def monitor_run(self, run_id: str, max_wait: int = 120) -> dict:
        """Monitor a pipeline run until completion"""
        start_time = time.time()
        
        while time.time() - start_time < max_wait:
            await asyncio.sleep(3)
            
            try:
                status = self.get_status()
                if not status or not status.get("run_id"):
                    # Run completed and state cleared
                    return {"state": "completed", "final_status": {"state": "success"}}
                
                current_state = status.get("state")
                current_stage = status.get("current_stage", "unknown")
                
                logger.info(f"  [{int(time.time() - start_time)}s] State: {current_state}, Stage: {current_stage}")
                
                if current_state in ["success", "failed", "stopped"]:
                    return {"state": current_state, "final_status": status}
                
                # Check for errors
                errors = self.check_for_errors(status)
                if errors:
                    return {"state": "failed", "errors": errors, "final_status": status}
            except Exception as e:
                logger.warning(f"Error checking status: {e}")
                continue
        
        # Timeout
        final_status = self.get_status()
        return {"state": "timeout", "final_status": final_status}
    
    async def run_test_cycle(self) -> bool:
        """Run one test cycle"""
        self.test_count += 1
        logger.info(f"\n{'='*60}")
        logger.info(f"TEST CYCLE #{self.test_count}")
        logger.info(f"{'='*60}")
        
        # Check backend
        if not self.check_backend():
            logger.error("Backend not running!")
            return False
        
        logger.info("✓ Backend is running")
        
        # Check current status
        status = self.get_status()
        if status.get("state") and status.get("state") not in ["idle", "success", "failed", "stopped"]:
            logger.warning(f"Pipeline already running: {status.get('state')}")
            # Wait a bit and check again
            await asyncio.sleep(10)
            status = self.get_status()
            if status.get("state") and status.get("state") not in ["idle", "success", "failed", "stopped"]:
                logger.warning("Pipeline still running, resetting...")
                self.reset_pipeline()
                await asyncio.sleep(2)
        
        # Start pipeline
        logger.info("Starting pipeline run...")
        result = self.start_pipeline()
        
        if not result or not result.get("run_id"):
            error_msg = "Failed to start pipeline"
            logger.error(error_msg)
            self.errors_found.append(error_msg)
            
            # Try to fix
            if self.fix_error(error_msg):
                logger.info("Fix applied, retrying...")
                await asyncio.sleep(2)
                result = self.start_pipeline()
                if not result or not result.get("run_id"):
                    self.failure_count += 1
                    return False
            
            if not result or not result.get("run_id"):
                self.failure_count += 1
                return False
        
        run_id = result.get("run_id")
        logger.info(f"✓ Pipeline started: {run_id[:8]}")
        
        # Monitor run
        logger.info("Monitoring pipeline run...")
        final_status = await self.monitor_run(run_id)
        
        # Check result
        final_state = final_status.get("state", "unknown")
        final_status_data = final_status.get("final_status", {})
        
        if final_state == "success" or final_state == "completed" or final_status_data.get("state") == "success":
            logger.info("✓ Pipeline completed successfully!")
            self.success_count += 1
            return True
        elif final_state == "failed" or final_status_data.get("state") == "failed":
            errors = final_status.get("errors", [])
            error_msg = final_status_data.get("error", "Unknown error")
            logger.error(f"✗ Pipeline failed: {error_msg}")
            if errors:
                logger.error(f"  Errors: {errors}")
            self.errors_found.append(error_msg)
            self.failure_count += 1
            
            # Try to fix
            if self.fix_error(error_msg):
                logger.info("Fix applied")
            
            return False
        else:
            logger.warning(f"Pipeline ended in unexpected state: {final_state}")
            if final_status_data:
                logger.warning(f"  Final status: {final_status_data}")
            self.failure_count += 1
            return False
    
    async def run_continuous(self, cycles: int = 10, delay_between: int = 30):
        """Run continuous testing"""
        logger.info("="*60)
        logger.info("CONTINUOUS PIPELINE TESTING")
        logger.info("="*60)
        logger.info(f"Will run {cycles} test cycles")
        logger.info(f"Delay between cycles: {delay_between} seconds")
        logger.info("="*60)
        
        for i in range(cycles):
            success = await self.run_test_cycle()
            
            if i < cycles - 1:  # Don't wait after last cycle
                logger.info(f"\nWaiting {delay_between} seconds before next cycle...")
                await asyncio.sleep(delay_between)
        
        # Print summary
        logger.info("\n" + "="*60)
        logger.info("TESTING SUMMARY")
        logger.info("="*60)
        logger.info(f"Total tests: {self.test_count}")
        logger.info(f"Successful: {self.success_count}")
        logger.info(f"Failed: {self.failure_count}")
        logger.info(f"Success rate: {(self.success_count/self.test_count*100):.1f}%")
        
        if self.errors_found:
            logger.info(f"\nErrors found: {len(self.errors_found)}")
            for error in set(self.errors_found):
                logger.info(f"  - {error}")
        
        if self.fixes_applied:
            logger.info(f"\nFixes applied: {len(self.fixes_applied)}")
            for fix in set(self.fixes_applied):
                logger.info(f"  - {fix}")


async def main():
    """Main entry point"""
    tester = ContinuousTester()
    
    # Run 5 cycles with 30 second delays
    await tester.run_continuous(cycles=5, delay_between=30)


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        logger.info("\nTesting interrupted by user")
    except Exception as e:
        logger.error(f"Fatal error: {e}", exc_info=True)

