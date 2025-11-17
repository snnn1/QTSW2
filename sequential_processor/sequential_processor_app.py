#!/usr/bin/env python3
"""
Streamlit App for Sequential Target Change and Time Change Processor
"""

import streamlit as st
import pandas as pd
import os
from datetime import datetime
import sys

# Get the script directory and project root
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
PROJECT_ROOT = os.path.dirname(SCRIPT_DIR)
sys.path.append(SCRIPT_DIR)
from sequential_processor import SequentialProcessorV2

# Force clear any cached state that might be causing issues
if 'target_selection_cache' in st.session_state:
    del st.session_state['target_selection_cache']

# Clear time selection cache when switching files to prevent '07:30' errors
if 'previous_data_file' in st.session_state:
    current_file = st.session_state.get('current_data_file', None)
    if current_file != st.session_state['previous_data_file']:
        # File changed - clear results and processor
        if 'results' in st.session_state:
            del st.session_state['results']
        if 'processor' in st.session_state:
            del st.session_state['processor']

def create_auto_sized_dataframe(data, use_container_width=True):
    """Create a dataframe with automatic column sizing based on content"""
    if data.empty:
        return st.dataframe(data, use_container_width=use_container_width, hide_index=True)
    
    # Use Streamlit's built-in auto-sizing without forcing column widths
    # This allows Streamlit to automatically size columns based on content
    return st.dataframe(
        data, 
        use_container_width=False,  # Let columns size themselves
        hide_index=True
    )

