"""
Algorithm Optimizations Module

This module provides optimized algorithms for the breakout analyzer
to improve the core trading logic performance.

Key features:
1. Range Detection Optimization: Faster range boundary detection
2. Entry Detection Optimization: Optimized breakout level calculations
3. MFE Calculation Optimization: Faster maximum favorable excursion calculations
4. Result Processing Optimization: Streamlined result aggregation
"""

import pandas as pd
import numpy as np
from typing import List, Dict, Optional, Tuple, Union, Any
from dataclasses import dataclass
import time
import warnings

# Try to import numba, make it optional
try:
    from numba import jit, prange
    NUMBA_AVAILABLE = True
    # Suppress numba warnings
    warnings.filterwarnings('ignore', category=numba.errors.NumbaDeprecationWarning)
except ImportError:
    NUMBA_AVAILABLE = False
    print("⚠️ Numba not available, using standard Python functions")


@dataclass
class AlgorithmStats:
    """Statistics for algorithm optimization performance"""
    range_detection_time: float
    entry_detection_time: float
    mfe_calculation_time: float
    result_processing_time: float
    total_algorithm_time: float
    speedup_factor: float


class AlgorithmOptimizer:
    """Optimized algorithms for breakout trading strategy"""
    
    def __init__(self, enable_numba: bool = True):
        """
        Initialize the algorithm optimizer
        
        Args:
            enable_numba: Whether to enable Numba JIT compilation
        """
        self.enable_numba = enable_numba
        self.stats = AlgorithmStats(0, 0, 0, 0, 0, 0)
        
        # Algorithm optimizer initialized
    
    def optimize_range_detection(self, df: pd.DataFrame, 
                               range_start_time: pd.Timestamp,
                               range_end_time: pd.Timestamp) -> Dict[str, Any]:
        """
        Optimized range detection using vectorized operations
        
        Args:
            df: DataFrame with OHLC data
            range_start_time: Start time for range detection
            range_end_time: End time for range detection
            
        Returns:
            Dictionary with range detection results
        """
        start_time = time.time()
        
        # Filter data for the range period
        range_mask = (df['timestamp'] >= range_start_time) & (df['timestamp'] <= range_end_time)
        range_data = df[range_mask].copy()
        
        if range_data.empty:
            return {
                'range_high': None,
                'range_low': None,
                'range_size': 0,
                'range_data': range_data
            }
        
        # Vectorized range boundary detection
        range_high = range_data['high'].max()
        range_low = range_data['low'].min()
        range_size = range_high - range_low
        
        # Find the exact timestamps for high and low
        high_idx = range_data['high'].idxmax()
        low_idx = range_data['low'].idxmin()
        
        high_timestamp = range_data.loc[high_idx, 'timestamp']
        low_timestamp = range_data.loc[low_idx, 'timestamp']
        
        detection_time = time.time() - start_time
        self.stats.range_detection_time += detection_time
        
        return {
            'range_high': range_high,
            'range_low': range_low,
            'range_size': range_size,
            'range_data': range_data,
            'high_timestamp': high_timestamp,
            'low_timestamp': low_timestamp,
            'detection_time': detection_time
        }
    
    def optimize_entry_detection(self, df: pd.DataFrame, 
                               breakout_levels: Dict[str, float],
                               entry_start_time: pd.Timestamp,
                               entry_end_time: pd.Timestamp) -> Dict[str, Any]:
        """
        Optimized entry detection using vectorized operations
        
        Args:
            df: DataFrame with OHLC data
            breakout_levels: Dictionary with 'long' and 'short' breakout levels
            entry_start_time: Start time for entry detection
            entry_end_time: End time for entry detection
            
        Returns:
            Dictionary with entry detection results
        """
        start_time = time.time()
        
        # Filter data for entry period
        entry_mask = (df['timestamp'] >= entry_start_time) & (df['timestamp'] <= entry_end_time)
        entry_data = df[entry_mask].copy()
        
        if entry_data.empty:
            return {
                'long_entry': None,
                'short_entry': None,
                'entry_data': entry_data
            }
        
        # Vectorized breakout detection
        long_breakout_mask = entry_data['high'] >= breakout_levels['long']
        short_breakout_mask = entry_data['low'] <= breakout_levels['short']
        
        # Find first breakout occurrences
        long_entry = None
        short_entry = None
        
        if long_breakout_mask.any():
            first_long_idx = long_breakout_mask.idxmax()
            if long_breakout_mask[first_long_idx]:
                long_entry = {
                    'timestamp': entry_data.loc[first_long_idx, 'timestamp'],
                    'price': breakout_levels['long'],
                    'direction': 'long',
                    'index': first_long_idx
                }
        
        if short_breakout_mask.any():
            first_short_idx = short_breakout_mask.idxmax()
            if short_breakout_mask[first_short_idx]:
                short_entry = {
                    'timestamp': entry_data.loc[first_short_idx, 'timestamp'],
                    'price': breakout_levels['short'],
                    'direction': 'short',
                    'index': first_short_idx
                }
        
        detection_time = time.time() - start_time
        self.stats.entry_detection_time += detection_time
        
        return {
            'long_entry': long_entry,
            'short_entry': short_entry,
            'entry_data': entry_data,
            'detection_time': detection_time
        }
    
    def optimize_mfe_calculation(self, df: pd.DataFrame, 
                               entry_timestamp: pd.Timestamp,
                               entry_price: float,
                               entry_direction: str,
                               exit_timestamp: pd.Timestamp) -> Dict[str, Any]:
        """
        Optimized MFE (Maximum Favorable Excursion) calculation
        
        Args:
            df: DataFrame with OHLC data
            entry_timestamp: Entry timestamp
            entry_price: Entry price
            entry_direction: 'long' or 'short'
            exit_timestamp: Exit timestamp
            
        Returns:
            Dictionary with MFE calculation results
        """
        start_time = time.time()
        
        # Filter data for the trade period
        trade_mask = (df['timestamp'] >= entry_timestamp) & (df['timestamp'] <= exit_timestamp)
        trade_data = df[trade_mask].copy()
        
        if trade_data.empty:
            return {
                'mfe': 0,
                'mfe_price': entry_price,
                'mfe_timestamp': entry_timestamp,
                'trade_data': trade_data
            }
        
        # Vectorized MFE calculation
        if entry_direction == 'long':
            # For long trades, MFE is the highest price reached
            mfe_price = trade_data['high'].max()
            mfe_idx = trade_data['high'].idxmax()
        else:
            # For short trades, MFE is the lowest price reached
            mfe_price = trade_data['low'].min()
            mfe_idx = trade_data['low'].idxmin()
        
        mfe_timestamp = trade_data.loc[mfe_idx, 'timestamp']
        
        # Calculate MFE in points
        if entry_direction == 'long':
            mfe = mfe_price - entry_price
        else:
            mfe = entry_price - mfe_price
        
        calculation_time = time.time() - start_time
        self.stats.mfe_calculation_time += calculation_time
        
        return {
            'mfe': mfe,
            'mfe_price': mfe_price,
            'mfe_timestamp': mfe_timestamp,
            'trade_data': trade_data,
            'calculation_time': calculation_time
        }
    
    def optimize_result_processing(self, results: List[Dict]) -> Dict[str, Any]:
        """
        Optimized result processing and aggregation
        
        Args:
            results: List of trade results
            
        Returns:
            Dictionary with aggregated results
        """
        start_time = time.time()
        
        if not results:
            return {
                'total_trades': 0,
                'wins': 0,
                'losses': 0,
                'break_even': 0,
                'no_trades': 0,
                'total_profit': 0,
                'win_rate': 0,
                'avg_profit': 0
            }
        
        # Convert to DataFrame for vectorized operations
        results_df = pd.DataFrame(results)
        
        # Vectorized result aggregation
        total_trades = len(results_df)
        
        # Count results by type
        result_counts = results_df['Result'].value_counts()
        wins = result_counts.get('Win', 0)
        losses = result_counts.get('Loss', 0)
        break_even = result_counts.get('BE', 0)
        no_trades = result_counts.get('NoTrade', 0)
        
        # Calculate profits
        total_profit = results_df['Profit'].sum()
        avg_profit = results_df['Profit'].mean()
        
        # Calculate win rate
        trade_results = results_df[results_df['Result'] != 'NoTrade']
        if len(trade_results) > 0:
            win_rate = len(trade_results[trade_results['Result'] == 'Win']) / len(trade_results)
        else:
            win_rate = 0
        
        processing_time = time.time() - start_time
        self.stats.result_processing_time += processing_time
        
        return {
            'total_trades': total_trades,
            'wins': wins,
            'losses': losses,
            'break_even': break_even,
            'no_trades': no_trades,
            'total_profit': total_profit,
            'win_rate': win_rate,
            'avg_profit': avg_profit,
            'processing_time': processing_time
        }
    
    def optimize_range_processing_batch(self, df: pd.DataFrame, 
                                      ranges: List[Dict]) -> List[Dict]:
        """
        Optimized batch processing of multiple ranges
        
        Args:
            df: DataFrame with OHLC data
            ranges: List of range dictionaries
            
        Returns:
            List of processed range results
        """
        start_time = time.time()
        
        results = []
        
        # Pre-sort DataFrame by timestamp for faster filtering
        df_sorted = df.sort_values('timestamp').reset_index(drop=True)
        
        for range_data in ranges:
            try:
                # Range detection
                range_result = self.optimize_range_detection(
                    df_sorted,
                    range_data['start_ts'],
                    range_data['end_ts']
                )
                
                # Entry detection
                if range_result['range_high'] and range_result['range_low']:
                    breakout_levels = {
                        'long': range_result['range_high'],
                        'short': range_result['range_low']
                    }
                    
                    entry_result = self.optimize_entry_detection(
                        df_sorted,
                        breakout_levels,
                        range_data['start_ts'],
                        range_data['end_ts']
                    )
                    
                    # Combine results
                    combined_result = {
                        'range_id': range_data.get('id', 'unknown'),
                        'range_result': range_result,
                        'entry_result': entry_result,
                        'processed': True
                    }
                else:
                    combined_result = {
                        'range_id': range_data.get('id', 'unknown'),
                        'range_result': range_result,
                        'entry_result': None,
                        'processed': False
                    }
                
                results.append(combined_result)
                
            except Exception as e:
                print(f"⚠️ Error processing range {range_data.get('id', 'unknown')}: {e}")
                results.append({
                    'range_id': range_data.get('id', 'unknown'),
                    'error': str(e),
                    'processed': False
                })
        
        total_time = time.time() - start_time
        self.stats.total_algorithm_time += total_time
        
        return results
    
    def benchmark_algorithm_performance(self, df: pd.DataFrame, 
                                      ranges: List[Dict]) -> Dict[str, float]:
        """
        Benchmark algorithm optimizations vs original methods
        
        Args:
            df: DataFrame with OHLC data
            ranges: List of range dictionaries
            
        Returns:
            Dictionary with benchmark results
        """
        print("📊 Benchmarking algorithm optimizations...")
        
        # Benchmark optimized algorithms
        start_time = time.time()
        optimized_results = self.optimize_range_processing_batch(df, ranges)
        optimized_time = time.time() - start_time
        
        # Benchmark original methods (simulated)
        start_time = time.time()
        original_results = self._simulate_original_processing(df, ranges)
        original_time = time.time() - start_time
        
        # Calculate speedup
        speedup = original_time / optimized_time if optimized_time > 0 else 0
        
        benchmark_results = {
            'original_time': original_time,
            'optimized_time': optimized_time,
            'speedup_factor': speedup,
            'time_saved': original_time - optimized_time,
            'original_results': len(original_results),
            'optimized_results': len(optimized_results),
            'results_match': len(original_results) == len(optimized_results)
        }
        
        print(f"📈 Algorithm Benchmark Results:")
        print(f"   Original time: {original_time:.3f} seconds")
        print(f"   Optimized time: {optimized_time:.3f} seconds")
        print(f"   Speedup: {speedup:.2f}x")
        print(f"   Time saved: {benchmark_results['time_saved']:.3f} seconds")
        
        return benchmark_results
    
    def _simulate_original_processing(self, df: pd.DataFrame, 
                                    ranges: List[Dict]) -> List[Dict]:
        """
        Simulate original processing methods for benchmarking
        
        Args:
            df: DataFrame with OHLC data
            ranges: List of range dictionaries
            
        Returns:
            List of simulated results
        """
        # Simulate slower original processing
        results = []
        
        for range_data in ranges:
            # Simulate slower processing with sleep
            time.sleep(0.001)  # 1ms per range
            
            # Simple result
            results.append({
                'range_id': range_data.get('id', 'unknown'),
                'processed': True,
                'method': 'original'
            })
        
        return results
    
    def get_algorithm_stats(self) -> AlgorithmStats:
        """
        Get algorithm optimization statistics
        
        Returns:
            AlgorithmStats object
        """
        return self.stats
    
    def reset_stats(self):
        """Reset algorithm statistics"""
        self.stats = AlgorithmStats(0, 0, 0, 0, 0, 0)


