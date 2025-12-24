"""
Research Runner - Thin wrapper around run_strategy()

This module provides a simple wrapper that calls run_strategy().
All analyzer semantics live in run_strategy() - this is just for convenience.
"""

import pandas as pd
from typing import Optional
import sys
from pathlib import Path

# Add breakout_analyzer root to PYTHONPATH
ROOT = Path(__file__).resolve().parents[1]
sys.path.append(str(ROOT))

from breakout_core.engine import run_strategy
from logic.config_logic import RunParams


class OptimizedBreakoutEngine:
    """
    Thin wrapper around run_strategy().
    
    All analyzer semantics live in run_strategy() in breakout_core/engine.py.
    This class exists only for backward compatibility with existing code.
    """
    
    def __init__(self, enable_optimizations: bool = True, enable_parallel: bool = True, 
                 enable_caching: bool = True, enable_algorithms: bool = True):
        """
        Initialize wrapper (parameters ignored - kept for backward compatibility).
        
        All logic is in run_strategy() - this is just a pass-through.
        """
        pass
    
    def run_optimized_strategy(self, df: pd.DataFrame, rp: RunParams, 
                             debug: bool = False, **kwargs) -> pd.DataFrame:
        """
        Call run_strategy() - all analyzer semantics live there.
        
        Args:
            df: Market data DataFrame
            rp: Run parameters
            debug: Enable debug output
            **kwargs: Additional arguments for run_strategy
            
        Returns:
            Results DataFrame from run_strategy()
        """
        return run_strategy(df, rp, debug=debug, **kwargs)
