"""
Parallel Processing Module

This module provides parallel processing capabilities for the breakout analyzer
to utilize multiple CPU cores for faster range processing.

Key features:
1. ProcessPoolExecutor: Multi-process processing for CPU-bound work (bypasses GIL)
2. Chunk Processing: Split ranges into chunks for parallel execution
3. Shared memory: Optional shared-memory path to avoid DataFrame pickling overhead
4. Load Balancing: Distribute work evenly across processes
"""

import pandas as pd
import numpy as np
from typing import List, Dict, Optional, Tuple, Callable, Any
from dataclasses import dataclass
import time
import threading
from concurrent.futures import ProcessPoolExecutor, ThreadPoolExecutor, as_completed
import multiprocessing
import os
import uuid
from functools import partial

try:
    from multiprocessing import shared_memory
    SHARED_MEMORY_AVAILABLE = True
except ImportError:
    SHARED_MEMORY_AVAILABLE = False


def _build_df_from_shared_memory(shm_name: str, n_rows: int, instrument: str) -> pd.DataFrame:
    """
    Build DataFrame from shared memory block. Module-level for picklability.
    Worker keeps shm attached for the duration of chunk processing.
    """
    shm = shared_memory.SharedMemory(name=shm_name, create=False)
    try:
        # Layout: timestamp (int64) + open, high, low, close (float64 each) = 40 bytes/row
        ts_arr = np.ndarray((n_rows,), dtype=np.int64, buffer=shm.buf)
        offset = n_rows * 8
        o_arr = np.ndarray((n_rows,), dtype=np.float64, buffer=shm.buf, offset=offset)
        offset += n_rows * 8
        h_arr = np.ndarray((n_rows,), dtype=np.float64, buffer=shm.buf, offset=offset)
        offset += n_rows * 8
        l_arr = np.ndarray((n_rows,), dtype=np.float64, buffer=shm.buf, offset=offset)
        offset += n_rows * 8
        c_arr = np.ndarray((n_rows,), dtype=np.float64, buffer=shm.buf, offset=offset)
        timestamp = pd.to_datetime(ts_arr.copy(), unit="ns")
        df = pd.DataFrame({
            "timestamp": timestamp,
            "open": o_arr.copy(),
            "high": h_arr.copy(),
            "low": l_arr.copy(),
            "close": c_arr.copy(),
            "instrument": [instrument] * n_rows,
        })
        return df
    finally:
        shm.close()


def _process_chunk_with_shm(shm_metadata: Dict, process_func: Callable, chunk: List[Dict],
                            *args, **kwargs) -> List[Any]:
    """Process chunk using DataFrame built from shared memory. Module-level for picklability."""
    df = _build_df_from_shared_memory(
        shm_metadata["shm_name"],
        shm_metadata["n_rows"],
        shm_metadata["instrument"],
    )
    chunk_results = []
    for range_data in chunk:
        try:
            result = process_func(df, range_data, *args, **kwargs)
            chunk_results.append(result)
        except Exception as exc:
            print(f"[WARNING] Range processing failed: {exc}")
            chunk_results.append(None)
    return chunk_results


@dataclass
class ParallelStats:
    """Statistics for parallel processing performance"""
    total_ranges: int
    processed_ranges: int
    threads_used: int
    processing_time: float
    speedup_factor: float
    cpu_cores_available: int


