"""
Custom Trigger UI Component
Streamlit interface for custom T1 trigger configuration
"""

import streamlit as st
from typing import Optional
from . import CustomTriggerConfig, CustomTriggerManager

def render_custom_trigger_table(custom_config: CustomTriggerConfig) -> CustomTriggerConfig:
    """Render the custom trigger configuration table"""
    
    st.subheader("🎯 Custom Configuration")
    st.write("Configure T1 trigger threshold and removal setting")
    
    st.write("**T1 Trigger Threshold (% of target)**")
    
    # Single T1 threshold
    threshold = st.slider(
        "T1 Threshold",
        min_value=0.0,
        max_value=100.0,
        value=float(custom_config.t1_trigger_threshold),
        key="t1_threshold",
        help="T1 trigger threshold percentage"
    )
    
    removed = st.checkbox(
        "Remove T1",
        value=custom_config.remove_t1,
        key="t1_remove",
        help="Remove T1 trigger"
    )
    
    # Create updated config
    updated_config = CustomTriggerConfig(
        t1_trigger_threshold=threshold,
        remove_t1=removed
    )
    
    # Add reset button
    if st.button("🔄 Reset to Defaults", help="Reset trigger settings to default values"):
        updated_config = CustomTriggerConfig()
        st.rerun()
    
    # Show current configuration summary
    with st.expander("📊 Current Configuration Summary"):
        st.write(f"**T1 Threshold:** {updated_config.t1_trigger_threshold}%")
        st.write(f"**T1 Removed:** {updated_config.remove_t1}")
    
    return updated_config

def render_compact_trigger_table(custom_config: CustomTriggerConfig, instrument: str = "ES", use_point_values: bool = False, selected_levels: Optional[list] = None) -> CustomTriggerConfig:
    """Render a compact trigger table with number input fields for precise typing"""
    
    st.write("**🎯 Custom Triggers**")
    
    if use_point_values:
        st.caption(f"💡 Type exact point values for {instrument} (e.g., 7.5, 12.0, 15.0)")
    else:
        st.caption("💡 Type exact percentage values (0-100) with decimals for precise trigger thresholds")
    
    # Get base targets for the instrument
    base_targets = {
        "ES": 10.0, "NQ": 50.0, "YM": 100.0, "GC": 5.0, "CL": 0.5, "NG": 0.05,
        "MES": 10.0, "MNQ": 50.0, "MYM": 100.0, "MCL": 0.5, "MNG": 0.05, "MGC": 5.0
    }
    base_target = base_targets.get(instrument.upper(), 10.0)
    
    # T1 section - single input
    st.write("**T1 Threshold:**")
    
    if use_point_values:
        # Convert percentage to point value for display
        current_percentage = custom_config.t1_trigger_threshold
        current_point_value = (current_percentage / 100.0) * base_target
        
        # Get instrument tick size for proper decimal precision
        from breakout_core.config import TICK_SIZE
        tick_size = TICK_SIZE.get(instrument.upper(), 0.25)  # Default to ES tick size
        
        # Determine decimal places based on tick size
        if tick_size >= 1.0:
            decimal_places = 0
            step_size = 1.0
            format_str = "%.0f"
        elif tick_size >= 0.1:
            decimal_places = 1
            step_size = 0.1
            format_str = "%.1f"
        elif tick_size >= 0.01:
            decimal_places = 2
            step_size = 0.01
            format_str = "%.2f"
        else:
            decimal_places = 3
            step_size = 0.001
            format_str = "%.3f"
        
        # Number input for point values with instrument-appropriate precision
        typed_point_value = st.number_input(
            "T1 Threshold (points)",
            min_value=0.0,
            max_value=base_target * 2.0,  # Allow up to 200% of target
            value=round(current_point_value, decimal_places),
            key="compact_t1_point",
            step=step_size,
            format=format_str
        )
        
        # Convert point value back to percentage
        typed_threshold = (typed_point_value / base_target) * 100.0
    else:
        # Number input for percentage values
        typed_threshold = st.number_input(
            "T1 Threshold (%)",
            min_value=0.0,
            max_value=100.0,
            value=float(custom_config.t1_trigger_threshold),
            key="compact_t1_percent",
            step=0.1,
            format="%.1f"
        )
    
    removed = st.checkbox(
        "Remove T1",
        value=custom_config.remove_t1,
        key="compact_t1_remove"
    )
    
    # Create updated config
    updated_config = CustomTriggerConfig(
        t1_trigger_threshold=float(typed_threshold),
        remove_t1=removed
    )
    
    return updated_config
