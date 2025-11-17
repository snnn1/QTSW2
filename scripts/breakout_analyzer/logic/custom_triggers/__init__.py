"""
Custom Configuration Module
Handles custom T1 trigger threshold and removal setting for base level
"""

from typing import Optional
from pydantic import BaseModel, Field

class CustomTriggerConfig(BaseModel):
    """Configuration for custom T1 trigger settings"""
    
    # Custom T1 trigger threshold (0-100%)
    t1_trigger_threshold: float = Field(
        default=65.0,
        description="T1 trigger percentage"
    )
    
    # Remove trigger flag
    remove_t1: bool = Field(
        default=False,
        description="Remove T1 trigger flag"
    )
    
    def get_t1_threshold(self, target_pts: float) -> float:
        """Get T1 threshold"""
        if self.remove_t1:
            return target_pts * 1.0  # 100% of target (effectively removes T1)
        
        percentage = self.t1_trigger_threshold / 100.0
        return target_pts * percentage
    
    def is_t1_removed(self) -> bool:
        """Check if T1 is removed"""
        return self.remove_t1
    
    def get_trigger_threshold(self, target_pts: float) -> float:
        """Get T1 threshold"""
        return self.get_t1_threshold(target_pts)

class CustomTriggerManager:
    """Manager for custom configurations"""
    
    def __init__(self):
        self.config = CustomTriggerConfig()
    
    def update_t1_threshold(self, percentage: float):
        """Update T1 threshold"""
        self.config.t1_trigger_threshold = percentage
    
    def toggle_t1_removal(self):
        """Toggle T1 removal"""
        self.config.remove_t1 = not self.config.remove_t1
    
    def reset_to_defaults(self):
        """Reset all settings to default values"""
        self.config = CustomTriggerConfig()
    
    def get_summary(self) -> dict:
        """Get a summary of current configuration"""
        return {
            "t1_threshold": self.config.t1_trigger_threshold,
            "t1_removed": self.config.remove_t1
        }
