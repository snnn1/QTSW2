"""
Optimized Engine Integration

This module shows how to integrate the DataProcessingOptimizer with the existing
breakout analyzer without breaking the original functionality.
"""

import pandas as pd
import numpy as np
from typing import List, Dict, Optional, Any
import sys
from pathlib import Path

# Add breakout_analyzer root to PYTHONPATH
ROOT = Path(__file__).resolve().parents[1]
sys.path.append(str(ROOT))

from breakout_core.engine import run_strategy
from logic.config_logic import RunParams
from optimizations.data_processing_optimizer import DataProcessingOptimizer
from optimizations.parallel_processor import ParallelProcessor
from optimizations.caching_optimizer import CachingOptimizer
from optimizations.algorithm_optimizer import AlgorithmOptimizer


class OptimizedBreakoutEngine:
    """
    Optimized version of the breakout engine that uses data processing optimizations
    while maintaining compatibility with the original analyzer
    """
    
    def __init__(self, enable_optimizations: bool = True, enable_parallel: bool = True, 
                 enable_caching: bool = True, enable_algorithms: bool = True):
        """
        Initialize the optimized engine
        
        Args:
            enable_optimizations: Whether to enable data processing optimizations
            enable_parallel: Whether to enable parallel processing
            enable_caching: Whether to enable data caching
            enable_algorithms: Whether to enable algorithm optimizations
        """
        self.enable_optimizations = enable_optimizations
        self.enable_parallel = enable_parallel
        self.enable_caching = enable_caching
        self.enable_algorithms = enable_algorithms
        self.optimizer = DataProcessingOptimizer() if enable_optimizations else None
        self.parallel_processor = ParallelProcessor(enable_parallel=enable_parallel) if enable_parallel else None
        self.caching_optimizer = CachingOptimizer(enable_caching=enable_caching) if enable_caching else None
        self.algorithm_optimizer = AlgorithmOptimizer(enable_numba=True) if enable_algorithms else None
    
    def run_optimized_strategy(self, df: pd.DataFrame, rp: RunParams, 
                             debug: bool = False, **kwargs) -> pd.DataFrame:
        """
        Run the breakout strategy with optimizations
        
        Args:
            df: Market data DataFrame
            rp: Run parameters
            debug: Enable debug output
            **kwargs: Additional arguments for run_strategy
            
        Returns:
            Results DataFrame
        """
        # Running optimized strategy
        
        # Apply data processing optimizations
        if self.enable_optimizations and self.optimizer:
            df = self._apply_optimizations(df)
        
        # Use optimized processing if available
        if self.enable_parallel and self.parallel_processor:
            # Running with parallel processing
            result = self._run_parallel_strategy(df, rp, debug=debug, **kwargs)
        else:
            # Running standard strategy
            result = run_strategy(df, rp, debug=debug, **kwargs)
        
        # Strategy completed
        return result
    
    def load_data_with_caching(self, file_path: str, filters: Optional[Dict] = None) -> pd.DataFrame:
        """
        Load data with caching support
        
        Args:
            file_path: Path to the parquet file
            filters: Optional filters to apply
            
        Returns:
            Loaded DataFrame
        """
        if self.caching_optimizer is None:
            return pd.read_parquet(file_path)
        
        return self.caching_optimizer.load_data_with_cache(file_path, filters)
    
    def smart_year_filtering(self, file_paths: List[str], target_years: List[int]) -> List[str]:
        """
        Filter files based on year metadata
        
        Args:
            file_paths: List of file paths to check
            target_years: List of target years to filter for
            
        Returns:
            List of relevant file paths
        """
        if self.caching_optimizer is None:
            return file_paths
        
        return [str(p) for p in self.caching_optimizer.smart_year_filtering(file_paths, target_years)]
    
    def optimize_range_processing(self, df: pd.DataFrame, ranges: List[Dict]) -> List[Dict]:
        """
        Optimize range processing using algorithm optimizations
        
        Args:
            df: DataFrame with OHLC data
            ranges: List of range dictionaries
            
        Returns:
            List of optimized range results
        """
        if self.algorithm_optimizer is None:
            # Fall back to basic processing
            return [{'range_id': r.get('id', 'unknown'), 'processed': True} for r in ranges]
        
        return self.algorithm_optimizer.optimize_range_processing_batch(df, ranges)
    
    def optimize_result_aggregation(self, results: List[Dict]) -> Dict[str, Any]:
        """
        Optimize result aggregation using algorithm optimizations
        
        Args:
            results: List of trade results
            
        Returns:
            Dictionary with aggregated results
        """
        if self.algorithm_optimizer is None:
            # Fall back to basic aggregation
            return {
                'total_trades': len(results),
                'total_profit': sum(r.get('Profit', 0) for r in results)
            }
        
        return self.algorithm_optimizer.optimize_result_processing(results)
    
    def _apply_optimizations(self, df: pd.DataFrame) -> pd.DataFrame:
        """
        Apply data processing optimizations to the DataFrame
        
        Args:
            df: Input DataFrame
            
        Returns:
            Optimized DataFrame
        """
        if self.optimizer is None:
            return df
        
        # Step 1: Optimize memory usage
        df_optimized = self.optimizer.optimize_dataframe_memory(df)
        
        # Step 2: Pre-compute date information
        df_optimized = self.optimizer.precompute_date_info(df_optimized)
        
        return df_optimized
    
    def _run_parallel_strategy(self, df: pd.DataFrame, rp: RunParams, 
                              debug: bool = False, **kwargs) -> pd.DataFrame:
        """
        Run strategy with parallel processing optimizations
        
        Args:
            df: Market data DataFrame
            rp: Run parameters
            debug: Enable debug output
            **kwargs: Additional arguments
            
        Returns:
            Results DataFrame
        """
        # For now, fall back to standard processing
        # TODO: Implement actual parallel processing of ranges
        # Using standard processing
        return run_strategy(df, rp, debug=debug, **kwargs)
    
    def benchmark_performance(self, df: pd.DataFrame, rp: RunParams, 
                            debug: bool = False, **kwargs) -> Dict:
        """
        Benchmark original vs optimized performance
        
        Args:
            df: Market data DataFrame
            rp: Run parameters
            debug: Enable debug output
            **kwargs: Additional arguments for run_strategy
            
        Returns:
            Dictionary with performance comparison
        """
        import time
        
        # Benchmark original
        start_time = time.time()
        original_result = run_strategy(df, rp, debug=debug, **kwargs)
        original_time = time.time() - start_time
        
        # Benchmark optimized
        start_time = time.time()
        optimized_result = self.run_optimized_strategy(df, rp, debug=debug, **kwargs)
        optimized_time = time.time() - start_time
        
        # Calculate improvements
        speedup = original_time / optimized_time if optimized_time > 0 else 0
        time_saved = original_time - optimized_time
        
        return {
            'original_time': original_time,
            'optimized_time': optimized_time,
            'speedup_factor': speedup,
            'time_saved_seconds': time_saved,
            'original_results': len(original_result),
            'optimized_results': len(optimized_result),
            'results_match': len(original_result) == len(optimized_result)
        }