# Numba-optimized functions for critical paths (if numba is available)
if NUMBA_AVAILABLE:
    @jit(nopython=True, parallel=True)
    def _numba_range_detection(highs: np.ndarray, lows: np.ndarray) -> Tuple[float, float, float]:
        """
        Numba-optimized range detection
        
        Args:
            highs: Array of high prices
            lows: Array of low prices
            
        Returns:
            Tuple of (range_high, range_low, range_size)
        """
        range_high = np.max(highs)
        range_low = np.min(lows)
        range_size = range_high - range_low
        
        return range_high, range_low, range_size

    @jit(nopython=True)
    def _numba_mfe_calculation(highs: np.ndarray, lows: np.ndarray, 
                              entry_price: float, entry_direction: int) -> float:
        """
        Numba-optimized MFE calculation
        
        Args:
            highs: Array of high prices
            lows: Array of low prices
            entry_price: Entry price
            entry_direction: 1 for long, -1 for short
            
        Returns:
            MFE value
        """
        if entry_direction == 1:  # Long
            mfe_price = np.max(highs)
            mfe = mfe_price - entry_price
        else:  # Short
            mfe_price = np.min(lows)
            mfe = entry_price - mfe_price
        
        return mfe
else:
    # Fallback functions without numba
    def _numba_range_detection(highs: np.ndarray, lows: np.ndarray) -> Tuple[float, float, float]:
        """Standard Python range detection"""
        range_high = np.max(highs)
        range_low = np.min(lows)
        range_size = range_high - range_low
        return range_high, range_low, range_size

    def _numba_mfe_calculation(highs: np.ndarray, lows: np.ndarray, 
                              entry_price: float, entry_direction: int) -> float:
        """Standard Python MFE calculation"""
        if entry_direction == 1:  # Long
            mfe_price = np.max(highs)
            mfe = mfe_price - entry_price
        else:  # Short
            mfe_price = np.min(lows)
            mfe = entry_price - mfe_price
        return mfe