def main():
    st.set_page_config(
        page_title="Sequential Processor App",
        page_icon="🎯",
        layout="wide"
    )
    
    st.title("🎯 Sequential Target Change & Time Change Processor")
    st.markdown("---")
    
    # Sidebar for configuration
    st.sidebar.header("⚙️ Configuration")
    
    # Data file selection
    # Try multiple possible paths for the data folder
    possible_paths = [
        os.path.join(PROJECT_ROOT, "data", "analyzer_runs"),  # Absolute from project root
        os.path.join(os.getcwd(), "data", "analyzer_runs"),   # Relative to current working directory
        "data/analyzer_runs",                                  # Simple relative path
    ]
    
    data_folder = None
    for path in possible_paths:
        abs_path = os.path.abspath(path)
        if os.path.exists(abs_path):
            data_folder = abs_path
            break
    
    # Initialize variables
    data_path = None
    selected_file = "Not selected"
    
    # Debug: Show what folder we're looking in
    if st.sidebar.checkbox("Show Debug Info", value=False, key="debug_info_paths"):
        st.sidebar.write(f"**Project Root:** `{PROJECT_ROOT}`")
        st.sidebar.write(f"**Current Working Dir:** `{os.getcwd()}`")
        st.sidebar.write(f"**Looking in:** `{data_folder}`")
        st.sidebar.write(f"**Folder exists:** {os.path.exists(data_folder) if data_folder else False}")
        st.sidebar.write(f"**Tried paths:**")
        for path in possible_paths:
            abs_path = os.path.abspath(path)
            exists = os.path.exists(abs_path)
            st.sidebar.write(f"  - `{abs_path}` {'✅' if exists else '❌'}")
        if data_folder and os.path.exists(data_folder):
            try:
                contents = os.listdir(data_folder)
                st.sidebar.write(f"**Contents:** {contents[:10]}...")  # Show first 10 items
            except:
                st.sidebar.write(f"**Contents:** (error reading)")
    
    if data_folder and os.path.exists(data_folder):
        # Search for parquet files only in the main folder (not subfolders)
        data_files = []
        for file in os.listdir(data_folder):
            if file.endswith('.parquet') and os.path.isfile(os.path.join(data_folder, file)):
                data_files.append(file)
        
        if data_files:
            # Prefer files with multiple time slots (5439trades) over single time slot files (259trades)
            # This fixes the CL time advancement issue
            preferred_index = 0
            for i, file in enumerate(data_files):
                if '5439trades' in file:  # Files with multiple time slots
                    preferred_index = i
                    break
            
            selected_file = st.sidebar.selectbox(
                "Select Data File:",
                data_files,
                index=preferred_index
            )
            # Use full path (selected_file is just the filename)
            data_path = os.path.join(data_folder, selected_file)
            
            # Track file changes to clear cached state
            st.session_state['previous_data_file'] = st.session_state.get('current_data_file', None)
            st.session_state['current_data_file'] = selected_file
        else:
            # No files found - allow manual path entry
            st.sidebar.warning("⚠️ No parquet files found in analyzer_runs folder")
            st.sidebar.info(f"**Searched in:** `{data_folder}`")
            st.sidebar.markdown("**Or enter a custom file path:**")
            custom_path = st.sidebar.text_input(
                "Custom Data File Path:",
                value="",
                help="Enter the full path to a parquet file, or a relative path from the project root"
            )
            
            if custom_path:
                # Handle both absolute and relative paths
                if os.path.isabs(custom_path):
                    data_path = custom_path
                else:
                    # Try relative to project root first, then current working directory
                    if os.path.exists(os.path.join(PROJECT_ROOT, custom_path)):
                        data_path = os.path.join(PROJECT_ROOT, custom_path)
                    elif os.path.exists(custom_path):
                        data_path = os.path.abspath(custom_path)
                    else:
                        data_path = custom_path  # Will be validated when loading
                
                if not os.path.exists(data_path):
                    st.sidebar.error(f"❌ File not found: `{data_path}`")
                    data_path = None
                    selected_file = "Invalid path"
                else:
                    st.sidebar.success(f"✅ Using: `{data_path}`")
                    selected_file = os.path.basename(data_path)
            else:
                data_path = None
                selected_file = "No file selected"
    else:
        # Folder doesn't exist - allow manual path entry
        st.sidebar.warning("⚠️ analyzer_runs folder not found")
        st.sidebar.info(f"**Tried looking in:**\n- `{os.path.join(PROJECT_ROOT, 'data', 'analyzer_runs')}`\n- `{os.path.join(os.getcwd(), 'data', 'analyzer_runs')}`\n- `data/analyzer_runs`")
        st.sidebar.markdown("**Enter a custom file path:**")
        custom_path = st.sidebar.text_input(
            "Custom Data File Path:",
            value="",
            help="Enter the full path to a parquet file, or a relative path from the project root",
            key="custom_path_no_folder"
        )
        
        if custom_path:
            # Handle both absolute and relative paths
            if os.path.isabs(custom_path):
                data_path = custom_path
            else:
                # Try relative to project root first, then current working directory
                if os.path.exists(os.path.join(PROJECT_ROOT, custom_path)):
                    data_path = os.path.join(PROJECT_ROOT, custom_path)
                elif os.path.exists(custom_path):
                    data_path = os.path.abspath(custom_path)
                else:
                    data_path = custom_path  # Will be validated when loading
            
            if not os.path.exists(data_path):
                st.sidebar.error(f"❌ File not found: `{data_path}`")
                data_path = None
                selected_file = "Invalid path"
            else:
                st.sidebar.success(f"✅ Using: `{data_path}`")
                selected_file = os.path.basename(data_path)
        else:
            data_path = None
            selected_file = "No file selected"
    
    # Mode selection
    st.sidebar.subheader("🎛️ Mode Selection")
    enable_target_change = st.sidebar.checkbox("Target Change Mode", value=True, help="Enable dynamic target progression")
    enable_time_change = st.sidebar.checkbox("Time Change Mode", value=True, help="Enable time slot switching")
    
    # Rolling median mode
    enable_rolling_median_mode = st.sidebar.checkbox(
        "Rolling Median Mode", 
        value=False, 
        help="Use rolling median of last 13 peaks for target changes. Target Change column shows median value rounded down to nearest 25. No trade when median < 50."
    )
    
    # Median ladder mode
    enable_median_ladder_mode = st.sidebar.checkbox(
        "Median Ladder Mode", 
        value=False, 
        help="Use rolling median with progressive ladder steps. Calculates median of last 13 peaks and maps to ladder steps (75, 100, 125, etc.). Progressive promotion with configurable days required. No trade when median < base target."
    )
    
    # Median ladder promotion days (show when median ladder mode is enabled)
    median_ladder_promotion_days = 2  # Default value
    if enable_median_ladder_mode:
        median_ladder_promotion_days = st.sidebar.selectbox(
            "Median Ladder Promotion Days",
            options=[1, 2, 3, 4, 5],
            index=1,  # Default to 2 days
            help="Days required at current level before promoting to next step"
        )
    
    # Median position selection (show when any median mode is enabled)
    median_position = 7  # Default value
    if enable_rolling_median_mode or enable_median_ladder_mode:
        median_position = st.sidebar.selectbox(
            "Median Position",
            options=list(range(1, 14)),
            index=6,  # Default to 7 (true median)
            help="Position in sorted 13-peak window: 1=lowest, 7=median, 13=highest"
        )
    
    # No-trade on low median toggle (show when median mode is enabled)
    enable_no_trade_on_low_median = True  # Default value
    if enable_rolling_median_mode or enable_median_ladder_mode:
        enable_no_trade_on_low_median = st.sidebar.checkbox(
            "Enable No-Trade on Low Median",
            value=True,
            help="When enabled, no trades are taken when the median is below the base target. Uncheck to trade even when median is low."
        )
    
    # Max target change percentage (show when any target change mode is enabled)
    if enable_target_change or enable_rolling_median_mode or enable_median_ladder_mode:
        max_target_percentage = st.sidebar.selectbox(
            "Max Target Change (%)",
            options=[50, 100, 150, 200, 250, 300, 400, 500],
            index=6,  # Default to 400%
            help="Maximum percentage of base target for Target Change column. ES base=10, NQ base=50, etc."
        )
    else:
        max_target_percentage = 400  # Default value when neither mode is enabled
    
    # Target sharing option
    st.sidebar.subheader("🎯 Target Configuration")
    share_targets = st.sidebar.checkbox("Share Targets Between Time Slots", value=True, help="When enabled, both time slots use the same target. When disabled, each time slot has independent target progression.")
    
    # Rolling mode - always enabled
    enable_rolling_mode = True
    
    # No consecutive target changes option
    no_consecutive_target_changes = st.sidebar.checkbox(
        "No Consecutive Target Changes", 
        value=False, 
        help="Prevent target changes on consecutive days (e.g., if target changes Monday, it cannot change Tuesday)"
    )
    
    # Day filtering options
    st.sidebar.subheader("📅 Day Filtering")
    
    # Day of week filtering
    st.sidebar.markdown("**Exclude Days of Week:**")
    exclude_days_of_week = st.sidebar.multiselect(
        "Select days to exclude from analysis:",
        ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"],
        default=[],
        help="Selected days will be excluded from Revised Score and Revised Profit calculations"
    )
    
    # Day of month filtering
    st.sidebar.markdown("**Exclude Days of Month:**")
    exclude_days_of_month = st.sidebar.multiselect(
        "Select days of month to exclude:",
        list(range(1, 32)),
        default=[],
        help="Selected days of month will be excluded from Revised Score and Revised Profit calculations"
    )
    
    # Loss recovery mode
    st.sidebar.markdown("**Loss Recovery Mode:**")
    loss_recovery_mode = st.sidebar.checkbox(
        "Loss Recovery Mode", 
        value=False, 
        help="Only count trades after wins, BE, NO TRADE, or TIME results (exclude trades following losses until next non-loss). Helps analyze 'winning streak' performance only."
    )
    
    # Processing options
    st.sidebar.subheader("📊 Processing Options")
    # Process all available data by default
    max_days = 10000  # Effectively unlimited
    
    # Starting configuration - dynamically set based on selected data file
    st.sidebar.subheader("🎯 Starting Configuration")
    
    # Load data to detect instrument and available options
    if data_path and os.path.exists(data_path):
        try:
            sample_data = pd.read_parquet(data_path)
            # Ensure times are strings for proper comparison
            available_times = sorted([str(t) for t in sample_data['Time'].unique()])
            available_targets = sorted(sample_data['Target'].unique().tolist())
            
            # Auto-detect instrument for appropriate defaults
            instrument = "ES"  # Default
            if 'Stream' in sample_data.columns:
                stream_values = sample_data['Stream'].unique()
                for stream in stream_values:
                    if isinstance(stream, str):
                        if stream.startswith('ES'):
                            instrument = 'ES'
                            break
                        elif stream.startswith('NQ'):
                            instrument = 'NQ'
                            break
                        elif stream.startswith('CL'):
                            instrument = 'CL'
                            break
            elif 'Instrument' in sample_data.columns:
                inst_values = sample_data['Instrument'].unique()
                for inst in inst_values:
                    if isinstance(inst, str):
                        inst_upper = inst.upper()
                        if inst_upper in ['ES', 'NQ', 'YM', 'CL', 'NG', 'GC']:
                            instrument = inst_upper
                            break
            
            # Set appropriate default time (use first available)
            default_time = available_times[0] if available_times else "08:00"
            start_time = st.sidebar.selectbox("Start Time", available_times, index=0, help="Time slot to start processing from")
            
            # Set appropriate default target based on instrument
            base_targets = {"ES": 10, "NQ": 50, "YM": 100, "CL": 0.5, "NG": 0.05, "GC": 5}
            default_target = base_targets.get(instrument, 10)
            
            # Find the closest available target to the default
            if available_targets:
                closest_target = min(available_targets, key=lambda x: abs(x - default_target))
                default_index = available_targets.index(closest_target)
            else:
                default_index = 0
                
            # Force index 0 for CL to ensure we get 0.5, not 2.0
            if instrument == "CL":
                forced_index = 0
            else:
                forced_index = default_index
                
            # Use a key that changes when file changes to force selectbox reset
            selectbox_key = f"target_select_{os.path.basename(data_path)}_{instrument}_{forced_index}"
            start_target = st.sidebar.selectbox("Start Target", available_targets, index=forced_index, key=selectbox_key, help="Target level to start processing from")
            
            # Show detected instrument and configuration
            st.sidebar.info(f"🔍 **Detected Instrument: {instrument}**")
            st.sidebar.info(f"🎯 **Start Target: {start_target}**")
            
        except Exception as e:
            st.sidebar.error(f"Error loading data: {e}")
            # Fallback to ES defaults
            start_time = st.sidebar.selectbox("Start Time", ["08:00", "09:00"], index=0)
            start_target = st.sidebar.selectbox("Start Target", [10, 15, 20, 25, 30, 35, 40], index=0)
            instrument = "ES"
            available_times = ["08:00", "09:00"]
            available_targets = [10, 15, 20, 25, 30, 35, 40]
    else:
        # No data file available - use default settings
        st.sidebar.info("📋 **Using default settings (no data file)**")
        st.sidebar.info("🔧 **Select instrument manually below**")
        
        # Let user select instrument
        instrument = st.sidebar.selectbox(
            "Select Instrument:",
            ["ES", "NQ", "YM", "CL", "NG", "GC"],
            index=0,
            help="Choose the trading instrument for default settings"
        )
        
        # Set defaults based on selected instrument
        base_targets = {"ES": 10, "NQ": 50, "YM": 100, "CL": 0.5, "NG": 0.05, "GC": 5}
        available_times = ["08:00", "09:00"]
        available_targets = [10, 15, 20, 25, 30, 35, 40]  # Standard ES targets
        
        start_time = st.sidebar.selectbox("Start Time", available_times, index=0, help="Time slot to start processing from")
        start_target = st.sidebar.selectbox("Start Target", available_targets, index=0, help="Target level to start processing from")
    
    # Show info about processing all data
    st.sidebar.info("🔄 **Processing ALL available data**")
    
    # Display current settings
    st.sidebar.markdown("---")
    st.sidebar.markdown("**Current Settings:**")
    if data_path:
        st.sidebar.markdown(f"• Data File: `{selected_file}`")
        st.sidebar.markdown(f"• Data Path: `{data_path}`")
    else:
        st.sidebar.markdown(f"• Data File: `{selected_file}`")
        st.sidebar.warning("⚠️ Please select a data file to continue")
    st.sidebar.markdown(f"• Instrument: `{instrument}`")
    st.sidebar.markdown(f"• Start Time: `{start_time}`")
    st.sidebar.markdown(f"• Start Target: `{start_target}`")
    st.sidebar.markdown(f"• Available Times: {available_times if 'available_times' in locals() else 'N/A'}")
    st.sidebar.markdown(f"• Available Targets: {available_targets if 'available_targets' in locals() else 'N/A'}")
    st.sidebar.markdown(f"• Target Change: {'✅ ON' if enable_target_change else '❌ OFF'}")
    st.sidebar.markdown(f"• Time Change: {'✅ ON' if enable_time_change else '❌ OFF'}")
    st.sidebar.markdown(f"• Share Targets: {'✅ ON' if share_targets else '❌ OFF'}")
    st.sidebar.markdown(f"• Rolling Mode: **✅ ALWAYS ON**")
    st.sidebar.markdown(f"• Rolling Median Mode: {'✅ ON' if enable_rolling_median_mode else '❌ OFF'}")
    st.sidebar.markdown(f"• Median Ladder Mode: {'✅ ON' if enable_median_ladder_mode else '❌ OFF'}")
    if enable_median_ladder_mode:
        st.sidebar.markdown(f"• Median Ladder Promotion Days: **{median_ladder_promotion_days}**")
    st.sidebar.markdown(f"• No Consecutive Changes: {'✅ ON' if no_consecutive_target_changes else '❌ OFF'}")
    st.sidebar.markdown(f"• Excluded Days of Week: {exclude_days_of_week if exclude_days_of_week else 'None'}")
    st.sidebar.markdown(f"• Excluded Days of Month: {exclude_days_of_month if exclude_days_of_month else 'None'}")
    st.sidebar.markdown(f"• Loss Recovery Mode: {'✅ ON' if loss_recovery_mode else '❌ OFF'}")
    if enable_target_change or enable_rolling_median_mode or enable_median_ladder_mode:
        st.sidebar.markdown(f"• Max Target Change: **{max_target_percentage}%**")
    st.sidebar.markdown(f"• Processing: **ALL DATA**")
    
    # Output folder selection (global for all tabs)
    st.markdown("---")
    st.subheader("📁 Output Folder")
    
    col_folder1, col_folder2 = st.columns([2, 1])
    
    with col_folder1:
        output_folder_options = {
            "Default (data/sequencer_runs/)": "data/sequencer_runs",
            "Custom Folder": "custom"
        }
        
        selected_output_option = st.selectbox(
            "Save results to:",
            list(output_folder_options.keys()),
            help="Choose where to save your sequential processor runs"
        )
        
        custom_folder = None
        if output_folder_options[selected_output_option] == "custom":
            custom_folder = st.text_input(
                "Custom folder path:",
                value="my_sequential_runs",
                help="Enter folder name (will be created if it doesn't exist)"
            )
        
        # Determine final output folder (use absolute path)
        if custom_folder:
            output_folder = os.path.join(PROJECT_ROOT, custom_folder)
        else:
            output_folder = os.path.join(PROJECT_ROOT, output_folder_options[selected_output_option])
    
    with col_folder2:
        st.info(f"📁 **Current folder:**\n`{output_folder}/`")
    
    st.markdown("---")
    
    # Main content area
    col1, col2 = st.columns([1, 2])
    
    with col1:
        st.subheader("🚀 Run Processor")
        
        if st.button("▶️ Start Processing", type="primary"):
            if not data_path or not os.path.exists(data_path):
                st.error("❌ Please select a valid data file first!")
                st.stop()
            
            with st.spinner("Processing..."):
                try:
                    # Validate start_time is compatible with the data before processing
                    validation_data = pd.read_parquet(data_path)
                    # Ensure times are strings for proper comparison
                    validation_times = sorted([str(t) for t in validation_data['Time'].unique()])
                    
                    # Convert start_time to string to ensure compatibility
                    start_time_str = str(start_time)
                    
                    if start_time_str not in validation_times:
                        st.error(f"❌ Start time '{start_time_str}' is not available in the selected data file.")
                        st.info(f"📋 Available time slots: {validation_times}")
                        st.warning("💡 **Solution:** Please select a different data file or change the start time to one of the available slots.")
                        st.stop()
                    
                    # Initialize processor with auto-detected settings
                    processor = SequentialProcessorV2(
                        data_path, 
                        start_time_str, 
                        start_target, 
                        "normal", 
                        share_targets, 
                        enable_rolling_mode, 
                        no_consecutive_target_changes, 
                        exclude_days_of_week, 
                        exclude_days_of_month, 
                        loss_recovery_mode, 
                        enable_rolling_median_mode, 
                        max_target_percentage,
                        enable_median_ladder_mode,
                        median_ladder_promotion_days,
                        median_position,
                        enable_no_trade_on_low_median
                    )
                    
                    # Process data
                    results = processor.process_sequential(
                        max_days=max_days,
                        enable_target_change=enable_target_change,
                        enable_time_change=enable_time_change
                    )
                    
                    # Store results in session state
                    st.session_state['results'] = results
                    st.session_state['processor'] = processor
                    
                    st.success("✅ Processing completed!")
                    
                except Exception as e:
                    st.error(f"❌ Error during processing: {str(e)}")
                    # Show more details for debugging
                    import traceback
                    with st.expander("🔍 Show detailed error information"):
                        st.code(traceback.format_exc())
    
    with col2:
        st.subheader("📈 Quick Stats")
        if 'results' in st.session_state:
            results = st.session_state['results']
            
            # Calculate stats
            total_days = len(results)
            target_changes = len(results[results.get('Target Change', '') != '']) if 'Target Change' in results.columns else 0
            time_changes = len(results[results.get('Time Change', '') != '']) if 'Time Change' in results.columns else 0
            
            # Calculate revised win rate and profit (excluding filtered days) - with safe column access
            if 'Revised Score' in results.columns:
                # Use revised data (excluding filtered days)
                wins = len(results[results['Revised Score'] == 'Win'])
                losses = len(results[results['Revised Score'] == 'Loss'])
                break_even = len(results[results['Revised Score'] == 'BE'])
                # Win rate excludes BE trades (only wins vs losses)
                win_loss_trades = wins + losses
                win_rate = (wins / win_loss_trades * 100) if win_loss_trades > 0 else 0
                # Total profit from non-excluded trades only
                total_profit = results[results['Revised Score'] != '']['Profit'].sum() if 'Profit' in results.columns else 0
            else:
                # Fallback to original data if revised columns not available
                wins = len(results[results.get('Result', '') == 'Win']) if 'Result' in results.columns else 0
                losses = len(results[results.get('Result', '') == 'Loss']) if 'Result' in results.columns else 0
                break_even = len(results[results.get('Result', '') == 'BE']) if 'Result' in results.columns else 0
                # Win rate excludes BE trades (only wins vs losses)
                win_loss_trades = wins + losses
                win_rate = (wins / win_loss_trades * 100) if win_loss_trades > 0 else 0
                total_profit = results['Profit'].sum() if 'Profit' in results.columns else 0
            
            # Calculate total profit in dollars
            total_profit_dollars = 0.0
            if 'Profit' in results.columns and len(results) > 0:
                try:
                    # Get instrument from first row
                    first_row = results.iloc[0]
                    # Get instrument from Stream column (e.g., "NQ1" -> "NQ")
                    stream = first_row.get('Stream', 'ES1')
                    instrument = stream[:2] if len(stream) >= 2 else 'ES'  # Extract first 2 chars
                    contract_values = {"ES": 50, "NQ": 10, "YM": 5, "CL": 1000, "NG": 10000, "GC": 100}  # Correct contract values
                    contract_value = contract_values.get(instrument, 50)
                    total_profit_dollars = total_profit * contract_value
                except:
                    total_profit_dollars = total_profit * 50  # Default to ES
            
            # Calculate Sharpe ratio (proper formula) using revised data
            sharpe_ratio = 0.0
            sortino_ratio = 0.0
            calmar_ratio = 0.0
            profit_factor = 0.0
            if 'Profit' in results.columns and len(results) > 1:
                if 'Revised Score' in results.columns:
                    # Use only non-excluded trades for Sharpe ratio
                    profits = results[results['Revised Score'] != '']['Profit'].dropna()
                else:
                    # Fallback to all profits
                    profits = results['Profit'].dropna()
                
                if len(profits) > 1:
                    # Use profits directly from analyzer (stop loss capping already applied there)
                    # Get instrument from Stream column (e.g., "NQ1" -> "NQ")
                    stream = results.iloc[0].get('Stream', 'ES1')
                    instrument = stream[:2] if len(stream) >= 2 else 'ES'
                    
                    # Convert profits to dollars for proper cross-instrument comparison
                    contract_values = {"ES": 50, "NQ": 10, "YM": 5, "CL": 1000, "NG": 10000, "GC": 100}  # Correct contract values
                    contract_value = contract_values.get(instrument, 50)
                    profits_dollars = profits * contract_value
                    
                    # Annualize the returns (assuming daily data)
                    mean_return = profits_dollars.mean()
                    std_return = profits_dollars.std()
                    
                    # Annualize: multiply by sqrt(252) for daily data
                    annualized_return = mean_return * 252
                    annualized_volatility = std_return * (252 ** 0.5)
                    
                    # Risk-free rate (assume 0% for simplicity, or could use treasury rate)
                    risk_free_rate = 0.0
                    
                    # Sharpe ratio = (Return - Risk-free rate) / Volatility
                    sharpe_ratio = (annualized_return - risk_free_rate) / annualized_volatility if annualized_volatility != 0 else 0
                    
                    # Calculate Sortino ratio (downside deviation only)
                    downside_returns = profits_dollars[profits_dollars < 0]
                    if len(downside_returns) > 1:
                        downside_deviation = downside_returns.std() * (252 ** 0.5)  # Annualized downside deviation
                        sortino_ratio = (annualized_return - risk_free_rate) / downside_deviation if downside_deviation != 0 else 0
                    else:
                        sortino_ratio = 0.0
                    
            
            # Calculate Maximum Drawdown using revised data
            max_drawdown = 0.0
            max_drawdown_dollars = 0.0
            if 'Profit' in results.columns and len(results) > 0:
                if 'Revised Score' in results.columns:
                    # Use only non-excluded trades for max drawdown
                    profits = results[results['Revised Score'] != '']['Profit'].dropna()
                else:
                    # Fallback to all profits
                    profits = results['Profit'].dropna()
                
                if len(profits) > 0:
                    # Calculate cumulative returns
                    cumulative = profits.cumsum()
                    # Calculate running maximum (peak)
                    running_max = cumulative.expanding().max()
                    # Calculate drawdown at each point
                    drawdown = cumulative - running_max
                    # Maximum drawdown is the largest negative value
                    max_drawdown = abs(drawdown.min()) if len(drawdown) > 0 else 0
                    
                    # Convert to dollars
                    if 'processor' in st.session_state:
                        try:
                            # Get instrument from results data
                            if 'results' in st.session_state and len(st.session_state['results']) > 0:
                                # Get instrument from Stream column (e.g., "NQ1" -> "NQ")
                                stream = st.session_state['results'].iloc[0].get('Stream', 'ES1')
                                instrument = stream[:2] if len(stream) >= 2 else 'ES'
                            else:
                                instrument = 'ES'
                            contract_values = {"ES": 50, "NQ": 10, "YM": 5, "CL": 1000, "NG": 10000, "GC": 100}  # Correct contract values
                            contract_value = contract_values.get(instrument, 50)
                            max_drawdown_dollars = max_drawdown * contract_value
                        except:
                            max_drawdown_dollars = max_drawdown * 50  # Default to ES
                    else:
                        max_drawdown_dollars = max_drawdown * 50  # Default to ES
                
                # Calculate Calmar ratio (annualized return / max drawdown)
                if max_drawdown_dollars > 0 and 'Profit' in results.columns:
                    if 'Revised Score' in results.columns:
                        profits_for_calmar = results[results['Revised Score'] != '']['Profit'].dropna()
                    else:
                        profits_for_calmar = results['Profit'].dropna()
                    
                    if len(profits_for_calmar) > 0:
                        # Convert to dollars for proper comparison
                        # Get instrument from Stream column (e.g., "NQ1" -> "NQ")
                        stream = results.iloc[0].get('Stream', 'ES1')
                        instrument = stream[:2] if len(stream) >= 2 else 'ES'
                        contract_values = {"ES": 50, "NQ": 10, "YM": 5, "CL": 1000, "NG": 10000, "GC": 100}  # Correct contract values
                        contract_value = contract_values.get(instrument, 50)
                        profits_for_calmar_dollars = profits_for_calmar * contract_value
                        
                        mean_return = profits_for_calmar_dollars.mean()
                        annualized_return = mean_return * 252
                        calmar_ratio = annualized_return / max_drawdown_dollars
                    else:
                        calmar_ratio = 0.0
                else:
                    calmar_ratio = 0.0
                
                # Calculate Profit Factor using revised data
                if 'Profit' in results.columns and len(results) > 0:
                    if 'Revised Score' in results.columns:
                        # Use only non-excluded trades for profit factor
                        revised_results = results[results['Revised Score'] != '']
                        if len(revised_results) > 0:
                            winning_trades = revised_results[revised_results['Result'] == 'Win']['Profit']
                            losing_trades = revised_results[revised_results['Result'] == 'Loss']['Profit']
                        else:
                            winning_trades = pd.Series(dtype=float)
                            losing_trades = pd.Series(dtype=float)
                    else:
                        # Use all trades
                        winning_trades = results[results['Result'] == 'Win']['Profit']
                        losing_trades = results[results['Result'] == 'Loss']['Profit']
                    
                    total_wins = winning_trades.sum() if len(winning_trades) > 0 else 0
                    total_losses = abs(losing_trades.sum()) if len(losing_trades) > 0 else 0
                    
                    if total_losses > 0:
                        profit_factor = total_wins / total_losses
                    else:
                        profit_factor = float('inf') if total_wins > 0 else 0.0
                else:
                    profit_factor = 0.0
                
                # Calculate Risk-Reward ratio using revised data
                if 'Profit' in results.columns and len(results) > 0:
                    if 'Revised Score' in results.columns:
                        # Use only non-excluded trades for risk-reward
                        revised_results = results[results['Revised Score'] != '']
                        if len(revised_results) > 0:
                            winning_trades = revised_results[revised_results['Result'] == 'Win']['Profit']
                            losing_trades = revised_results[revised_results['Result'] == 'Loss']['Profit']
                        else:
                            winning_trades = pd.Series(dtype=float)
                            losing_trades = pd.Series(dtype=float)
                    else:
                        # Use all trades
                        winning_trades = results[results['Result'] == 'Win']['Profit']
                        losing_trades = results[results['Result'] == 'Loss']['Profit']
                    
                    avg_win = winning_trades.mean() if len(winning_trades) > 0 else 0
                    avg_loss = abs(losing_trades.mean()) if len(losing_trades) > 0 else 0
                    
                    if avg_loss > 0:
                        risk_reward_ratio = avg_win / avg_loss
                    else:
                        risk_reward_ratio = float('inf') if avg_win > 0 else 0.0
                else:
                    risk_reward_ratio = 0.0
            
            # Display stats in a cleaner layout
            col1, col2, col3, col4, col5 = st.columns(5)
            
            with col1:
                st.metric("Total Days", total_days)
                st.metric("Win Rate", f"{win_rate:.0f}%")
                st.metric("Total Profit", f"{total_profit:.0f}")
                st.metric("Total Profit ($)", f"${total_profit_dollars:,.0f}")
            
            with col2:
                st.metric("Wins", wins)
                st.metric("Losses", losses)
                st.metric("Break-Even", break_even)
            
            with col3:
                st.metric("Target Changes", target_changes)
                st.metric("Time Changes", time_changes)
                if total_days > 0:
                    final_row = results.iloc[-1]
                    st.metric("Final Time", final_row.get('Time', 'N/A'))
            
            with col4:
                st.metric("Sharpe Ratio", f"{sharpe_ratio:.2f}")
                st.metric("Sortino Ratio", f"{sortino_ratio:.2f}")
                st.metric("Calmar Ratio", f"{calmar_ratio:.2f}")
            
            with col5:
                st.metric("Profit Factor", f"{profit_factor:.1f}" if profit_factor != float('inf') else "∞")
                st.metric("Risk-Reward", f"{risk_reward_ratio:.2f}" if risk_reward_ratio != float('inf') else "∞")
                st.metric("Max Drawdown", f"{max_drawdown:.0f}")
                st.metric("Max Drawdown ($)", f"${max_drawdown_dollars:,.0f}")
        else:
            st.info("Run processing to see stats")
    
    # Results section
    if 'results' in st.session_state:
        st.markdown("---")
        st.header("📊 Results")
        
        results = st.session_state['results']
        
        # Tabs for different views
        tab1, tab2, tab3, tab4, tab5, tab6, tab7, tab8 = st.tabs(["📋 Full Results", "🎯 Target Changes", "⏰ Time Changes", "📊 Revised Summary", "📈 Summary", "📅 Day of Week Analysis", "📅 Day of Month Pivot", "📅 Yearly Profit"])
        
        with tab1:
            st.subheader("Complete Results")
            
            # Display options
            col1, col2 = st.columns([1, 1])
            with col1:
                # Set default columns based on what's available
                default_cols = []
                base_columns = ['Date', 'Day of Week', 'Stream', 'Time', 'Target', 'Range', 'SL', 'Profit', 'Peak', 'Direction', 'Result', 'Revised Score', 'Target Change', 'Time Change', 'Profit ($)', 'Revised Profit ($)']
                
                # Add Rolling Median to default columns if rolling median mode or median ladder mode is enabled
                if (enable_rolling_median_mode or enable_median_ladder_mode) and 'Rolling Median' in results.columns:
                    # Insert Rolling Median before Target Change
                    target_change_index = base_columns.index('Target Change')
                    base_columns.insert(target_change_index, 'Rolling Median')
                
                for col in base_columns:
                    if col in results.columns:
                        default_cols.append(col)
                
                show_columns = st.multiselect(
                    "Select columns to display:",
                    results.columns.tolist(),
                    default=default_cols
                )
            
            with col2:
                if len(results) > 1:
                    max_rows = st.slider("Max rows to display", 10, len(results), min(50, len(results)))
                elif len(results) == 1:
                    st.info("Only 1 result available - displaying all")
                    max_rows = 1
                else:
                    st.info("No data to display")
                    max_rows = 0
            
            # Display filtered results
            if len(results) > 0 and max_rows > 0:
                display_results = results[show_columns].head(max_rows)
                create_auto_sized_dataframe(display_results)
            elif len(results) == 0:
                st.info("No results to display. Please run the processor first.")
            
            # Monthly Profit Summary
            st.subheader("📊 Monthly Profit Summary")
            
            if len(results) > 0 and 'Date' in results.columns and 'Profit' in results.columns:
                # Convert Date to datetime for proper grouping
                results_copy = results.copy()
                results_copy['Date'] = pd.to_datetime(results_copy['Date'])
                results_copy['Month'] = results_copy['Date'].dt.to_period('M')
                
                # Group by month and calculate profit
                monthly_profit = results_copy.groupby('Month')['Profit'].sum().reset_index()
                monthly_profit['Month'] = monthly_profit['Month'].astype(str)
                
                # Add profit conversion column based on detected instrument
                contract_values = {"ES": 50, "NQ": 10, "YM": 5, "CL": 1000, "NG": 10000, "GC": 100}  # NQ halved to 10
                contract_value = contract_values.get('ES', 50)  # Default to ES value
                monthly_profit['Profit ($)'] = monthly_profit['Profit'] * contract_value
                
                # Display monthly summary
                col1, col2 = st.columns(2)
                with col1:
                            create_auto_sized_dataframe(monthly_profit)
                
                with col2:
                    # Total profit summary
                    total_profit = results['Profit'].sum()
                    total_profit_dollars = total_profit * contract_value
                    
                    st.metric("Total Profit (Points)", f"{total_profit:.2f}")
                    st.metric("Total Profit ($)", f"${total_profit_dollars:,.2f}")
                
                # Download button
                csv = results.to_csv(index=False)
                timestamp = datetime.now().strftime('%Y%m%d_%H%M%S')
                
                st.download_button(
                    label="📥 Download Full Results as CSV",
                    data=csv,
                    file_name=f"sequential_run_{timestamp}.csv",
                    mime="text/csv"
                )
                
                # Save button
                st.markdown("---")
                st.subheader("💾 Save Results")
                
                if st.button("💾 Save Results", type="primary", help="Save current results to selected folder"):
                    # Ensure output folder exists
                    os.makedirs(output_folder, exist_ok=True)
                    
                    # Save the results
                    filename = f"{output_folder}/sequential_run_{timestamp}.csv"
                    results.to_csv(filename, index=False)
                    
                    st.success(f"✅ Results saved to: `{filename}`")
                    st.balloons()
                
                # Show save location info
                st.info(f"💾 Results will be saved to: `{output_folder}/sequential_run_{timestamp}.csv`")
            else:
                st.info("Date and Profit columns required for monthly analysis")
        
        with tab2:
            st.subheader("Target Change Events")
            
            target_changes = results[results.get('Target Change', '') != ''] if 'Target Change' in results.columns else pd.DataFrame()
            if len(target_changes) > 0:
                # Select available columns for target changes
                available_cols = []
                for col in ['Date', 'Target Change', 'Peak', 'Result', 'Target Reason']:
                    if col in target_changes.columns:
                        available_cols.append(col)
                
                st.dataframe(
                    target_changes[available_cols],
                    use_container_width=True
                )
                
                # Target change chart - with safe column access
                st.subheader("Target Progression")
                if 'Date' in results.columns and 'Target' in results.columns:
                    target_progression = results[['Date', 'Target']].copy()
                    st.line_chart(target_progression.set_index('Date'))
                else:
                    st.info("Date and Target columns not available for target progression chart")
            else:
                st.info("No target changes occurred")
        
        with tab3:
            st.subheader("Time Change Events")
            
            time_changes = results[results.get('Time Change', '') != ''] if 'Time Change' in results.columns else pd.DataFrame()
            if len(time_changes) > 0:
                # Select available columns for time changes
                available_cols = []
                for col in ['Date', 'Time Change', 'Result', 'Time Reason']:
                    if col in time_changes.columns:
                        available_cols.append(col)
                
                st.dataframe(
                    time_changes[available_cols],
                    use_container_width=True
                )
            else:
                st.info("No time changes occurred")
        
        with tab4:
            st.subheader("📊 Revised Summary (Excluding Filtered Days)")
            
            if len(results) > 0 and 'Revised Score' in results.columns and 'Revised Profit ($)' in results.columns:
                # Calculate revised statistics
                revised_total_profit_dollars = results['Revised Profit ($)'].sum()
                revised_trade_count = len(results[results['Revised Score'] != ''])  # Count non-empty revised results
                
                # Calculate win rate from revised data
                revised_wins = len(results[results['Revised Score'] == 'Win'])
                revised_losses = len(results[results['Revised Score'] == 'Loss'])
                revised_win_rate = (revised_wins / (revised_wins + revised_losses) * 100) if (revised_wins + revised_losses) > 0 else 0
                
                # Calculate revised total profit from non-excluded trades
                revised_total_profit = results[results['Revised Score'] != '']['Profit'].sum()
                
                # Show excluded days info
                excluded_days_info = []
                if exclude_days_of_week:
                    excluded_days_info.append(f"Days of Week: {', '.join(exclude_days_of_week)}")
                if exclude_days_of_month:
                    excluded_days_info.append(f"Days of Month: {', '.join(map(str, exclude_days_of_month))}")
                
                if excluded_days_info:
                    st.info(f"**Excluded from analysis:** {' | '.join(excluded_days_info)}")
                else:
                    st.info("**No days excluded** - Revised summary matches original summary")
                
                # Display revised metrics
                col1, col2, col3, col4 = st.columns(4)
                
                with col1:
                    st.metric("Revised Total Profit (Points)", f"{revised_total_profit:.2f}")
                    st.metric("Revised Total Profit ($)", f"${revised_total_profit_dollars:,.2f}")
                
                with col2:
                    st.metric("Revised Trade Count", revised_trade_count)
                    st.metric("Revised Win Rate", f"{revised_win_rate:.1f}%")
                
                with col3:
                    st.metric("Revised Wins", revised_wins)
                    st.metric("Revised Losses", revised_losses)
                
                with col4:
                    # Compare with original
                    original_total_profit = results['Profit'].sum()
                    original_total_profit_dollars = results['Profit ($)'].sum()
                    original_trade_count = len(results)
                    
                    profit_diff = revised_total_profit_dollars - original_total_profit_dollars
                    trade_diff = revised_trade_count - original_trade_count
                    
                    st.metric("Profit Difference ($)", f"${profit_diff:,.2f}")
                    st.metric("Trade Count Difference", trade_diff)
                
                # Revised monthly summary
                st.subheader("📊 Revised Monthly Profit Summary")
                
                # Convert Date to datetime for proper grouping
                results_copy = results.copy()
                results_copy['Date'] = pd.to_datetime(results_copy['Date'])
                results_copy['Month'] = results_copy['Date'].dt.to_period('M')
                
                # Group by month and calculate revised profit
                revised_monthly_profit = results_copy.groupby('Month')['Revised Profit ($)'].sum().reset_index()
                revised_monthly_profit['Month'] = revised_monthly_profit['Month'].astype(str)
                
                # Display revised monthly summary
                col1, col2 = st.columns(2)
                with col1:
                            create_auto_sized_dataframe(revised_monthly_profit)
                
                with col2:
                    # Revised total profit summary
                    st.metric("Revised Total Profit (Points)", f"{revised_total_profit:.2f}")
                    st.metric("Revised Total Profit ($)", f"${revised_total_profit_dollars:,.2f}")
                
                # Download revised summary
                revised_summary_data = {
                    'Metric': ['Total Profit (Points)', 'Total Profit ($)', 'Trade Count', 'Win Rate (%)', 'Wins', 'Losses'],
                    'Original': [f"{original_total_profit:.2f}", f"${original_total_profit_dollars:,.2f}", original_trade_count, f"{win_rate:.1f}", wins, losses],
                    'Revised': [f"{revised_total_profit:.2f}", f"${revised_total_profit_dollars:,.2f}", revised_trade_count, f"{revised_win_rate:.1f}", revised_wins, revised_losses],
                    'Difference': [f"{revised_total_profit - original_total_profit:.2f}", f"${profit_diff:,.2f}", trade_diff, f"{revised_win_rate - win_rate:.1f}", revised_wins - wins, revised_losses - losses]
                }
                revised_summary_df = pd.DataFrame(revised_summary_data)
                
                st.subheader("📋 Original vs Revised Comparison")
                create_auto_sized_dataframe(revised_summary_df)
                
                # Download button
                csv = revised_summary_df.to_csv(index=False)
                st.download_button(
                    label="📥 Download Revised Summary as CSV",
                    data=csv,
                    file_name=f"{output_folder}/revised_summary_{datetime.now().strftime('%Y%m%d_%H%M%S')}.csv",
                    mime="text/csv"
                )
                
            else:
                st.info("Revised Score and Revised Profit ($) columns required for revised summary")
        
        with tab5:
            st.subheader("Processing Summary")
            
            # Summary statistics
            col1, col2, col3 = st.columns(3)
            
            with col1:
                st.metric("Total Days Processed", len(results))
                target_changes_count = len(results[results.get('Target Change', '') != '']) if 'Target Change' in results.columns else 0
                time_changes_count = len(results[results.get('Time Change', '') != '']) if 'Time Change' in results.columns else 0
                st.metric("Target Changes", target_changes_count)
                st.metric("Time Changes", time_changes_count)
                
                # Calculate Sharpe ratio for summary
                sharpe_ratio_summary = 0.0
                sortino_ratio_summary = 0.0
                calmar_ratio_summary = 0.0
                profit_factor_summary = 0.0
                if 'Profit' in results.columns and len(results) > 1:
                    profits = results['Profit'].dropna()
                    if len(profits) > 1:
                        mean_return = profits.mean()
                        std_return = profits.std()
                        sharpe_ratio_summary = mean_return / std_return if std_return != 0 else 0
                        
                        # Calculate Sortino ratio for summary
                        downside_returns = profits[profits < 0]
                        if len(downside_returns) > 1:
                            downside_deviation = downside_returns.std()
                            sortino_ratio_summary = mean_return / downside_deviation if downside_deviation != 0 else 0
                
                # Calculate Profit Factor for summary
                if 'Profit' in results.columns and len(results) > 0:
                    winning_trades = results[results['Result'] == 'Win']['Profit']
                    losing_trades = results[results['Result'] == 'Loss']['Profit']
                    
                    total_wins = winning_trades.sum() if len(winning_trades) > 0 else 0
                    total_losses = abs(losing_trades.sum()) if len(losing_trades) > 0 else 0
                    
                    if total_losses > 0:
                        profit_factor_summary = total_wins / total_losses
                    else:
                        profit_factor_summary = float('inf') if total_wins > 0 else 0.0
                else:
                    profit_factor_summary = 0.0
                
                # Calculate Risk-Reward ratio for summary
                if 'Profit' in results.columns and len(results) > 0:
                    winning_trades = results[results['Result'] == 'Win']['Profit']
                    losing_trades = results[results['Result'] == 'Loss']['Profit']
                    
                    avg_win = winning_trades.mean() if len(winning_trades) > 0 else 0
                    avg_loss = abs(losing_trades.mean()) if len(losing_trades) > 0 else 0
                    
                    if avg_loss > 0:
                        risk_reward_summary = avg_win / avg_loss
                    else:
                        risk_reward_summary = float('inf') if avg_win > 0 else 0.0
                else:
                    risk_reward_summary = 0.0
                
                st.metric("Sharpe Ratio", f"{sharpe_ratio_summary:.3f}")
                st.metric("Sortino Ratio", f"{sortino_ratio_summary:.3f}")
                st.metric("Profit Factor", f"{profit_factor_summary:.2f}" if profit_factor_summary != float('inf') else "∞")
                st.metric("Risk-Reward", f"{risk_reward_summary:.2f}" if risk_reward_summary != float('inf') else "∞")
                
                # Calculate Maximum Drawdown for summary
                max_drawdown_summary = 0.0
                max_drawdown_summary_dollars = 0.0
                if 'Profit' in results.columns and len(results) > 0:
                    profits = results['Profit'].dropna()
                    if len(profits) > 0:
                        cumulative = profits.cumsum()
                        running_max = cumulative.expanding().max()
                        drawdown = cumulative - running_max
                        max_drawdown_summary = abs(drawdown.min()) if len(drawdown) > 0 else 0
                        
                        # Convert to dollars
                        if 'processor' in st.session_state:
                            try:
                                # Get instrument from results data
                                if 'results' in st.session_state and len(st.session_state['results']) > 0:
                                    # Get instrument from Stream column (e.g., "NQ1" -> "NQ")
                                    stream = st.session_state['results'].iloc[0].get('Stream', 'ES1')
                                    instrument = stream[:2] if len(stream) >= 2 else 'ES'
                                else:
                                    instrument = 'ES'
                                contract_values = {"ES": 50, "NQ": 10, "YM": 5, "CL": 1000, "NG": 10000, "GC": 100}  # Correct contract values
                                contract_value = contract_values.get(instrument, 50)
                                max_drawdown_summary_dollars = max_drawdown_summary * contract_value
                            except:
                                max_drawdown_summary_dollars = max_drawdown_summary * 50  # Default to ES
                        else:
                            max_drawdown_summary_dollars = max_drawdown_summary * 50  # Default to ES
                st.metric("Max Drawdown", f"{max_drawdown_summary:.2f}")
                st.metric("Max Drawdown ($)", f"${max_drawdown_summary_dollars:,.2f}")
                
                # Calculate Calmar ratio for summary
                if 'Profit' in results.columns and len(results) > 1:
                    profits_for_calmar = results['Profit'].dropna()
                    if len(profits_for_calmar) > 0:
                        mean_return = profits_for_calmar.mean()
                        annualized_return = mean_return * 252
                        if max_drawdown_summary > 0:
                            calmar_ratio_summary = annualized_return / max_drawdown_summary
                        else:
                            calmar_ratio_summary = 0.0
                    else:
                        calmar_ratio_summary = 0.0
                else:
                    calmar_ratio_summary = 0.0
                
                st.metric("Calmar Ratio", f"{calmar_ratio_summary:.3f}")
            
            with col2:
                if len(results) > 0:
                    final_row = results.iloc[-1]
                    st.metric("Final Time Slot", final_row.get('Time', 'N/A'))
                    st.metric("Final Target", final_row.get('Target', 'N/A'))
                    st.metric("Last Result", final_row.get('Result', 'N/A'))
            
            with col3:
                # Result distribution - with safe column access
                if 'Result' in results.columns:
                    result_counts = results['Result'].value_counts()
                    st.metric("Wins", result_counts.get('Win', 0))
                    st.metric("Losses", result_counts.get('Loss', 0))
                    st.metric("Break-Evens", result_counts.get('BE', 0))
                    st.metric("No Trades", result_counts.get('NoTrade', 0))
                else:
                    st.metric("Wins", "N/A")
                    st.metric("Losses", "N/A")
                    st.metric("Break-Evens", "N/A")
                    st.metric("No Trades", "N/A")
            
            # Data usage summary - with safe column access
            st.subheader("Data Usage Summary")
            if 'Time' in results.columns and 'Target' in results.columns:
                usage_summary = results.groupby(['Time', 'Target']).size().reset_index(name='Count')
                create_auto_sized_dataframe(usage_summary)
            else:
                st.info("Time and Target columns not available for usage summary")
        
        with tab6:
            st.subheader("📅 Day of Week Profit Analysis")
            
            # Get day of week analysis from processor
            if 'processor' in st.session_state:
                processor = st.session_state['processor']
                day_analysis = processor.get_day_of_week_analysis(results)
                
                if day_analysis and 'summary' in day_analysis:
                    # Display summary metrics
                    col1, col2, col3, col4 = st.columns(4)
                    
                    with col1:
                        st.metric("Best Day", day_analysis['best_day'] or "N/A")
                    with col2:
                        st.metric("Worst Day", day_analysis['worst_day'] or "N/A")
                    with col3:
                        # Use correct contract value based on instrument
                        contract_values = {"ES": 50, "NQ": 10, "YM": 5, "CL": 1000, "NG": 10000, "GC": 100}  # Correct contract values
                        contract_value = contract_values.get('ES', 50)  # Default to ES value
                        total_profit_dollars = day_analysis['total_profit'] * contract_value
                        st.metric("Total Profit ($)", f"${total_profit_dollars:,.2f}")
                    with col4:
                        st.metric("Overall Win Rate", f"{day_analysis['overall_win_rate']:.1f}%")
                    
                    # Convert summary to DataFrame for display
                    day_df = pd.DataFrame(day_analysis['summary']).T
                    day_df = day_df.reset_index()
                    day_df.rename(columns={'index': 'Day of Week'}, inplace=True)
                    
                    # Display the day-of-week table
                    st.subheader("📊 Day of Week Statistics")
                    create_auto_sized_dataframe(day_df)
                    
                    # ========== REVISED VERSION ==========
                    if 'Revised Score' in results.columns and 'Revised Profit ($)' in results.columns:
                        st.markdown("---")
                        st.subheader("📊 **REVISED** Day of Week Statistics (Excluding Filtered Days)")
                        
                        # Calculate revised day-of-week analysis using Revised Profit ($)
                        revised_results = results[results['Revised Score'] != ''].copy()
                        if len(revised_results) > 0:
                            revised_day_analysis = processor.get_day_of_week_analysis(revised_results)
                            
                            if revised_day_analysis and 'summary' in revised_day_analysis:
                                # Display revised summary metrics
                                col1r, col2r, col3r, col4r = st.columns(4)
                                
                                with col1r:
                                    st.metric("Revised Best Day", revised_day_analysis['best_day'] or "N/A")
                                with col2r:
                                    st.metric("Revised Worst Day", revised_day_analysis['worst_day'] or "N/A")
                                with col3r:
                                    revised_total_profit_dollars = revised_day_analysis['total_profit'] * contract_value
                                    st.metric("Revised Total Profit ($)", f"${revised_total_profit_dollars:,.2f}")
                                with col4r:
                                    st.metric("Revised Overall Win Rate", f"{revised_day_analysis['overall_win_rate']:.1f}%")
                                
                                # Convert revised summary to DataFrame
                                revised_day_df = pd.DataFrame(revised_day_analysis['summary']).T
                                revised_day_df = revised_day_df.reset_index()
                                revised_day_df.rename(columns={'index': 'Day of Week'}, inplace=True)
                                revised_day_df = revised_day_df.rename(columns={
                                    'Total_Profit': 'Total Profit (Points)',
                                    'Avg_Profit': 'Avg Profit (Points)',
                                    'Trade_Count': 'Trade Count',
                                    'Win_Count': 'Win Count',
                                    'Win_Rate': 'Win Rate (%)',
                                    'Total_Profit_Dollars': 'Total Profit ($)',
                                    'Avg_Profit_Dollars': 'Avg Profit ($)'
                                })
                                
                                # Display revised table
                                create_auto_sized_dataframe(revised_day_df)
                        st.markdown("---")
                    
                    # Create profit chart
                    st.subheader("📈 Profit by Day of Week")
                    
                    # Prepare data for chart
                    chart_data = day_df[['Day of Week', 'Total_Profit_Dollars']].copy()
                    chart_data = chart_data.set_index('Day of Week')
                    
                    # Create bar chart
                    st.bar_chart(chart_data)
                    
                    # Create win rate chart
                    st.subheader("🎯 Win Rate by Day of Week")
                    win_rate_data = day_df[['Day of Week', 'Win_Rate']].copy()
                    win_rate_data = win_rate_data.set_index('Day of Week')
                    
                    st.bar_chart(win_rate_data)
                    
                    # Detailed breakdown
                    st.subheader("📋 Detailed Breakdown")
                    
                    # Create a more detailed table with all metrics
                    detailed_df = day_df.copy()
                    detailed_df = detailed_df.rename(columns={
                        'Total_Profit': 'Total Profit (Points)',
                        'Avg_Profit': 'Avg Profit (Points)',
                        'Trade_Count': 'Trade Count',
                        'Win_Count': 'Win Count',
                        'Win_Rate': 'Win Rate (%)',
                        'Total_Profit_Dollars': 'Total Profit ($)',
                        'Avg_Profit_Dollars': 'Avg Profit ($)'
                    })
                    
                    create_auto_sized_dataframe(detailed_df)
                    
                    # Download day-of-week analysis
                    csv = detailed_df.to_csv(index=False)
                    st.download_button(
                        label="📥 Download Day of Week Analysis as CSV",
                        data=csv,
                        file_name=f"{output_folder}/day_of_week_analysis_{datetime.now().strftime('%Y%m%d_%H%M%S')}.csv",
                        mime="text/csv"
                    )
                    
                else:
                    st.warning("No day-of-week analysis data available. Please run the processor first.")
            else:
                st.warning("No processor data available. Please run the processor first.")

        with tab7:
            st.subheader("📅 Day of Month Profit Pivot")
            if len(results) > 0 and 'Date' in results.columns and 'Profit' in results.columns:
                df_dom = results.copy()
                df_dom['Date'] = pd.to_datetime(df_dom['Date'], errors='coerce')
                df_dom = df_dom.dropna(subset=['Date'])
                if len(df_dom) == 0:
                    st.info("No valid dates to analyze")
                else:
                    df_dom['DayOfMonth'] = df_dom['Date'].dt.day
                    pivot = df_dom.groupby('DayOfMonth')['Profit'].sum().reset_index()
                    # Determine contract value using processor if available
                    contract_values = {"ES": 50, "NQ": 10, "YM": 5, "CL": 1000, "NG": 10000, "GC": 100}  # Correct contract values
                    instrument = 'ES'
                    if 'processor' in st.session_state:
                        try:
                            # Get instrument from results data
                            if 'results' in st.session_state and len(st.session_state['results']) > 0:
                                # Get instrument from Stream column (e.g., "NQ1" -> "NQ")
                                stream = st.session_state['results'].iloc[0].get('Stream', 'ES1')
                                instrument = stream[:2] if len(stream) >= 2 else 'ES'
                            else:
                                instrument = 'ES'
                        except Exception:
                            instrument = 'ES'
                    contract_value = contract_values.get(instrument, 50)
                    pivot['Profit ($)'] = pivot['Profit'] * contract_value
                    # Display table
                    st.dataframe(pivot.sort_values('DayOfMonth'), use_container_width=True)
                    
                    # ========== REVISED VERSION ==========
                    if 'Revised Score' in results.columns and 'Revised Profit ($)' in results.columns:
                        st.markdown("---")
                        st.subheader("📊 **REVISED** Day of Month Profit Pivot (Excluding Filtered Days)")
                        
                        # Calculate revised day-of-month pivot using filtered results
                        revised_results = results[results['Revised Score'] != ''].copy()
                        if len(revised_results) > 0:
                            revised_df_dom = revised_results.copy()
                            revised_df_dom['Date'] = pd.to_datetime(revised_df_dom['Date'], errors='coerce')
                            revised_df_dom = revised_df_dom.dropna(subset=['Date'])
                            
                            if len(revised_df_dom) > 0:
                                revised_df_dom['DayOfMonth'] = revised_df_dom['Date'].dt.day
                                revised_pivot = revised_df_dom.groupby('DayOfMonth')['Profit'].sum().reset_index()
                                revised_pivot['Profit ($)'] = revised_pivot['Profit'] * contract_value
                                
                                # Display revised table
                                st.dataframe(revised_pivot.sort_values('DayOfMonth'), use_container_width=True)
                        st.markdown("---")
                    
                    # Chart
                    chart_data = pivot.set_index('DayOfMonth')[['Profit']]
                    st.subheader("📈 Profit (Points) by Day of Month")
                    st.bar_chart(chart_data)
                    # Download
                    csv = pivot.sort_values('DayOfMonth').to_csv(index=False)
                    st.download_button(
                        label="📥 Download Day of Month Pivot as CSV",
                        data=csv,
                        file_name=f"{output_folder}/day_of_month_pivot_{datetime.now().strftime('%Y%m%d_%H%M%S')}.csv",
                        mime="text/csv"
                    )
            else:
                st.info("Date and Profit columns required for day-of-month pivot")

        with tab8:
            st.subheader("📅 Yearly Profit")
            if len(results) > 0 and 'Date' in results.columns and 'Profit' in results.columns:
                df_y = results.copy()
                df_y['Date'] = pd.to_datetime(df_y['Date'], errors='coerce')
                df_y = df_y.dropna(subset=['Date'])
                if len(df_y) == 0:
                    st.info("No valid dates to analyze")
                else:
                    df_y['Year'] = df_y['Date'].dt.year
                    yearly = df_y.groupby('Year')['Profit'].sum().reset_index()
                    # Determine contract value using processor if available
                    contract_values = {"ES": 50, "NQ": 10, "YM": 5, "CL": 1000, "NG": 10000, "GC": 100}  # Correct contract values
                    instrument = 'ES'
                    if 'processor' in st.session_state:
                        try:
                            # Get instrument from results data
                            if 'results' in st.session_state and len(st.session_state['results']) > 0:
                                # Get instrument from Stream column (e.g., "NQ1" -> "NQ")
                                stream = st.session_state['results'].iloc[0].get('Stream', 'ES1')
                                instrument = stream[:2] if len(stream) >= 2 else 'ES'
                            else:
                                instrument = 'ES'
                        except Exception:
                            instrument = 'ES'
                    contract_value = contract_values.get(instrument, 50)
                    yearly['Profit ($)'] = yearly['Profit'] * contract_value
                    # Display table
                    st.dataframe(yearly.sort_values('Year'), use_container_width=True)
                    
                    # ========== REVISED VERSION ==========
                    if 'Revised Score' in results.columns and 'Revised Profit ($)' in results.columns:
                        st.markdown("---")
                        st.subheader("📊 **REVISED** Yearly Profit (Excluding Filtered Days)")
                        
                        # Calculate revised yearly profit using filtered results
                        revised_results = results[results['Revised Score'] != ''].copy()
                        if len(revised_results) > 0:
                            revised_df_y = revised_results.copy()
                            revised_df_y['Date'] = pd.to_datetime(revised_df_y['Date'], errors='coerce')
                            revised_df_y = revised_df_y.dropna(subset=['Date'])
                            
                            if len(revised_df_y) > 0:
                                revised_df_y['Year'] = revised_df_y['Date'].dt.year
                                revised_yearly = revised_df_y.groupby('Year')['Profit'].sum().reset_index()
                                revised_yearly['Profit ($)'] = revised_yearly['Profit'] * contract_value
                                
                                # Display revised table
                                st.dataframe(revised_yearly.sort_values('Year'), use_container_width=True)
                        st.markdown("---")
                    
                    # Chart
                    chart_data = yearly.set_index('Year')[['Profit']]
                    st.subheader("📈 Profit (Points) by Year")
                    st.bar_chart(chart_data)
                    # Download
                    csv = yearly.sort_values('Year').to_csv(index=False)
                    st.download_button(
                        label="📥 Download Yearly Profit as CSV",
                        data=csv,
                        file_name=f"{output_folder}/yearly_profit_{datetime.now().strftime('%Y%m%d_%H%M%S')}.csv",
                        mime="text/csv"
                    )
            else:
                st.info("Date and Profit columns required for yearly profit")
        
    
    # Footer
    st.markdown("---")
    st.markdown("**Sequential Target Change & Time Change Processor** | Built with Streamlit")
    
    # Debug info
    if st.sidebar.checkbox("Show Debug Info", key="debug_info_system"):
        st.sidebar.markdown("---")
        st.sidebar.markdown("**Debug Information:**")
        st.sidebar.markdown(f"• Python version: {os.sys.version}")
        st.sidebar.markdown(f"• Working directory: {os.getcwd()}")
        st.sidebar.markdown(f"• Data folder exists: {os.path.exists(data_folder)}")

if __name__ == "__main__":
    main()
