#!/usr/bin/env python3
"""
Streamlit App for Sequential Time Change Processor
"""

import streamlit as st
import pandas as pd
import os
from datetime import datetime
import sys
import io

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

def create_auto_sized_dataframe(data, width='stretch'):
    """Create a dataframe with automatic column sizing based on content"""
    # Convert object columns to string to avoid Arrow serialization errors
    if not data.empty:
        data = data.copy()  # Make a copy to avoid modifying original
        for col in data.columns:
            if data[col].dtype == 'object':
                # Try to convert to string, handling any errors
                try:
                    data[col] = data[col].astype(str)
                except Exception:
                    # If conversion fails, replace with string representation
                    data[col] = data[col].apply(lambda x: str(x) if pd.notna(x) else '')
    
    if data.empty:
        return st.dataframe(data, width=width, hide_index=True)
    
    # Use Streamlit's built-in auto-sizing without forcing column widths
    # This allows Streamlit to automatically size columns based on content
    return st.dataframe(
        data, 
        width='content',  # Let columns size themselves
        hide_index=True
    )

def main():
    st.set_page_config(
        page_title="Sequential Processor App",
        page_icon=None,
        layout="wide"
    )
    
    st.title("Sequential Time Change Processor")
    st.markdown("---")
    
    # Sidebar for configuration
    st.sidebar.header("Configuration")
    
    # Data file selection
    # Try multiple possible paths for analyzer_runs folder
    possible_paths = [
        os.path.join(PROJECT_ROOT, "data", "analyzer_runs"),  # Absolute from project root
        os.path.join(os.getcwd(), "data", "analyzer_runs"),   # Relative to current working directory
        "data/analyzer_runs",                                  # Simple relative path
    ]
    
    analyzer_runs_folder = None
    for path in possible_paths:
        abs_path = os.path.abspath(path)
        if os.path.exists(abs_path):
            analyzer_runs_folder = abs_path
            break
    
    # Initialize variables
    data_path = None
    selected_file = "Not selected"
    selected_files_list = []  # List of all selected file paths for multi-file processing
    
    # Debug: Show what folder we're looking in
    show_debug = st.sidebar.checkbox("Show Debug Info", value=False, key="debug_info_paths")
    if show_debug:
        st.sidebar.write(f"**Project Root:** `{PROJECT_ROOT}`")
        st.sidebar.write(f"**Current Working Dir:** `{os.getcwd()}`")
        st.sidebar.write(f"**Looking in:** `{analyzer_runs_folder}`")
        st.sidebar.write(f"**Folder exists:** {os.path.exists(analyzer_runs_folder) if analyzer_runs_folder else False}")
        st.sidebar.write(f"**Tried paths:**")
        for path in possible_paths:
            abs_path = os.path.abspath(path)
            exists = os.path.exists(abs_path)
            st.sidebar.write(f"  - `{abs_path}` {'Found' if exists else 'Not Found'}")
    
    if analyzer_runs_folder and os.path.exists(analyzer_runs_folder):
        # Get instrument folders (directories only)
        instrument_folders = []
        try:
            for item in os.listdir(analyzer_runs_folder):
                item_path = os.path.join(analyzer_runs_folder, item)
                if os.path.isdir(item_path):
                    instrument_folders.append(item)
        except Exception as e:
            st.sidebar.error(f"Error reading analyzer_runs folder: {e}")
            instrument_folders = []
        
        if instrument_folders:
            # Sort instrument folders for consistent display
            instrument_folders.sort()
            
            # Let user select instrument folder
            selected_instrument = st.sidebar.selectbox(
                "Select Instrument Folder:",
                instrument_folders,
                help="Select the instrument folder to search for parquet files"
            )
            
            instrument_folder_path = os.path.join(analyzer_runs_folder, selected_instrument)
            
            # Search mode selection
            st.sidebar.markdown("---")
            search_mode = st.sidebar.radio(
                "Search Mode:",
                ["Full Search", "Search by Year"],
                index=0,  # Default to Full Search
                key=f"search_mode_{selected_instrument}",  # Unique key per instrument
                help="Full Search: Show ALL files recursively from all years/months. Search by Year: Filter files by year."
            )
            
            selected_year = None
            if search_mode == "Search by Year":
                # Get available years from filenames and folder names (search recursively)
                try:
                    import re
                    available_years = set()
                    
                    # Search recursively for years in both filenames and folder names
                    for root, dirs, files in os.walk(instrument_folder_path):
                        # Check folder names for years
                        folder_name = os.path.basename(root)
                        folder_year_matches = re.findall(r'\b(20\d{2})\b', folder_name)
                        if folder_year_matches:
                            available_years.add(int(folder_year_matches[0]))
                        
                        # Check filenames for years
                        for file in files:
                            if file.endswith('.parquet'):
                                year_matches = re.findall(r'\b(20\d{2})\b', file)  # Match years 2000-2099
                                if year_matches:
                                    available_years.add(int(year_matches[0]))
                    
                    if available_years:
                        available_years = sorted(list(available_years), reverse=True)  # Most recent first
                        selected_year = st.sidebar.selectbox(
                            "Select Year:",
                            available_years,
                            key=f"year_select_{selected_instrument}",  # Unique key per instrument
                            help="Filter files by year (checks both filenames and folder names)"
                        )
                    else:
                        st.sidebar.warning("No years detected. Showing all files.")
                        search_mode = "Full Search"  # Fallback to full search if no years found
                        selected_year = None
                except Exception as e:
                    st.sidebar.error(f"Error detecting years: {e}")
                    selected_year = None
            
            # Search for parquet files in the selected instrument folder (including subfolders)
            data_files = []
            file_paths = {}  # Map display name to full path
            
            try:
                # Search recursively in the instrument folder and its subfolders
                # Count files found for debugging
                total_files_scanned = 0
                files_filtered_out = 0
                
                for root, dirs, files in os.walk(instrument_folder_path):
                    for file in files:
                        if file.endswith('.parquet'):
                            total_files_scanned += 1
                            
                            # Only filter if we're in "Search by Year" mode AND a year is selected
                            if search_mode == "Search by Year" and selected_year is not None:
                                # Check if filename contains the selected year OR if we're in a folder with that year name
                                folder_name = os.path.basename(root)
                                file_has_year = str(selected_year) in file
                                folder_has_year = str(selected_year) in folder_name
                                if not (file_has_year or folder_has_year):
                                    files_filtered_out += 1
                                    continue  # Skip this file if it doesn't match the year
                            
                            # Include this file (either Full Search mode, or it passed the year filter)
                            # Create display name with relative path if in subfolder
                            full_path = os.path.join(root, file)
                            rel_path = os.path.relpath(full_path, instrument_folder_path)
                            
                            # Use relative path as display name if in subfolder, otherwise just filename
                            if os.path.dirname(rel_path):
                                display_name = rel_path.replace('\\', '/')  # Use forward slashes for display
                            else:
                                display_name = file
                            
                            data_files.append(display_name)
                            file_paths[display_name] = full_path
                
                # Debug output
                if show_debug:
                    st.sidebar.write(f"**Search Stats:**")
                    st.sidebar.write(f"  - Total files scanned: {total_files_scanned}")
                    st.sidebar.write(f"  - Files filtered out: {files_filtered_out}")
                    st.sidebar.write(f"  - Files included: {len(data_files)}")
                    st.sidebar.write(f"  - Search path: `{instrument_folder_path}`")
            except Exception as e:
                st.sidebar.error(f"Error reading instrument folder: {e}")
                import traceback
                st.sidebar.error(f"Traceback: {traceback.format_exc()}")
                data_files = []
            
            if data_files:
                # Debug: Show file list if debug enabled
                if show_debug:
                    st.sidebar.write(f"**Search Mode:** {search_mode}")
                    st.sidebar.write(f"**Selected Year:** {selected_year if selected_year else 'None (Full Search)'}")
                    st.sidebar.write(f"**Files found:**")
                    for i, file in enumerate(data_files[:20]):  # Show first 20
                        st.sidebar.write(f"  {i+1}. `{file}`")
                    if len(data_files) > 20:
                        st.sidebar.write(f"  ... and {len(data_files) - 20} more")
                
                # Sort files for consistent display
                data_files.sort()
                
                # When Full Search is enabled, automatically select all files
                if search_mode == "Full Search":
                    # Full Search automatically selects all files - no manual selection needed
                    selected_files = data_files  # Automatically select all files
                    selected_files_list = [file_paths[f] for f in selected_files]  # Get full paths
                    selected_file = f"{len(selected_files)} files"
                    data_path = selected_files_list[0] if selected_files_list else None  # For backward compatibility
                    
                    if len(selected_files) == 0:
                        selected_file = "No file selected"
                        data_path = None
                        selected_files_list = []
                else:
                    # Single select for "Search by Year" mode
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
                    # Use full path from the mapping
                    data_path = file_paths[selected_file]
                    selected_files_list = [data_path]  # Single file for Search by Year mode
                
                # Track file changes to clear cached state
                if selected_file != "No file selected":
                    st.session_state['previous_data_file'] = st.session_state.get('current_data_file', None)
                    st.session_state['current_data_file'] = selected_file
            else:
                # No files found in selected instrument folder
                st.sidebar.warning(f"No parquet files found in `{selected_instrument}` folder")
                st.sidebar.info(f"**Searched in:** `{instrument_folder_path}`")
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
                        st.sidebar.error(f"File not found: `{data_path}`")
                        data_path = None
                        selected_file = "Invalid path"
                    else:
                        st.sidebar.success(f"Using: `{data_path}`")
                        selected_file = os.path.basename(data_path)
                else:
                    data_path = None
                    selected_file = "No file selected"
        else:
            # No instrument folders found
            st.sidebar.warning("No instrument folders found in analyzer_runs")
            st.sidebar.info(f"**Searched in:** `{analyzer_runs_folder}`")
            st.sidebar.markdown("**Enter a custom file path:**")
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
        st.sidebar.warning("analyzer_runs folder not found")
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
    
    # Day filtering options
    st.sidebar.subheader("Day Filtering")
    
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
    st.sidebar.subheader("Processing Options")
    # Process all available data by default
    max_days = 10000  # Effectively unlimited
    
    # Starting configuration - dynamically set based on selected data file
    st.sidebar.subheader("Starting Configuration")
    
    # Initialize variables
    instrument = "ES"  # Default
    available_times = ["08:00", "09:00"]
    start_time = "08:00"
    sample_data = None
    
    # Load data to detect instrument and available options
    # If multiple files selected, load and combine them for detection
    if 'selected_files_list' in locals() and selected_files_list and len(selected_files_list) > 1:
        try:
            dataframes = []
            for file_path in selected_files_list:
                if os.path.exists(file_path):
                    df = pd.read_parquet(file_path)
                    dataframes.append(df)
            if dataframes:
                sample_data = pd.concat(dataframes, ignore_index=True)
        except Exception as e:
            st.sidebar.error(f"Error loading multiple files: {e}")
            sample_data = None
    elif data_path and os.path.exists(data_path):
        try:
            sample_data = pd.read_parquet(data_path)
        except Exception as e:
            st.sidebar.error(f"Error loading data: {e}")
            sample_data = None
    
    # Process sample_data if available
    if sample_data is not None and len(sample_data) > 0:
        try:
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
            
            # Show detected instrument
            st.sidebar.info(f"**Detected Instrument: {instrument}**")
            
        except Exception as e:
            st.sidebar.error(f"Error processing data: {e}")
            # Fallback to ES defaults
            start_time = st.sidebar.selectbox("Start Time", ["08:00", "09:00"], index=0)
            instrument = "ES"
            available_times = ["08:00", "09:00"]
    else:
        # No data file available - use default settings
        st.sidebar.info("**Using default settings (no data file)**")
        st.sidebar.info("**Select instrument manually below**")
        
        # Let user select instrument
        instrument = st.sidebar.selectbox(
            "Select Instrument:",
            ["ES", "NQ", "YM", "CL", "NG", "GC"],
            index=0,
            help="Choose the trading instrument for default settings"
        )
        
        # Set defaults based on selected instrument
        available_times = ["08:00", "09:00"]
        
        start_time = st.sidebar.selectbox("Start Time", available_times, index=0, help="Time slot to start processing from")
    
    # Show info about processing all data
    st.sidebar.info("**Processing ALL available data**")
    
    # Display current settings
    st.sidebar.markdown("---")
    st.sidebar.markdown("**Current Settings:**")
    if data_path:
        st.sidebar.markdown(f"• Data File: `{selected_file}`")
        st.sidebar.markdown(f"• Data Path: `{data_path}`")
    else:
        st.sidebar.markdown(f"• Data File: `{selected_file}`")
        st.sidebar.warning("Please select a data file to continue")
    st.sidebar.markdown(f"• Instrument: `{instrument}`")
    st.sidebar.markdown(f"• Start Time: `{start_time}`")
    st.sidebar.markdown(f"• Available Times: {available_times if 'available_times' in locals() else 'N/A'}")
    st.sidebar.markdown(f"• Time Change: **ALWAYS ON**")
    st.sidebar.markdown(f"• Excluded Days of Week: {exclude_days_of_week if exclude_days_of_week else 'None'}")
    st.sidebar.markdown(f"• Excluded Days of Month: {exclude_days_of_month if exclude_days_of_month else 'None'}")
    st.sidebar.markdown(f"• Loss Recovery Mode: {'ON' if loss_recovery_mode else 'OFF'}")
    st.sidebar.markdown(f"• Processing: **ALL DATA**")
    
    # Output folder selection (global for all tabs)
    st.markdown("---")
    st.subheader("Output Folder")
    
    col_folder1, col_folder2 = st.columns([2, 1])
    
    with col_folder1:
        # Get today's date for temp folder structure
        from datetime import datetime
        today = datetime.now().strftime('%Y-%m-%d')
        
        output_folder_options = {
            f"Default (data/sequencer_temp/{today}/)": "data/sequencer_temp",
            "Custom Folder": "custom"
        }
        
        selected_output_option = st.selectbox(
            "Save results to:",
            list(output_folder_options.keys()),
            help="Choose where to save your sequential processor runs"
        )
        
        custom_folder = None
        selected_value = output_folder_options.get(selected_output_option, "data/sequencer_temp")
        if selected_value == "custom":
            custom_folder = st.text_input(
                "Custom folder path:",
                value="my_sequential_runs",
                help="Enter folder name (will be created if it doesn't exist)"
            )
        
        # Determine final output folder (use absolute path)
        if custom_folder:
            output_folder = os.path.join(PROJECT_ROOT, custom_folder)
        elif selected_value == "data/sequencer_temp":
            # Use date-based temp folder structure
            output_folder = os.path.join(PROJECT_ROOT, f"data/sequencer_temp/{today}")
        else:
            output_folder = os.path.join(PROJECT_ROOT, selected_value)
    
    with col_folder2:
        st.info(f"**Current folder:**\n`{output_folder}/`")
    
    st.markdown("---")
    
    # Main content area
    col1, col2 = st.columns([1, 2])
    
    with col1:
        st.subheader("Run Processor")
        
        if st.button("Start Processing", type="primary"):
            # Check if we have files to process
            if selected_files_list and len(selected_files_list) > 1:
                # Multiple files selected - validate all exist
                missing_files = [f for f in selected_files_list if not os.path.exists(f)]
                if missing_files:
                    st.error(f"Some selected files not found: {missing_files}")
                    st.stop()
            elif not data_path or not os.path.exists(data_path):
                st.error("Please select a valid data file first!")
                st.stop()
            
            with st.spinner("Processing..."):
                try:
                    # Validate start_time is compatible with the data before processing
                    # Load all selected files for validation
                    if selected_files_list and len(selected_files_list) > 1:
                        validation_dataframes = []
                        for file_path in selected_files_list:
                            if os.path.exists(file_path):
                                df = pd.read_parquet(file_path)
                                validation_dataframes.append(df)
                        if validation_dataframes:
                            validation_data = pd.concat(validation_dataframes, ignore_index=True)
                        else:
                            validation_data = None
                    else:
                        validation_data = pd.read_parquet(data_path)
                    # Ensure times are strings for proper comparison
                    validation_times = sorted([str(t) for t in validation_data['Time'].unique()])
                    
                    # Convert start_time to string to ensure compatibility
                    start_time_str = str(start_time)
                    
                    if start_time_str not in validation_times:
                        st.error(f"Start time '{start_time_str}' is not available in the selected data file.")
                        st.info(f"Available time slots: {validation_times}")
                        st.warning("**Solution:** Please select a different data file or change the start time to one of the available slots.")
                        st.stop()
                    
                    # Initialize processor with auto-detected settings
                    # Use selected_files_list if multiple files selected, otherwise use single data_path
                    files_to_load = selected_files_list if selected_files_list and len(selected_files_list) > 1 else [data_path]
                    
                    processor = SequentialProcessorV2(
                        data_path,  # Still required for backward compatibility
                        start_time_str, 
                        "normal", 
                        exclude_days_of_week, 
                        exclude_days_of_month, 
                        loss_recovery_mode,
                        data_files=files_to_load if len(files_to_load) > 1 else None  # Only pass if multiple files
                    )
                    
                    # Process data (time change is always enabled)
                    results = processor.process_sequential(
                        max_days=max_days
                    )
                    
                    # Store results in session state
                    st.session_state['results'] = results
                    st.session_state['processor'] = processor
                    
                    st.success("Processing completed!")
                    
                except Exception as e:
                    st.error(f"Error during processing: {str(e)}")
                    # Show more details for debugging
                    import traceback
                    with st.expander("Show detailed error information"):
                        st.code(traceback.format_exc())
    
    with col2:
        st.subheader("Quick Stats")
        if 'results' in st.session_state:
            results = st.session_state['results']
            
            # Calculate stats
            total_days = len(results)
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
        st.header("Results")
        
        results = st.session_state['results']
        
        # Tabs for different views
        tab1, tab2, tab3, tab4, tab5, tab6, tab7 = st.tabs(["Full Results", "Time Changes", "Revised Summary", "Summary", "Day of Week Analysis", "Day of Month Pivot", "Yearly Profit"])
        
        with tab1:
            st.subheader("Complete Results")
            
            # Display options
            col1, col2 = st.columns([1, 1])
            with col1:
                # Set default columns based on what's available
                default_cols = []
                base_columns = ['Date', 'Day of Week', 'Stream', 'Time', 'Target', 'Range', 'SL', 'Profit', 'Peak', 'Direction', 'Result', 'Revised Score', 'Time Change', 'Profit ($)', 'Revised Profit ($)']
                
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
            st.subheader("Monthly Profit Summary")
            
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
                
                # Download buttons
                timestamp = datetime.now().strftime('%Y%m%d_%H%M%S')
                
                col_dl1, col_dl2 = st.columns(2)
                with col_dl1:
                    csv = results.to_csv(index=False)
                    st.download_button(
                        label="Download as CSV",
                        data=csv,
                        file_name=f"sequential_run_{timestamp}.csv",
                        mime="text/csv"
                    )
                
                with col_dl2:
                    # Convert DataFrame to Parquet bytes for download
                    parquet_buffer = io.BytesIO()
                    results.to_parquet(parquet_buffer, index=False, compression='snappy')
                    parquet_buffer.seek(0)
                    st.download_button(
                        label="Download as Parquet",
                        data=parquet_buffer.getvalue(),
                        file_name=f"sequential_run_{timestamp}.parquet",
                        mime="application/octet-stream"
                    )
                
                # Save button
                st.markdown("---")
                st.subheader("Save Results")
                
                if st.button("Save Results", type="primary", help="Save current results to selected folder"):
                    # Ensure output folder exists
                    os.makedirs(output_folder, exist_ok=True)
                    
                    # Save as Parquet (primary format, like analyzer)
                    parquet_filename = f"{output_folder}/sequential_run_{timestamp}.parquet"
                    results.to_parquet(parquet_filename, index=False, compression='snappy')
                    
                    # Also save as CSV for compatibility
                    csv_filename = f"{output_folder}/sequential_run_{timestamp}.csv"
                    results.to_csv(csv_filename, index=False)
                    
                    st.success(f"Results saved to:")
                    st.info(f"Parquet: `{parquet_filename}`")
                    st.info(f"CSV: `{csv_filename}`")
                    st.balloons()
                
                # Show save location info
                if selected_value == "data/sequencer_temp":
                    st.info(f"Results will be saved to:\nParquet: `data/sequencer_temp/{today}/sequential_run_{timestamp}.parquet`\nCSV: `data/sequencer_temp/{today}/sequential_run_{timestamp}.csv`")
                else:
                    st.info(f"Results will be saved to:\nParquet: `{output_folder}/sequential_run_{timestamp}.parquet`\nCSV: `{output_folder}/sequential_run_{timestamp}.csv`")
            else:
                st.info("Date and Profit columns required for monthly analysis")
        
        with tab2:
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
                    width='stretch'
                )
            else:
                st.info("No time changes occurred")
        
        with tab3:
            st.subheader("Revised Summary (Excluding Filtered Days)")
            
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
                st.subheader("Revised Monthly Profit Summary")
                
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
                    'Original': [f"{original_total_profit:.2f}", f"${original_total_profit_dollars:,.2f}", str(original_trade_count), f"{win_rate:.1f}", str(wins), str(losses)],
                    'Revised': [f"{revised_total_profit:.2f}", f"${revised_total_profit_dollars:,.2f}", str(revised_trade_count), f"{revised_win_rate:.1f}", str(revised_wins), str(revised_losses)],
                    'Difference': [f"{revised_total_profit - original_total_profit:.2f}", f"${profit_diff:,.2f}", str(trade_diff), f"{revised_win_rate - win_rate:.1f}", str(revised_wins - wins), str(revised_losses - losses)]
                }
                revised_summary_df = pd.DataFrame(revised_summary_data)
                # Ensure all columns are strings to avoid Arrow serialization errors
                for col in revised_summary_df.columns:
                    revised_summary_df[col] = revised_summary_df[col].astype(str)
                
                st.subheader("Original vs Revised Comparison")
                create_auto_sized_dataframe(revised_summary_df)
                
                # Download button
                csv = revised_summary_df.to_csv(index=False)
                st.download_button(
                    label="Download Revised Summary as CSV",
                    data=csv,
                    file_name=f"{output_folder}/revised_summary_{datetime.now().strftime('%Y%m%d_%H%M%S')}.csv",
                    mime="text/csv"
                )
                
            else:
                st.info("Revised Score and Revised Profit ($) columns required for revised summary")
        
        with tab4:
            st.subheader("Summary")
            
            # Summary statistics
            col1, col2, col3 = st.columns(3)
            
            with col1:
                st.metric("Total Days Processed", len(results))
                time_changes_count = len(results[results.get('Time Change', '') != '']) if 'Time Change' in results.columns else 0
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
        
        with tab5:
            st.subheader("Day of Week Profit Analysis")
            
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
                    st.subheader("Day of Week Statistics")
                    create_auto_sized_dataframe(day_df)
                    
                    # ========== REVISED VERSION ==========
                    if 'Revised Score' in results.columns and 'Revised Profit ($)' in results.columns:
                        st.markdown("---")
                        st.subheader("**REVISED** Day of Week Statistics (Excluding Filtered Days)")
                        
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
                    st.subheader("Profit by Day of Week")
                    
                    # Prepare data for chart
                    chart_data = day_df[['Day of Week', 'Total_Profit_Dollars']].copy()
                    chart_data = chart_data.set_index('Day of Week')
                    
                    # Create bar chart
                    st.bar_chart(chart_data)
                    
                    # Create win rate chart
                    st.subheader("Win Rate by Day of Week")
                    win_rate_data = day_df[['Day of Week', 'Win_Rate']].copy()
                    win_rate_data = win_rate_data.set_index('Day of Week')
                    
                    st.bar_chart(win_rate_data)
                    
                    # Detailed breakdown
                    st.subheader("Detailed Breakdown")
                    
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
                        label="Download Day of Week Analysis as CSV",
                        data=csv,
                        file_name=f"{output_folder}/day_of_week_analysis_{datetime.now().strftime('%Y%m%d_%H%M%S')}.csv",
                        mime="text/csv"
                    )
                    
                else:
                    st.warning("No day-of-week analysis data available. Please run the processor first.")
            else:
                st.warning("No processor data available. Please run the processor first.")

        with tab6:
            st.subheader("Day of Month Profit Pivot")
            if len(results) > 0 and 'Date' in results.columns and 'Profit' in results.columns:
                df_dom = results.copy()
                df_dom['Date'] = pd.to_datetime(df_dom['Date'], errors='coerce')
                df_dom = df_dom.dropna(subset=['Date'])
                if len(df_dom) == 0:
                    st.info("No valid dates to analyze")
                else:
                    df_dom['DayOfMonth'] = df_dom['Date'].dt.day
                    pivot = df_dom.groupby('DayOfMonth')['Profit'].sum().reset_index()
                    # Rename column for better display
                    pivot.rename(columns={'DayOfMonth': 'Day of Month'}, inplace=True)
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
                    st.dataframe(pivot.sort_values('Day of Month'), width='stretch')
                    
                    # ========== REVISED VERSION ==========
                    if 'Revised Score' in results.columns and 'Revised Profit ($)' in results.columns:
                        st.markdown("---")
                        st.subheader("**REVISED** Day of Month Profit Pivot (Excluding Filtered Days)")
                        
                        # Calculate revised day-of-month pivot using filtered results
                        revised_results = results[results['Revised Score'] != ''].copy()
                        if len(revised_results) > 0:
                            revised_df_dom = revised_results.copy()
                            revised_df_dom['Date'] = pd.to_datetime(revised_df_dom['Date'], errors='coerce')
                            revised_df_dom = revised_df_dom.dropna(subset=['Date'])
                            
                            if len(revised_df_dom) > 0:
                                revised_df_dom['DayOfMonth'] = revised_df_dom['Date'].dt.day
                                revised_pivot = revised_df_dom.groupby('DayOfMonth')['Profit'].sum().reset_index()
                                # Rename column for better display
                                revised_pivot.rename(columns={'DayOfMonth': 'Day of Month'}, inplace=True)
                                revised_pivot['Profit ($)'] = revised_pivot['Profit'] * contract_value
                                
                                # Display revised table
                                st.dataframe(revised_pivot.sort_values('Day of Month'), width='stretch')
                        st.markdown("---")
                    
                    # Chart
                    chart_data = pivot.set_index('Day of Month')[['Profit']]
                    st.subheader("Profit (Points) by Day of Month")
                    st.bar_chart(chart_data)
                    # Download
                    csv = pivot.sort_values('Day of Month').to_csv(index=False)
                    st.download_button(
                        label="Download Day of Month Pivot as CSV",
                        data=csv,
                        file_name=f"{output_folder}/day_of_month_pivot_{datetime.now().strftime('%Y%m%d_%H%M%S')}.csv",
                        mime="text/csv"
                    )
            else:
                st.info("Date and Profit columns required for day-of-month pivot")

        with tab7:
            st.subheader("Yearly Profit")
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
                    st.dataframe(yearly.sort_values('Year'), width='stretch')
                    
                    # ========== REVISED VERSION ==========
                    if 'Revised Score' in results.columns and 'Revised Profit ($)' in results.columns:
                        st.markdown("---")
                        st.subheader("**REVISED** Yearly Profit (Excluding Filtered Days)")
                        
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
                                st.dataframe(revised_yearly.sort_values('Year'), width='stretch')
                        st.markdown("---")
                    
                    # Chart
                    chart_data = yearly.set_index('Year')[['Profit']]
                    st.subheader("Profit (Points) by Year")
                    st.bar_chart(chart_data)
                    # Download
                    csv = yearly.sort_values('Year').to_csv(index=False)
                    st.download_button(
                        label="Download Yearly Profit as CSV",
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
        st.sidebar.markdown(f"• Analyzer runs folder exists: {os.path.exists(analyzer_runs_folder) if analyzer_runs_folder else False}")

if __name__ == "__main__":
    main()
