"""
Utility Logic Module
Handles mathematical and formatting utility functions
"""

import numpy as np
import pandas as pd
from typing import Union

class UtilityManager:
    """Handles utility functions for mathematical operations and formatting"""
    
    def __init__(self):
        """Initialize utility manager"""
        pass
    
    def round_to_tick(self, value: float, tick_size: float) -> float:
        """
        Round a price to the nearest tick
        
        Args:
            value: Price value to round
            tick_size: Tick size for the instrument
            
        Returns:
            Price rounded to nearest tick
        """
        return np.round(value / tick_size) * tick_size
    
    def hhmm_to_sort_int(self, hhmm: str) -> int:
        """
        Convert HH:MM time string to integer for sorting
        
        Args:
            hhmm: Time string in HH:MM format
            
        Returns:
            Integer representation (HHMM)
        """
        h, m = map(int, hhmm.split(":"))
        return h * 100 + m
    
    def sort_int_to_hhmm(self, sort_int: int) -> str:
        """
        Convert integer sort value back to HH:MM format
        
        Args:
            sort_int: Integer representation (HHMM)
            
        Returns:
            Time string in HH:MM format
        """
        hours = sort_int // 100
        minutes = sort_int % 100
        return f"{hours:02d}:{minutes:02d}"
    
    def calculate_percentage(self, part: float, whole: float) -> float:
        """
        Calculate percentage
        
        Args:
            part: Part value
            whole: Whole value
            
        Returns:
            Percentage (0-100)
        """
        if whole == 0:
            return 0.0
        return (part / whole) * 100.0
    
    def calculate_ratio(self, value1: float, value2: float) -> float:
        """
        Calculate ratio between two values
        
        Args:
            value1: First value
            value2: Second value
            
        Returns:
            Ratio (value1 / value2)
        """
        if value2 == 0:
            return 0.0
        return value1 / value2
    
    def format_price(self, price: float, decimals: int = 2) -> str:
        """
        Format price to specified decimal places
        
        Args:
            price: Price to format
            decimals: Number of decimal places
            
        Returns:
            Formatted price string
        """
        return f"{price:.{decimals}f}"
    
    def format_percentage(self, percentage: float, decimals: int = 1) -> str:
        """
        Format percentage to specified decimal places
        
        Args:
            percentage: Percentage to format
            decimals: Number of decimal places
            
        Returns:
            Formatted percentage string
        """
        return f"{percentage:.{decimals}f}%"
    
    def safe_divide(self, numerator: float, denominator: float, default: float = 0.0) -> float:
        """
        Safe division that handles division by zero
        
        Args:
            numerator: Numerator value
            denominator: Denominator value
            default: Default value if denominator is zero
            
        Returns:
            Division result or default value
        """
        if denominator == 0:
            return default
        return numerator / denominator
    
    def clamp(self, value: float, min_val: float, max_val: float) -> float:
        """
        Clamp value between min and max
        
        Args:
            value: Value to clamp
            min_val: Minimum allowed value
            max_val: Maximum allowed value
            
        Returns:
            Clamped value
        """
        return max(min_val, min(value, max_val))
    
    def is_within_range(self, value: float, min_val: float, max_val: float) -> bool:
        """
        Check if value is within range
        
        Args:
            value: Value to check
            min_val: Minimum allowed value
            max_val: Maximum allowed value
            
        Returns:
            True if value is within range
        """
        return min_val <= value <= max_val
    
    def calculate_distance(self, price1: float, price2: float) -> float:
        """
        Calculate absolute distance between two prices
        
        Args:
            price1: First price
            price2: Second price
            
        Returns:
            Absolute distance
        """
        return abs(price1 - price2)
    
    def calculate_direction_multiplier(self, direction: str) -> int:
        """
        Get multiplier for trade direction
        
        Args:
            direction: Trade direction ("Long" or "Short")
            
        Returns:
            1 for Long, -1 for Short
        """
        return 1 if direction == "Long" else -1
