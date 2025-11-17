"""
Custom Configuration Integration Module
Integrates custom configuration with existing price tracking logic
"""

from typing import List, Tuple, Optional
from . import CustomTriggerConfig

def integrate_custom_triggers_with_price_tracking():
    """Integration functions for custom triggers with price tracking logic"""
    
    def get_custom_trigger_threshold(
        target_pts: float, 
        custom_config: Optional[CustomTriggerConfig] = None,
        fallback_t1: float = None
    ) -> float:
        """
        Get T1 trigger threshold using custom configuration or fallback value
        
        Args:
            target_pts: Target points for the trade
            custom_config: Custom trigger configuration
            fallback_t1: Fallback T1 threshold if no custom config
            
        Returns:
            T1 threshold
        """
        if custom_config is not None:
            return custom_config.get_trigger_threshold(target_pts)
        else:
            # Use fallback value
            t1_threshold = fallback_t1 if fallback_t1 is not None else target_pts * 0.65
            return t1_threshold
    
    def should_skip_t1_check(
        custom_config: Optional[CustomTriggerConfig] = None
    ) -> bool:
        """Check if T1 trigger should be skipped"""
        if custom_config is not None:
            return custom_config.is_t1_removed()
        return False
    
    def get_custom_classification_logic(
        t1_triggered: bool,
        custom_config: Optional[CustomTriggerConfig] = None,
        target_hit: bool = False
    ) -> str:
        """
        Get custom classification logic based on trigger state and custom configuration
        
        Args:
            t1_triggered: Whether T1 was triggered
            custom_config: Custom trigger configuration
            target_hit: Whether target was hit
            
        Returns:
            Classification string ("Win", "BE", "Loss")
        """
        if target_hit:
            return "Win"
        
        # Check if T1 was removed
        t1_removed = custom_config.is_t1_removed() if custom_config else False
        
        # If T1 was triggered (and not removed), it's BE
        if t1_triggered and not t1_removed:
            return "BE"
        
        # If no triggers were hit, it's a loss
        return "Loss"
    
    return {
        "get_custom_trigger_threshold": get_custom_trigger_threshold,
        "should_skip_t1_check": should_skip_t1_check,
        "get_custom_classification_logic": get_custom_classification_logic
    }

# Create the integration functions
integration_functions = integrate_custom_triggers_with_price_tracking()

# Export the functions
get_custom_trigger_threshold = integration_functions["get_custom_trigger_threshold"]
should_skip_t1_check = integration_functions["should_skip_t1_check"]
get_custom_classification_logic = integration_functions["get_custom_classification_logic"]