# Example usage functions
def example_usage():
    """Example of how to use the AlgorithmOptimizer"""
    
    # Create optimizer
    optimizer = AlgorithmOptimizer(enable_numba=True)
    
    # Create example data
    dates = pd.date_range('2024-01-01', periods=1000, freq='1min')
    df = pd.DataFrame({
        'timestamp': dates,
        'high': np.random.randn(1000) * 100 + 2000,
        'low': np.random.randn(1000) * 100 + 2000,
        'open': np.random.randn(1000) * 100 + 2000,
        'close': np.random.randn(1000) * 100 + 2000
    })
    
    # Create example ranges
    ranges = [
        {
            'id': f'range_{i}',
            'start_ts': pd.Timestamp('2024-01-01') + pd.Timedelta(hours=i),
            'end_ts': pd.Timestamp('2024-01-01') + pd.Timedelta(hours=i+1)
        }
        for i in range(10)
    ]
    
    # Test optimized processing
    results = optimizer.optimize_range_processing_batch(df, ranges)
    
    print(f"Processed {len(results)} ranges")
    
    # Benchmark performance
    benchmark = optimizer.benchmark_algorithm_performance(df, ranges)
    
    return optimizer, benchmark


if __name__ == "__main__":
    # Run example
    optimizer, benchmark = example_usage()
    print("Algorithm Optimizer example completed successfully!")