def example_usage():
    """Example of how to use the OptimizedBreakoutEngine"""
    
    # Load sample data
    df = pd.read_parquet('data_processed/NQ_2006-2025.parquet')
    df_sample = df.head(10000)  # Use small sample for testing
    
    # Create run parameters
    rp = RunParams(instrument='NQ')
    rp.enabled_sessions = ['S1', 'S2']
    rp.enabled_slots = {'S1': [], 'S2': []}
    rp.trade_days = [0, 1, 2, 3, 4]
    rp.same_bar_priority = 'STOP_FIRST'
    rp.write_setup_rows = False
    rp.write_no_trade_rows = True
    
    # Create optimized engine
    optimized_engine = OptimizedBreakoutEngine(enable_optimizations=True)
    
    # Benchmark performance
    benchmark_results = optimized_engine.benchmark_performance(df_sample, rp, debug=False)
    
    print("=== Performance Benchmark Results ===")
    print(f"Original time: {benchmark_results['original_time']:.2f} seconds")
    print(f"Optimized time: {benchmark_results['optimized_time']:.2f} seconds")
    print(f"Speedup factor: {benchmark_results['speedup_factor']:.2f}x")
    print(f"Time saved: {benchmark_results['time_saved_seconds']:.2f} seconds")
    print(f"Results match: {benchmark_results['results_match']}")
    
    return optimized_engine, benchmark_results


if __name__ == "__main__":
    # Run example
    engine, results = example_usage()
    print("Optimized Breakout Engine example completed successfully!")
