"""
ProcessSupervisor - Manages subprocess execution with timeout and hang detection

Single Responsibility: Execute subprocesses safely with monitoring
"""

import subprocess
import threading
import queue
import time
import logging
from pathlib import Path
from typing import List, Optional, Dict, Callable
from dataclasses import dataclass


@dataclass
class ProcessResult:
    """
    Result of a subprocess execution.
    Condensed summary for pipeline services to interpret.
    """
    returncode: int
    stdout: str
    stderr: str
    stdout_tail: List[str]  # Last N lines of stdout
    stderr_tail: List[str]  # Last N lines of stderr
    execution_time: float
    timed_out: bool
    was_terminated: bool
    success: bool  # Final success flag (returncode == 0 or completion detected)


class ProcessSupervisor:
    """
    Manages subprocess execution with:
    - Timeout handling
    - Hang detection
    - Real-time output monitoring
    - Graceful termination
    """
    
    def __init__(self, logger: logging.Logger, timeout_seconds: int = 3600):
        self.logger = logger
        self.timeout_seconds = timeout_seconds
        self.no_output_timeout = 300  # 5 minutes
        self.progress_interval = 60  # 1 minute
    
    def execute(
        self,
        command: List[str],
        cwd: Path,
        on_stdout_line: Optional[Callable[[str], None]] = None,
        on_stderr_line: Optional[Callable[[str], None]] = None,
        on_progress: Optional[Callable[[Dict], None]] = None,
        completion_detector: Optional[Callable[[List[str]], bool]] = None,
        completion_timeout: int = 30,
        env: Optional[Dict[str, str]] = None
    ) -> ProcessResult:
        """
        Execute a subprocess with monitoring.
        
        Args:
            command: Command to execute
            cwd: Working directory
            on_stdout_line: Callback for each stdout line
            on_stderr_line: Callback for each stderr line
            on_progress: Callback for progress updates
            completion_detector: Function that returns True when process should be considered complete
            completion_timeout: Seconds to wait after completion_detector returns True before terminating
            env: Optional environment variables dict (if None, uses current environment)
        
        Returns:
            ProcessResult with execution details
        """
        start_time = time.time()
        process = subprocess.Popen(
            command,
            cwd=str(cwd),
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            bufsize=1,
            env=env
        )
        
        # Thread-safe queues for output
        stdout_queue = queue.Queue()
        stderr_queue = queue.Queue()
        stdout_lines = []
        stderr_lines = []
        
        def read_stdout():
            for line in iter(process.stdout.readline, ''):
                stdout_queue.put(line)
            process.stdout.close()
        
        def read_stderr():
            for line in iter(process.stderr.readline, ''):
                stderr_queue.put(line)
            process.stderr.close()
        
        stdout_thread = threading.Thread(target=read_stdout, daemon=True)
        stderr_thread = threading.Thread(target=read_stderr, daemon=True)
        stdout_thread.start()
        stderr_thread.start()
        
        # Monitoring loop
        last_output_time = time.time()
        last_progress_time = time.time()
        was_terminated = False
        timed_out = False
        
        while process.poll() is None:
            elapsed = time.time() - start_time
            
            # Check timeout
            if elapsed > self.timeout_seconds:
                self.logger.error(f"Process timed out after {self.timeout_seconds}s")
                process.kill()
                timed_out = True
                break
            
            # Process stdout
            try:
                while True:
                    line = stdout_queue.get_nowait()
                    if line.strip():
                        stdout_lines.append(line)
                        last_output_time = time.time()
                        if on_stdout_line:
                            on_stdout_line(line.strip())
            except queue.Empty:
                pass
            
            # Process stderr
            try:
                while True:
                    line = stderr_queue.get_nowait()
                    if line.strip():
                        stderr_lines.append(line)
                        last_output_time = time.time()
                        if on_stderr_line:
                            on_stderr_line(line.strip())
            except queue.Empty:
                pass
            
            # Check for completion (if detector provided)
            if completion_detector:
                if completion_detector(stdout_lines):
                    # Wait for natural exit
                    wait_start = time.time()
                    while process.poll() is None and (time.time() - wait_start) < completion_timeout:
                        time.sleep(0.5)
                    
                    # If still running, terminate
                    if process.poll() is None:
                        self.logger.warning(f"Process completed but didn't exit after {completion_timeout}s - terminating")
                        process.terminate()
                        time.sleep(2)
                        if process.poll() is None:
                            process.kill()
                        was_terminated = True
                        process.returncode = 0  # Treat as success
                        break
            
            # Progress updates
            if on_progress and (time.time() - last_progress_time) >= self.progress_interval:
                on_progress({
                    "elapsed_seconds": elapsed,
                    "stdout_lines": len(stdout_lines),
                    "stderr_lines": len(stderr_lines)
                })
                last_progress_time = time.time()
            
            # No output warning
            time_since_output = time.time() - last_output_time
            if time_since_output >= self.no_output_timeout:
                self.logger.warning(f"No output for {int(time_since_output)}s - process may be stuck")
                last_output_time = time.time()  # Reset to avoid spam
            
            time.sleep(0.5)
        
        # IMPORTANT: For fast-failing processes, the reader threads may still be draining
        # output when the process exits. We join first, then drain queues to avoid
        # dropping critical stderr (e.g., argparse usage errors) for short-lived runs.
        stdout_thread.join(timeout=5)
        stderr_thread.join(timeout=5)

        # Drain any remaining output after threads complete
        try:
            while True:
                stdout_lines.append(stdout_queue.get_nowait())
        except queue.Empty:
            pass

        try:
            while True:
                stderr_lines.append(stderr_queue.get_nowait())
        except queue.Empty:
            pass
        
        execution_time = time.time() - start_time
        
        # Get tail of output (last 100 lines each)
        stdout_tail = stdout_lines[-100:] if len(stdout_lines) > 100 else stdout_lines
        stderr_tail = stderr_lines[-100:] if len(stderr_lines) > 100 else stderr_lines
        
        # Determine success (returncode 0 or was terminated after completion)
        success = (process.returncode == 0) or (was_terminated and not timed_out)
        
        return ProcessResult(
            returncode=process.returncode if process.returncode is not None else 0,
            stdout=''.join(stdout_lines),
            stderr=''.join(stderr_lines),
            stdout_tail=stdout_tail,
            stderr_tail=stderr_tail,
            execution_time=execution_time,
            timed_out=timed_out,
            was_terminated=was_terminated,
            success=success
        )

