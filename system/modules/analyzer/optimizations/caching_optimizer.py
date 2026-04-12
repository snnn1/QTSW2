"""
Caching and Data Loading Module

This module provides intelligent data loading and caching capabilities for the breakout analyzer
to optimize data access patterns and reduce I/O overhead.

Key features:
1. Parquet Metadata Reading: Read file metadata before loading full data
2. Smart Year Filtering: Skip irrelevant files based on metadata
3. Data Caching: Cache frequently accessed data in memory
4. Lazy Loading: Load data only when needed
"""

import pandas as pd
import numpy as np
from typing import List, Dict, Optional, Tuple, Union, Any
from dataclasses import dataclass
import time
import os
import pickle
import hashlib
from pathlib import Path
import pyarrow.parquet as pq
import pyarrow as pa
from functools import lru_cache
import threading
from concurrent.futures import ThreadPoolExecutor


@dataclass
class CacheStats:
    """Statistics for caching performance"""
    cache_hits: int
    cache_misses: int
    cache_size_mb: float
    load_time_saved: float
    metadata_reads: int
    files_skipped: int


@dataclass
class FileMetadata:
    """Metadata for a parquet file"""
    file_path: str
    file_size_mb: float
    row_count: int
    columns: List[str]
    min_timestamp: Optional[pd.Timestamp]
    max_timestamp: Optional[pd.Timestamp]
    years_available: List[int]
    instruments: List[str]
    last_modified: float


