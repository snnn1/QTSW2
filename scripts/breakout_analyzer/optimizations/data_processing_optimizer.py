"""
Data Processing Optimizations Module

This module provides optimized data processing functions that can be used
alongside the original analyzer to improve performance without breaking
the existing functionality.

Key optimizations:
1. Vectorized Operations: Replace Python loops with NumPy/Pandas vectorized operations
2. Binary Search Filtering: Use searchsorted() for faster data filtering
3. Memory Optimization: Optimize data types (int32 vs int64, float32 vs float64)
4. Data Pre-computation: Pre-calculate frequently used values (dates, weekdays, times)
"""

import pandas as pd
import numpy as np
from typing import List, Dict, Optional, Tuple
from dataclasses import dataclass
import time


@dataclass
class OptimizationStats:
    """Statistics for optimization performance tracking"""
    original_time: float
    optimized_time: float
    memory_saved_mb: float
    speedup_factor: float


class DataProcessingOptimizer:
    """Optimized data processing functions for breakout analyzer"""
    
    def __init__(self, enable_memory_optimization: bool = True):
        """
        Initialize the data processing optimizer
        
        Args:
            enable_memory_optimization: Whether to optimize data types for memory usage
        """
        self.enable_memory_optimization = enable_memory_optimization
        self.stats = {}
    
    def optimize_dataframe_memory(self, df: pd.DataFrame) -> pd.DataFrame:
        """
        Optimize DataFrame memory usage by converting data types
        
        Args:
            df: Input DataFrame
            
        Returns:
            Memory-optimized DataFrame
        """
        if not self.enable_memory_optimization:
            return df.copy()
        
        df_optimized = df.copy()
        
        # Optimize numeric columns
        for col in df_optimized.select_dtypes(include=[np.number]).columns:
            col_data = df_optimized[col]
            
            # Optimize integers
            if col_data.dtype in ['int64', 'int32']:
                if col_data.min() >= np.iinfo(np.int8).min and col_data.max() <= np.iinfo(np.int8).max:
                    df_optimized[col] = col_data.astype(np.int8)
                elif col_data.min() >= np.iinfo(np.int16).min and col_data.max() <= np.iinfo(np.int16).max:
                    df_optimized[col] = col_data.astype(np.int16)
                elif col_data.min() >= np.iinfo(np.int32).min and col_data.max() <= np.iinfo(np.int32).max:
                    df_optimized[col] = col_data.astype(np.int32)
            
            # Optimize floats
            elif col_data.dtype in ['float64', 'float32']:
                if col_data.min() >= np.finfo(np.float32).min and col_data.max() <= np.finfo(np.float32).max:
                    df_optimized[col] = col_data.astype(np.float32)
        
        # Optimize object columns (strings)
        for col in df_optimized.select_dtypes(include=['object']).columns:
            if df_optimized[col].dtype == 'object':
                try:
                    df_optimized[col] = df_optimized[col].astype('category')
                except:
                    pass  # Keep as object if conversion fails
        
        return df_optimized
    
    def precompute_date_info(self, df: pd.DataFrame) -> pd.DataFrame:
        """
        Pre-compute frequently used date information for faster filtering
        
        Args:
            df: DataFrame with timestamp column
            
        Returns:
            DataFrame with pre-computed date columns
        """
        df_precomputed = df.copy()
        
        # Pre-compute date components
        df_precomputed['date'] = df_precomputed['timestamp'].dt.date
        df_precomputed['weekday'] = df_precomputed['timestamp'].dt.weekday
        df_precomputed['time'] = df_precomputed['timestamp'].dt.time
        df_precomputed['hour'] = df_precomputed['timestamp'].dt.hour
        df_precomputed['minute'] = df_precomputed['timestamp'].dt.minute
        df_precomputed['year'] = df_precomputed['timestamp'].dt.year
        df_precomputed['month'] = df_precomputed['timestamp'].dt.month
        df_precomputed['day'] = df_precomputed['timestamp'].dt.day
        
        # Add performance optimizations for trading logic
        if all(col in df_precomputed.columns for col in ['high', 'low', 'open', 'close']):
            # Pre-compute price ranges for faster range detection
            df_precomputed['price_range'] = df_precomputed['high'] - df_precomputed['low']
            df_precomputed['price_mid'] = (df_precomputed['high'] + df_precomputed['low']) / 2
            
            # Pre-compute volatility indicators
            df_precomputed['volatility'] = df_precomputed['price_range'] / df_precomputed['close']
            
            # Sort by timestamp for faster lookups
            df_precomputed = df_precomputed.sort_values('timestamp').reset_index(drop=True)
            
            # Create index for faster time-based filtering
            df_precomputed['time_index'] = df_precomputed.index
        
        return df_precomputed
    
    def filter_data_binary_search(self, df: pd.DataFrame, start_time: pd.Timestamp, 
                                 end_time: pd.Timestamp) -> pd.DataFrame:
        """
        Filter data using binary search for O(log n) performance instead of O(n)
        
        Args:
            df: DataFrame with timestamp column (must be sorted)
            start_time: Start timestamp for filtering
            end_time: End timestamp for filtering
            
        Returns:
            Filtered DataFrame
        """
        if df.empty:
            return df
        
        # Ensure timestamp column is sorted
        if not df['timestamp'].is_monotonic_increasing:
            df = df.sort_values('timestamp').reset_index(drop=True)
        
        # Use binary search for fast filtering
        start_idx = df['timestamp'].searchsorted(start_time, side='left')
        end_idx = df['timestamp'].searchsorted(end_time, side='right')
        
        if start_idx >= end_idx:
            return pd.DataFrame(columns=df.columns)
        
        return df.iloc[start_idx:end_idx].copy()
    
    def vectorized_range_filtering(self, df: pd.DataFrame, ranges: List[Dict]) -> Dict[str, pd.DataFrame]:
        """
        Filter data for multiple ranges using vectorized operations
        
        Args:
            df: DataFrame with timestamp column
            ranges: List of range dictionaries with 'start_ts' and 'end_ts' keys
            
        Returns:
            Dictionary mapping range indices to filtered DataFrames
        """
        if df.empty or not ranges:
            return {}
        
        # Ensure data is sorted
        df_sorted = df.sort_values('timestamp').reset_index(drop=True)
        
        # Extract all start and end times
        start_times = [r['start_ts'] for r in ranges]
        end_times = [r['end_ts'] for r in ranges]
        
        # Use vectorized operations to find indices
        start_indices = df_sorted['timestamp'].searchsorted(start_times, side='left')
        end_indices = df_sorted['timestamp'].searchsorted(end_times, side='right')
        
        # Create filtered data for each range
        filtered_data = {}
        for i, (start_idx, end_idx) in enumerate(zip(start_indices, end_indices)):
            if start_idx < end_idx:
                filtered_data[i] = df_sorted.iloc[start_idx:end_idx].copy()
            else:
                filtered_data[i] = pd.DataFrame(columns=df.columns)
        
        return filtered_data
    
    def vectorized_entry_detection(self, df: pd.DataFrame, breakout_levels: Dict[str, float]) -> pd.DataFrame:
        """
        Detect entries using vectorized operations instead of loops
        
        Args:
            df: DataFrame with OHLC data
            breakout_levels: Dictionary with 'long' and 'short' breakout levels
            
        Returns:
            DataFrame with entry signals
        """
        df_signals = df.copy()
        
        # Vectorized breakout detection
        df_signals['long_breakout'] = df_signals['high'] >= breakout_levels['long']
        df_signals['short_breakout'] = df_signals['low'] <= breakout_levels['short']
        
        # Vectorized entry detection
        df_signals['entry_long'] = (
            df_signals['long_breakout'] & 
            (~df_signals['long_breakout'].shift(1).fillna(False).astype(bool))
        )
        
        df_signals['entry_short'] = (
            df_signals['short_breakout'] & 
            (~df_signals['short_breakout'].shift(1).fillna(False).astype(bool))
        )
        
        return df_signals
    
    def vectorized_profit_calculation(self, entry_prices: np.ndarray, exit_prices: np.ndarray, 
                                    directions: np.ndarray, targets: np.ndarray) -> np.ndarray:
        """
        Calculate profits using vectorized operations
        
        Args:
            entry_prices: Array of entry prices
            exit_prices: Array of exit prices
            directions: Array of trade directions (1 for long, -1 for short)
            targets: Array of target points
            
        Returns:
            Array of calculated profits
        """
        # Vectorized profit calculation
        price_diff = exit_prices - entry_prices
        profits = price_diff * directions * targets
        
        return profits
    
    def batch_process_ranges(self, df: pd.DataFrame, ranges: List[Dict], 
                           batch_size: int = 100) -> List[pd.DataFrame]:
        """
        Process multiple ranges in batches for better memory management
        
        Args:
            df: Input DataFrame
            ranges: List of range dictionaries
            batch_size: Number of ranges to process at once
            
        Returns:
            List of processed DataFrames
        """
        results = []
        
        for i in range(0, len(ranges), batch_size):
            batch_ranges = ranges[i:i + batch_size]
            
            # Process batch using vectorized operations
            batch_data = self.vectorized_range_filtering(df, batch_ranges)
            
            # Add to results
            for j, range_data in batch_data.items():
                results.append(range_data)
        
        return results
    
    def get_optimization_stats(self) -> Dict[str, OptimizationStats]:
        """
        Get performance statistics for optimizations
        
        Returns:
            Dictionary of optimization statistics
        """
        return self.stats.copy()
    
    def benchmark_optimization(self, original_func, optimized_func, *args, **kwargs) -> OptimizationStats:
        """
        Benchmark original vs optimized function
        
        Args:
            original_func: Original function to benchmark
            optimized_func: Optimized function to benchmark
            *args: Arguments to pass to functions
            **kwargs: Keyword arguments to pass to functions
            
        Returns:
            OptimizationStats object with performance comparison
        """
        # Benchmark original function
        start_time = time.time()
        original_result = original_func(*args, **kwargs)
        original_time = time.time() - start_time
        
        # Benchmark optimized function
        start_time = time.time()
        optimized_result = optimized_func(*args, **kwargs)
        optimized_time = time.time() - start_time
        
        # Calculate memory savings (approximate)
        original_memory = original_result.memory_usage(deep=True).sum() if hasattr(original_result, 'memory_usage') else 0
        optimized_memory = optimized_result.memory_usage(deep=True).sum() if hasattr(optimized_result, 'memory_usage') else 0
        memory_saved_mb = (original_memory - optimized_memory) / 1024 / 1024
        
        # Calculate speedup
        speedup_factor = original_time / optimized_time if optimized_time > 0 else float('inf')
        
        stats = OptimizationStats(
            original_time=original_time,
            optimized_time=optimized_time,
            memory_saved_mb=memory_saved_mb,
            speedup_factor=speedup_factor
        )
        
        return stats