class ParallelProcessor:
    """Parallel processing engine for breakout analyzer"""
    
    def __init__(self, max_workers: Optional[int] = None, enable_parallel: bool = True,
                 use_shared_memory: bool = False):
        """
        Initialize the parallel processor
        
        Args:
            max_workers: Maximum number of worker processes (None = auto-detect)
            enable_parallel: Whether to enable parallel processing
            use_shared_memory: Use shared memory to avoid DataFrame pickling (experimental)
        """
        self.enable_parallel = enable_parallel
        self.use_shared_memory = use_shared_memory and SHARED_MEMORY_AVAILABLE
        self.max_workers = max_workers or self._detect_optimal_workers()
        self.cpu_cores = multiprocessing.cpu_count()
        self.stats = {}
    
    def _detect_optimal_workers(self) -> int:
        """
        Detect optimal number of worker threads
        
        Returns:
            Optimal number of workers
        """
        cpu_count = multiprocessing.cpu_count()
        
        # Use 75% of CPU cores for parallel processing
        # Leave some cores free for system operations
        optimal_workers = max(1, int(cpu_count * 0.75))
        
        # Cap at 8 workers to avoid overhead
        optimal_workers = min(optimal_workers, 8)
        
        return optimal_workers
    
    def process_ranges_parallel(self, ranges: List[Dict], process_func: Callable, 
                              *args, **kwargs) -> List[Any]:
        """
        Process ranges in parallel using ThreadPoolExecutor
        
        Args:
            ranges: List of range dictionaries to process
            process_func: Function to process each range
            *args: Additional arguments for process_func
            **kwargs: Additional keyword arguments for process_func
            
        Returns:
            List of results from processing each range
        """
        if not self.enable_parallel or len(ranges) < 2:
            # Fall back to sequential processing for small datasets
            return self._process_ranges_sequential(ranges, process_func, *args, **kwargs)
        
        print(f"[PARALLEL] Starting parallel processing of {len(ranges)} ranges using {self.max_workers} processes")
        
        start_time = time.time()
        results = []
        
        # Split ranges into chunks for better load balancing
        chunk_size = max(1, len(ranges) // self.max_workers)
        range_chunks = self._split_into_chunks(ranges, chunk_size)
        
        print(f"[CHUNK] Split into {len(range_chunks)} chunks (avg {chunk_size} ranges per chunk)")
        
        # Process chunks in parallel (ProcessPoolExecutor bypasses GIL for CPU-bound work)
        with ProcessPoolExecutor(max_workers=self.max_workers) as executor:
            # Submit all chunks
            future_to_chunk = {
                executor.submit(self._process_chunk, chunk, process_func, *args, **kwargs): chunk
                for chunk in range_chunks
            }
            
            # Collect results as they complete
            for future in as_completed(future_to_chunk):
                chunk = future_to_chunk[future]
                try:
                    chunk_results = future.result()
                    results.extend(chunk_results)
                except Exception as exc:
                    print(f"[ERROR] Chunk processing failed: {exc}")
                    # Fall back to sequential processing for this chunk
                    chunk_results = self._process_chunk(chunk, process_func, *args, **kwargs)
                    results.extend(chunk_results)
        
        processing_time = time.time() - start_time
        
        # Calculate statistics
        self.stats = ParallelStats(
            total_ranges=len(ranges),
            processed_ranges=len(results),
            threads_used=self.max_workers,
            processing_time=processing_time,
            speedup_factor=0,  # Will be calculated by benchmark
            cpu_cores_available=self.cpu_cores
        )
        
        print(f"[SUCCESS] Parallel processing completed:")
        print(f"   Processed {len(results)} ranges in {processing_time:.2f} seconds")
        print(f"   Used {self.max_workers} processes")
        
        return results
    
    def _process_chunk(self, chunk: List[Dict], process_func: Callable, 
                      *args, **kwargs) -> List[Any]:
        """
        Process a chunk of ranges
        
        Args:
            chunk: List of ranges to process
            process_func: Function to process each range
            *args: Additional arguments for process_func
            **kwargs: Additional keyword arguments for process_func
            
        Returns:
            List of results from processing the chunk
        """
        chunk_results = []
        
        for range_data in chunk:
            try:
                result = process_func(range_data, *args, **kwargs)
                chunk_results.append(result)
            except Exception as exc:
                print(f"[WARNING] Range processing failed: {exc}")
                chunk_results.append(None)
        
        return chunk_results
    
    def _split_into_chunks(self, ranges: List[Dict], chunk_size: int) -> List[List[Dict]]:
        """
        Split ranges into chunks for parallel processing
        
        Args:
            ranges: List of ranges to split
            chunk_size: Size of each chunk
            
        Returns:
            List of chunks
        """
        chunks = []
        for i in range(0, len(ranges), chunk_size):
            chunk = ranges[i:i + chunk_size]
            chunks.append(chunk)
        
        return chunks
    
    def _process_ranges_sequential(self, ranges: List[Dict], process_func: Callable, 
                                 *args, **kwargs) -> List[Any]:
        """
        Process ranges sequentially (fallback)
        
        Args:
            ranges: List of ranges to process
            process_func: Function to process each range
            *args: Additional arguments for process_func
            **kwargs: Additional keyword arguments for process_func
            
        Returns:
            List of results from processing each range
        """
        print(f"[SEQUENTIAL] Processing {len(ranges)} ranges sequentially")
        
        start_time = time.time()
        results = []
        
        for range_data in ranges:
            try:
                result = process_func(range_data, *args, **kwargs)
                results.append(result)
            except Exception as exc:
                print(f"[WARNING] Range processing failed: {exc}")
                results.append(None)
        
        processing_time = time.time() - start_time
        print(f"[SUCCESS] Sequential processing completed in {processing_time:.2f} seconds")
        
        return results
    
    def process_dataframe_parallel(self, df: pd.DataFrame, ranges: List[Dict], 
                                 process_func: Callable, *args, **kwargs) -> List[Any]:
        """
        Process DataFrame with ranges in parallel
        
        Args:
            df: DataFrame to process
            ranges: List of ranges to process
            process_func: Function (df, range_dict, *args) to process each range
            *args: Additional arguments for process_func
            **kwargs: Additional keyword arguments for process_func
            
        Returns:
            List of results from processing each range
        """
        if not self.enable_parallel or len(ranges) < 2:
            return self._process_dataframe_sequential(df, ranges, process_func, *args, **kwargs)
        
        print(f"[PARALLEL] Starting parallel DataFrame processing of {len(ranges)} ranges")
        
        start_time = time.time()
        
        if self.use_shared_memory:
            results = self._process_dataframe_parallel_shm(df, ranges, process_func, *args, **kwargs)
        else:
            df_processed = self._prepare_dataframe_for_parallel(df)
            process_func_with_df = partial(process_func, df_processed)
            results = self.process_ranges_parallel(ranges, process_func_with_df, *args, **kwargs)
        
        processing_time = time.time() - start_time
        print(f"[SUCCESS] Parallel DataFrame processing completed in {processing_time:.2f} seconds")
        
        return results
    
    def _process_dataframe_parallel_shm(self, df: pd.DataFrame, ranges: List[Dict],
                                        process_func: Callable, *args, **kwargs) -> List[Any]:
        """Process using shared memory to avoid pickling DataFrame to each worker."""
        df_prep = self._prepare_dataframe_for_parallel(df)
        n_rows = len(df_prep)
        instrument = str(df_prep["instrument"].iloc[0]) if "instrument" in df_prep.columns and n_rows > 0 else "ES"
        
        # Layout: timestamp (int64) + open, high, low, close (float64) = 40 bytes/row
        shm_size = n_rows * 40
        shm_name = f"analyzer_{uuid.uuid4().hex[:12]}"
        shm = shared_memory.SharedMemory(name=shm_name, create=True, size=shm_size)
        
        try:
            ts = pd.to_datetime(df_prep["timestamp"]).astype(np.int64)
            np.ndarray((n_rows,), dtype=np.int64, buffer=shm.buf)[:] = ts.values
            offset = n_rows * 8
            np.ndarray((n_rows,), dtype=np.float64, buffer=shm.buf, offset=offset)[:] = df_prep["open"].values
            offset += n_rows * 8
            np.ndarray((n_rows,), dtype=np.float64, buffer=shm.buf, offset=offset)[:] = df_prep["high"].values
            offset += n_rows * 8
            np.ndarray((n_rows,), dtype=np.float64, buffer=shm.buf, offset=offset)[:] = df_prep["low"].values
            offset += n_rows * 8
            np.ndarray((n_rows,), dtype=np.float64, buffer=shm.buf, offset=offset)[:] = df_prep["close"].values
            
            shm_metadata = {"shm_name": shm_name, "n_rows": n_rows, "instrument": instrument}
            
            chunk_size = max(1, len(ranges) // self.max_workers)
            range_chunks = self._split_into_chunks(ranges, chunk_size)
            
            print(f"[SHARED-MEM] Using shared memory ({n_rows:,} rows, {shm_size/1024/1024:.1f} MB)")
            
            results = []
            with ProcessPoolExecutor(max_workers=self.max_workers) as executor:
                future_to_chunk = {
                    executor.submit(_process_chunk_with_shm, shm_metadata, process_func, chunk, *args, **kwargs): chunk
                    for chunk in range_chunks
                }
                for future in as_completed(future_to_chunk):
                    try:
                        chunk_results = future.result()
                        results.extend(chunk_results)
                    except Exception as exc:
                        print(f"[ERROR] Chunk processing failed: {exc}")
                        chunk = future_to_chunk[future]
                        chunk_results = _process_chunk_with_shm(shm_metadata, process_func, chunk, *args, **kwargs)
                        results.extend(chunk_results)
        finally:
            shm.close()
            shm.unlink()
        
        return results
    
    def _prepare_dataframe_for_parallel(self, df: pd.DataFrame) -> pd.DataFrame:
        """
        Prepare DataFrame for parallel processing
        
        Args:
            df: Input DataFrame
            
        Returns:
            Prepared DataFrame
        """
        # Make DataFrame thread-safe by creating a copy
        df_copy = df.copy()
        
        # Ensure timestamp column is sorted for binary search
        if 'timestamp' in df_copy.columns:
            df_copy = df_copy.sort_values('timestamp').reset_index(drop=True)
        
        return df_copy
    
    def _process_dataframe_sequential(self, df: pd.DataFrame, ranges: List[Dict], 
                                    process_func: Callable, *args, **kwargs) -> List[Any]:
        """
        Process DataFrame with ranges sequentially (fallback)
        
        Args:
            df: DataFrame to process
            ranges: List of ranges to process
            process_func: Function to process each range
            *args: Additional arguments for process_func
            **kwargs: Additional keyword arguments for process_func
            
        Returns:
            List of results from processing each range
        """
        print(f"[SEQUENTIAL] Processing DataFrame with {len(ranges)} ranges sequentially")
        
        start_time = time.time()
        results = []
        
        for range_data in ranges:
            try:
                result = process_func(df, range_data, *args, **kwargs)
                results.append(result)
            except Exception as exc:
                print(f"[WARNING] Range processing failed: {exc}")
                results.append(None)
        
        processing_time = time.time() - start_time
        print(f"[SUCCESS] Sequential DataFrame processing completed in {processing_time:.2f} seconds")
        
        return results
    
    def benchmark_parallel_vs_sequential(self, ranges: List[Dict], process_func: Callable, 
                                       *args, **kwargs) -> Dict[str, float]:
        """
        Benchmark parallel vs sequential processing
        
        Args:
            ranges: List of ranges to process
            process_func: Function to process each range
            *args: Additional arguments for process_func
            **kwargs: Additional keyword arguments for process_func
            
        Returns:
            Dictionary with benchmark results
        """
        print("[BENCHMARK] Benchmarking parallel vs sequential processing...")
        
        # Benchmark sequential processing
        self.enable_parallel = False
        start_time = time.time()
        sequential_results = self.process_ranges_parallel(ranges, process_func, *args, **kwargs)
        sequential_time = time.time() - start_time
        
        # Benchmark parallel processing
        self.enable_parallel = True
        start_time = time.time()
        parallel_results = self.process_ranges_parallel(ranges, process_func, *args, **kwargs)
        parallel_time = time.time() - start_time
        
        # Calculate speedup
        speedup = sequential_time / parallel_time if parallel_time > 0 else 0
        
        benchmark_results = {
            'sequential_time': sequential_time,
            'parallel_time': parallel_time,
            'speedup_factor': speedup,
            'time_saved': sequential_time - parallel_time,
            'sequential_results': len(sequential_results),
            'parallel_results': len(parallel_results),
            'results_match': len(sequential_results) == len(parallel_results)
        }
        
        print(f"📈 Benchmark Results:")
        print(f"   Sequential time: {sequential_time:.2f} seconds")
        print(f"   Parallel time: {parallel_time:.2f} seconds")
        print(f"   Speedup: {speedup:.2f}x")
        print(f"   Time saved: {benchmark_results['time_saved']:.2f} seconds")
        print(f"   Results match: {benchmark_results['results_match']}")
        
        return benchmark_results
    
    def get_stats(self) -> ParallelStats:
        """
        Get parallel processing statistics
        
        Returns:
            ParallelStats object
        """
        return self.stats
    
    def get_cpu_info(self) -> Dict[str, int]:
        """
        Get CPU information
        
        Returns:
            Dictionary with CPU information
        """
        return {
            'cpu_cores': self.cpu_cores,
            'max_workers': self.max_workers,
            'optimal_workers': self._detect_optimal_workers()
        }


# Example usage functions
def example_range_processor(range_data: Dict, df: pd.DataFrame) -> Dict:
    """
    Example function to process a single range
    
    Args:
        range_data: Range dictionary
        df: DataFrame to process
        
    Returns:
        Processed range result
    """
    # Simulate some processing
    time.sleep(0.01)  # Simulate processing time
    
    return {
        'range_id': range_data.get('id', 'unknown'),
        'processed': True,
        'timestamp': time.time()
    }


def example_usage():
    """Example of how to use the ParallelProcessor"""
    
    # Create processor
    processor = ParallelProcessor(max_workers=4, enable_parallel=True)
    
    # Create example ranges
    ranges = [
        {'id': f'range_{i}', 'start_ts': pd.Timestamp('2024-01-01'), 'end_ts': pd.Timestamp('2024-01-02')}
        for i in range(20)
    ]
    
    # Create example DataFrame
    df = pd.DataFrame({
        'timestamp': pd.date_range('2024-01-01', periods=1000, freq='1min'),
        'price': np.random.randn(1000) * 100 + 2000
    })
    
    # Process ranges in parallel
    results = processor.process_ranges_parallel(ranges, example_range_processor, df)
    
    print(f"Processed {len(results)} ranges")
    
    # Benchmark parallel vs sequential
    benchmark = processor.benchmark_parallel_vs_sequential(ranges, example_range_processor, df)
    
    return processor, benchmark


if __name__ == "__main__":
    # Run example
    processor, benchmark = example_usage()
    print("Parallel Processor example completed successfully!")