class CachingOptimizer:
    """Intelligent data loading and caching optimizer"""
    
    def __init__(self, cache_size_limit_mb: int = 1000, enable_caching: bool = True):
        """
        Initialize the caching optimizer
        
        Args:
            cache_size_limit_mb: Maximum cache size in MB
            enable_caching: Whether to enable caching
        """
        self.enable_caching = enable_caching
        self.cache_size_limit_mb = cache_size_limit_mb
        self.cache = {}
        self.cache_lock = threading.Lock()
        self.stats = CacheStats(0, 0, 0, 0, 0, 0)
        
        # Caching optimizer initialized
    
    def read_parquet_metadata(self, file_path: Union[str, Path]) -> FileMetadata:
        """
        Read metadata from a parquet file without loading the full data
        
        Args:
            file_path: Path to the parquet file
            
        Returns:
            FileMetadata object with file information
        """
        file_path = Path(file_path)
        
        if not file_path.exists():
            raise FileNotFoundError(f"File not found: {file_path}")
        
        try:
            # Read parquet metadata
            parquet_file = pq.ParquetFile(file_path)
            schema = parquet_file.schema
            metadata = parquet_file.metadata
            
            # Get basic file info
            file_size_mb = file_path.stat().st_size / (1024 * 1024)
            row_count = metadata.num_rows
            columns = [field.name for field in schema]
            last_modified = file_path.stat().st_mtime
            
            # Try to read timestamp range from metadata
            min_timestamp = None
            max_timestamp = None
            years_available = []
            instruments = []
            
            # Read a small sample to get timestamp and instrument info
            sample_size = min(1000, row_count)
            if sample_size > 0:
                sample_df = parquet_file.read_row_groups([0]).to_pandas()
                
                if 'timestamp' in sample_df.columns:
                    min_timestamp = sample_df['timestamp'].min()
                    max_timestamp = sample_df['timestamp'].max()
                    
                    # Get years from sample
                    if pd.notna(min_timestamp) and pd.notna(max_timestamp):
                        years_available = list(range(
                            pd.to_datetime(min_timestamp).year,
                            pd.to_datetime(max_timestamp).year + 1
                        ))
                
                if 'instrument' in sample_df.columns:
                    instruments = sample_df['instrument'].unique().tolist()
            
            metadata_obj = FileMetadata(
                file_path=str(file_path),
                file_size_mb=file_size_mb,
                row_count=row_count,
                columns=columns,
                min_timestamp=min_timestamp,
                max_timestamp=max_timestamp,
                years_available=years_available,
                instruments=instruments,
                last_modified=last_modified
            )
            
            self.stats.metadata_reads += 1
            
            return metadata_obj
            
        except Exception as e:
            print(f"[WARNING] Error reading metadata from {file_path}: {e}")
            # Return basic metadata
            return FileMetadata(
                file_path=str(file_path),
                file_size_mb=file_path.stat().st_size / (1024 * 1024),
                row_count=0,
                columns=[],
                min_timestamp=None,
                max_timestamp=None,
                years_available=[],
                instruments=[],
                last_modified=file_path.stat().st_mtime
            )
    
    def smart_year_filtering(self, file_paths: List[Union[str, Path]], 
                           target_years: List[int]) -> List[Path]:
        """
        Filter files based on year metadata to skip irrelevant files
        
        Args:
            file_paths: List of file paths to check
            target_years: List of target years to filter for
            
        Returns:
            List of relevant file paths
        """
        if not target_years:
            return [Path(p) for p in file_paths]
        
        relevant_files = []
        
        print(f"🔍 Smart year filtering for years: {target_years}")
        
        for file_path in file_paths:
            file_path = Path(file_path)
            
            try:
                # Read metadata
                metadata = self.read_parquet_metadata(file_path)
                
                # Check if file contains any target years
                file_years = set(metadata.years_available)
                target_years_set = set(target_years)
                
                if file_years.intersection(target_years_set):
                    relevant_files.append(file_path)
                    print(f"[OK] {file_path.name}: Contains years {sorted(file_years.intersection(target_years_set))}")
                else:
                    print(f"[SKIP] {file_path.name}: Skipped (years: {sorted(file_years)})")
                    self.stats.files_skipped += 1
                    
            except Exception as e:
                print(f"[WARNING] Error processing {file_path}: {e}")
                # Include file if metadata reading fails
                relevant_files.append(file_path)
        
        print(f"[FILTER] Smart filtering: {len(relevant_files)}/{len(file_paths)} files relevant")
        return relevant_files
    
    def _generate_cache_key(self, file_path: Union[str, Path], 
                           filters: Optional[Dict] = None) -> str:
        """
        Generate a cache key for a file and filters
        
        Args:
            file_path: Path to the file
            filters: Optional filters applied
            
        Returns:
            Cache key string
        """
        file_path = Path(file_path)
        
        # Include file path, size, and modification time
        file_stat = file_path.stat()
        key_data = f"{file_path}_{file_stat.st_size}_{file_stat.st_mtime}"
        
        # Include filters if provided
        if filters:
            key_data += f"_{hash(str(sorted(filters.items())))}"
        
        # Generate hash
        return hashlib.md5(key_data.encode()).hexdigest()
    
    def load_data_with_cache(self, file_path: Union[str, Path], 
                           filters: Optional[Dict] = None,
                           force_reload: bool = False) -> pd.DataFrame:
        """
        Load data with caching support and smart year filtering
        
        Args:
            file_path: Path to the parquet file
            filters: Optional filters to apply (including year filtering)
            force_reload: Whether to force reload from disk
            
        Returns:
            Loaded DataFrame
        """
        file_path = Path(file_path)
        cache_key = self._generate_cache_key(file_path, filters)
        
        if not self.enable_caching:
            return self._load_data_direct(file_path, filters)
        
        # Check cache first
        with self.cache_lock:
            if not force_reload and cache_key in self.cache:
                self.stats.cache_hits += 1
                print(f"[CACHE] Cache hit for {file_path.name}")
                return self.cache[cache_key].copy()
        
        # Load from disk with smart filtering
        start_time = time.time()
        df = self._load_data_with_smart_filtering(file_path, filters)
        load_time = time.time() - start_time
        
        # Cache the result
        with self.cache_lock:
            self.stats.cache_misses += 1
            self.stats.load_time_saved += load_time
            
            # Check cache size limit
            if self._get_cache_size_mb() > self.cache_size_limit_mb:
                self._evict_oldest_cache_entry()
            
            self.cache[cache_key] = df.copy()
            self.stats.cache_size_mb = self._get_cache_size_mb()
        
        print(f"💾 Loaded and cached {file_path.name} ({load_time:.2f}s)")
        return df
    
    def _load_data_with_smart_filtering(self, file_path: Path, filters: Optional[Dict] = None) -> pd.DataFrame:
        """
        Load data with smart year filtering to skip irrelevant data
        
        Args:
            file_path: Path to the parquet file
            filters: Optional filters to apply
            
        Returns:
            Loaded DataFrame
        """
        # For now, use the fast direct loading approach
        # The smart filtering will be implemented as a fallback optimization
        return self._load_data_direct(file_path, filters)
    
    def _load_data_direct(self, file_path: Path, filters: Optional[Dict] = None) -> pd.DataFrame:
        """
        Load data directly from disk
        
        Args:
            file_path: Path to the parquet file
            filters: Optional filters to apply
            
        Returns:
            Loaded DataFrame
        """
        df = pd.read_parquet(file_path)
        
        # Apply filters if provided
        if filters:
            for column, value in filters.items():
                if column in df.columns:
                    if isinstance(value, list):
                        df = df[df[column].isin(value)]
                    else:
                        df = df[df[column] == value]
        
        return df
    
    def _get_cache_size_mb(self) -> float:
        """
        Calculate current cache size in MB
        
        Returns:
            Cache size in MB
        """
        total_size = 0
        for cached_df in self.cache.values():
            total_size += cached_df.memory_usage(deep=True).sum()
        return total_size / (1024 * 1024)
    
    def _evict_oldest_cache_entry(self):
        """
        Evict the oldest cache entry to make room
        """
        if not self.cache:
            return
        
        # Simple FIFO eviction (remove first item)
        oldest_key = next(iter(self.cache))
        del self.cache[oldest_key]
        print(f"[CACHE] Evicted cache entry: {oldest_key[:8]}...")
    
    def lazy_load_data(self, file_path: Union[str, Path], 
                      filters: Optional[Dict] = None) -> 'LazyDataLoader':
        """
        Create a lazy data loader that loads data only when needed
        
        Args:
            file_path: Path to the parquet file
            filters: Optional filters to apply
            
        Returns:
            LazyDataLoader object
        """
        return LazyDataLoader(self, file_path, filters)
    
    def preload_metadata(self, file_paths: List[Union[str, Path]]) -> Dict[str, FileMetadata]:
        """
        Preload metadata for multiple files in parallel
        
        Args:
            file_paths: List of file paths
            
        Returns:
            Dictionary mapping file paths to metadata
        """
        print(f"[PRELOAD] Preloading metadata for {len(file_paths)} files...")
        
        metadata_dict = {}
        
        # Use ThreadPoolExecutor for parallel metadata reading
        with ThreadPoolExecutor(max_workers=min(4, len(file_paths))) as executor:
            future_to_path = {
                executor.submit(self.read_parquet_metadata, path): path
                for path in file_paths
            }
            
            for future in future_to_path:
                path = future_to_path[future]
                try:
                    metadata = future.result()
                    metadata_dict[str(path)] = metadata
                except Exception as e:
                    print(f"[WARNING] Error reading metadata for {path}: {e}")
        
        print(f"[SUCCESS] Metadata preloaded for {len(metadata_dict)} files")
        return metadata_dict
    
    def get_cache_stats(self) -> CacheStats:
        """
        Get caching statistics
        
        Returns:
            CacheStats object
        """
        return self.stats
    
    def clear_cache(self):
        """Clear the cache"""
        with self.cache_lock:
            self.cache.clear()
            self.stats = CacheStats(0, 0, 0, 0, 0, 0)
        print("[CACHE] Cache cleared")
    
    def get_cache_info(self) -> Dict[str, Any]:
        """
        Get detailed cache information
        
        Returns:
            Dictionary with cache information
        """
        with self.cache_lock:
            return {
                'cache_size_mb': self._get_cache_size_mb(),
                'cache_entries': len(self.cache),
                'cache_hits': self.stats.cache_hits,
                'cache_misses': self.stats.cache_misses,
                'hit_rate': self.stats.cache_hits / (self.stats.cache_hits + self.stats.cache_misses) if (self.stats.cache_hits + self.stats.cache_misses) > 0 else 0,
                'files_skipped': self.stats.files_skipped,
                'metadata_reads': self.stats.metadata_reads
            }