# Example usage functions
def example_usage():
    """Example of how to use the DataProcessingOptimizer"""
    
    # Create optimizer
    optimizer = DataProcessingOptimizer(enable_memory_optimization=True)
    
    # Example DataFrame
    dates = pd.date_range('2024-01-01', periods=1000, freq='1min')
    df = pd.DataFrame({
        'timestamp': dates,
        'open': np.random.randn(1000) * 100 + 2000,
        'high': np.random.randn(1000) * 100 + 2000,
        'low': np.random.randn(1000) * 100 + 2000,
        'close': np.random.randn(1000) * 100 + 2000,
        'volume': np.random.randint(1000, 10000, 1000)
    })
    
    # Optimize memory usage
    df_optimized = optimizer.optimize_dataframe_memory(df)
    print(f"Original memory: {df.memory_usage(deep=True).sum() / 1024 / 1024:.2f} MB")
    print(f"Optimized memory: {df_optimized.memory_usage(deep=True).sum() / 1024 / 1024:.2f} MB")
    
    # Pre-compute date info
    df_precomputed = optimizer.precompute_date_info(df_optimized)
    
    # Binary search filtering
    start_time = pd.Timestamp('2024-01-01 10:00:00')
    end_time = pd.Timestamp('2024-01-01 12:00:00')
    filtered_data = optimizer.filter_data_binary_search(df_precomputed, start_time, end_time)
    
    print(f"Filtered data shape: {filtered_data.shape}")
    
    return optimizer


if __name__ == "__main__":
    # Run example
    optimizer = example_usage()
    print("Data Processing Optimizer example completed successfully!")