class LazyDataLoader:
    """Lazy data loader that loads data only when accessed"""
    
    def __init__(self, cache_optimizer: CachingOptimizer, 
                 file_path: Union[str, Path], filters: Optional[Dict] = None):
        """
        Initialize lazy data loader
        
        Args:
            cache_optimizer: Caching optimizer instance
            file_path: Path to the parquet file
            filters: Optional filters to apply
        """
        self.cache_optimizer = cache_optimizer
        self.file_path = Path(file_path)
        self.filters = filters
        self._data = None
        self._loaded = False
    
    def load(self) -> pd.DataFrame:
        """
        Load the data (lazy loading)
        
        Returns:
            Loaded DataFrame
        """
        if not self._loaded:
            self._data = self.cache_optimizer.load_data_with_cache(self.file_path, self.filters)
            self._loaded = True
        return self._data
    
    def __getattr__(self, name):
        """Delegate attribute access to the loaded DataFrame"""
        if not self._loaded:
            self.load()
        return getattr(self._data, name)
    
    def __getitem__(self, key):
        """Delegate item access to the loaded DataFrame"""
        if not self._loaded:
            self.load()
        return self._data[key]
    
    def __len__(self):
        """Return length of the DataFrame"""
        if not self._loaded:
            self.load()
        return len(self._data)
    
    def __repr__(self):
        """String representation"""
        if not self._loaded:
            return f"LazyDataLoader({self.file_path.name}, not loaded)"
        return repr(self._data)


# Example usage functions
def example_usage():
    """Example of how to use the CachingOptimizer"""
    
    # Create optimizer
    optimizer = CachingOptimizer(cache_size_limit_mb=500, enable_caching=True)
    
    # Example file paths (replace with actual paths)
    file_paths = [
        "data_processed/NQ_2006-2025.parquet",
        "data_processed/ES_2006-2025.parquet",
        "data_processed/GC_2008-2025.parquet"
    ]
    
    # Preload metadata
    metadata_dict = optimizer.preload_metadata(file_paths)
    
    # Smart year filtering
    target_years = [2024, 2025]
    relevant_files = optimizer.smart_year_filtering(file_paths, target_years)
    
    # Load data with caching
    for file_path in relevant_files:
        if Path(file_path).exists():
            df = optimizer.load_data_with_cache(file_path)
            print(f"Loaded {file_path}: {len(df)} rows")
    
    # Get cache stats
    cache_info = optimizer.get_cache_info()
    print(f"Cache info: {cache_info}")
    
    return optimizer


if __name__ == "__main__":
    # Run example
    optimizer = example_usage()
    print("Caching Optimizer example completed successfully!")
