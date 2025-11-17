#!/usr/bin/env python3
"""
Streamlit Web App for Raw Data Translation - FRONTEND ONLY
UI interface for the data translator backend
"""

import streamlit as st
import pandas as pd
from pathlib import Path
import sys

# Add QTSW2 root to path for imports
QTSW2_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(QTSW2_ROOT))

# Import backend functions
from translator import (
    get_data_files,
    load_single_file,
    process_data,
    get_file_years,
    detect_data_frequency,
    get_data_type_summary
)

# Page config
st.set_page_config(
    page_title="📊 Raw Data Translator",
    page_icon="📊",
    layout="wide",
    initial_sidebar_state="expanded"
)


def main():
    """Main UI function - Frontend only"""
    st.title("📊 Raw Data Translator")
    st.markdown("Convert raw trading data exports into clean, organized files")
    
    # ==========================================
    # SIDEBAR - Configuration
    # ==========================================
    st.sidebar.header("⚙️ Configuration")
    
    input_folder = st.sidebar.text_input(
        "📁 Input Folder", 
        value="data/raw",
        help="Folder containing raw data files"
    )
    
    output_folder = st.sidebar.text_input(
        "📁 Output Folder", 
        value="data/processed",
        help="Folder where processed files will be saved"
    )
    
    # ==========================================
    # SIDEBAR - File Selection
    # ==========================================
    st.sidebar.header("📁 File Selection")
    
    # Get available files
    data_files = get_data_files(input_folder) if Path(input_folder).exists() else []
    file_names = [f.name for f in data_files] if data_files else []
    
    # Bulk process option
    process_all = st.sidebar.checkbox(
        "⚡ Process All Files (Bulk Mode)",
        value=False,
        help="Process all files in the folder automatically. When enabled, all files will be processed regardless of selection."
    )
    
    if file_names:
        if process_all:
            # In bulk mode, select all files automatically
            selected_files = file_names
            st.sidebar.success(f"✅ Bulk mode: {len(file_names)} file(s) will be processed")
        else:
            selected_files = st.sidebar.multiselect(
                "📄 Select Files to Process",
                options=file_names,
                default=[],
                help="Choose which files to translate. Select at least one file to process, or enable 'Process All Files' above."
            )
            
            if not selected_files:
                st.sidebar.warning("⚠️ No files selected. Please select files to process or enable 'Process All Files'.")
    else:
        selected_files = []
        st.sidebar.info("📁 No files found in input folder")
    
    # ==========================================
    # SIDEBAR - Processing Options
    # ==========================================
    st.sidebar.header("🔧 Processing Options")
    
    separate_years = st.sidebar.checkbox(
        "📅 Separate by years",
        value=True,
        help="Create individual files for each instrument and year (e.g., ES_2024.parquet, NQ_2024.parquet)"
    )
    
    if separate_years:
        # Get available years from selected files
        available_years = []
        if selected_files:
            # Create a progress container for year scanning
            progress_container = st.sidebar.container()
            with progress_container:
                st.write("🔍 Scanning files for years...")
                progress_bar = st.progress(0)
                status_text = st.empty()
            
            all_years = set()
            for i, filename in enumerate(selected_files):
                status_text.text(f"Scanning {filename}...")
                filepath = next((f for f in data_files if f.name == filename), None)
                if filepath:
                    file_years = get_file_years(filepath)
                    all_years.update(file_years)
                    if file_years:
                        status_text.text(f"Found years in {filename}: {sorted(file_years)}")
                progress_bar.progress((i + 1) / len(selected_files))
            
            available_years = sorted(list(all_years))
            
            # Clear the progress indicators
            progress_container.empty()
        
        if available_years:
            year_options = st.sidebar.multiselect(
                "📆 Select Years (leave empty for all)",
                options=available_years,
                default=[],
                help=f"Available years in selected files: {available_years}. Leave empty to process all years and create merged file."
            )
        else:
            year_options = []
            st.sidebar.info("📆 No years detected in selected files")
    else:
        year_options = None
    
    output_format = st.sidebar.selectbox(
        "💾 Output Format",
        options=["parquet", "csv", "both"],
        index=0,
        help="File format for output"
    )
    
    # ==========================================
    # MAIN CONTENT AREA
    # ==========================================
    col1, col2 = st.columns([2, 1])
    
    with col1:
        st.header("📊 Data Preview")
        
        # Check if input folder exists
        if not Path(input_folder).exists():
            st.warning(f"⚠️ Input folder '{input_folder}' does not exist")
            st.info("Please create the folder and add your raw data files")
        else:
            # Get data files
            data_files = get_data_files(input_folder)
            
            if not data_files:
                st.warning(f"⚠️ No data files found in '{input_folder}'")
                st.info("Supported formats: .csv, .txt, .dat")
            else:
                st.success(f"✅ Found {len(data_files)} data files")
                
                # Show file list
                st.subheader("📄 Files Found")
                file_info = []
                for filepath in data_files:
                    size_mb = filepath.stat().st_size / (1024 * 1024)
                    is_selected = "✅" if filepath.name in selected_files else "❌"
                    file_info.append({
                        "Status": is_selected,
                        "File": filepath.name,
                        "Size (MB)": f"{size_mb:.2f}",
                        "Type": filepath.suffix
                    })
                
                st.dataframe(pd.DataFrame(file_info), use_container_width=True)
                
                # Show selection summary
                st.subheader("📊 Selection Summary")
                col_a, col_b, col_c = st.columns(3)
                with col_a:
                    st.metric("Total Files", len(data_files))
                with col_b:
                    st.metric("Selected Files", len(selected_files))
                with col_c:
                    if separate_years and selected_files:
                        available_years = []
                        all_years = set()
                        for filename in selected_files:
                            filepath = next((f for f in data_files if f.name == filename), None)
                            if filepath:
                                file_years = get_file_years(filepath)
                                all_years.update(file_years)
                        available_years = sorted(list(all_years))
                        if available_years:
                            st.metric("Available Years", len(available_years))
                        else:
                            st.metric("Available Years", "Unknown")
                    else:
                        st.metric("Available Years", "N/A")
                
                # Preview selected files
                if selected_files:
                    st.subheader("🔍 Data Preview")
                    # Show preview from first selected file
                    first_selected_file = next((f for f in data_files if f.name in selected_files), None)
                    if first_selected_file:
                        preview_df = load_single_file(first_selected_file)
                        if preview_df is not None:
                            st.dataframe(preview_df.head(10), use_container_width=True)
                            
                            # Data summary
                            st.subheader("📈 Data Summary")
                            col_a, col_b, col_c, col_d = st.columns(4)
                            with col_a:
                                st.metric("Rows (Preview)", f"{len(preview_df):,}")
                            with col_b:
                                st.metric("Instruments", len(preview_df['instrument'].unique()))
                            with col_c:
                                st.metric("Date Range", f"{preview_df['timestamp'].min().strftime('%Y-%m-%d')} to {preview_df['timestamp'].max().strftime('%Y-%m-%d')}")
                            with col_d:
                                # Detect and display data frequency
                                frequency_info = get_data_type_summary(preview_df)
                                freq_label = "Tick Data" if frequency_info['is_tick'] else "Minute Data"
                                freq_value = frequency_info['frequency']
                                st.metric("Data Type", freq_label, help=f"Frequency: {freq_value}")
                    
                    # Show selected files list with years
                    st.subheader("✅ Selected Files")
                    for i, filename in enumerate(selected_files, 1):
                        filepath = next((f for f in data_files if f.name == filename), None)
                        if filepath:
                            file_years = get_file_years(filepath)
                            if file_years:
                                years_str = ", ".join(map(str, file_years))
                                st.write(f"{i}. {filename} (Years: {years_str})")
                            else:
                                st.write(f"{i}. {filename} (Years: Unknown)")
                        else:
                            st.write(f"{i}. {filename}")
                else:
                    st.info("Select files in the sidebar to see preview")
    
    with col2:
        st.header("🚀 Processing")
        
        # Show bulk mode indicator
        if process_all and file_names:
            st.info(f"⚡ **Bulk Mode Active**: {len(file_names)} file(s) will be processed")
        
        # Processing button
        button_label = "⚡ Process All Files" if process_all and file_names else "▶️ Start Processing"
        if st.button(button_label, type="primary", use_container_width=True):
            if not selected_files:
                if process_all and file_names:
                    # Process all files even if multiselect is empty
                    selected_files = file_names
                else:
                    st.error("No files selected for processing. Select files or enable 'Process All Files'.")
                    selected_files = []
            
            if selected_files:
                with st.spinner("Processing data..."):
                    # Call backend processing function
                    # Always process files separately (never merge)
                    success, result_df = process_data(
                        input_folder, 
                        output_folder, 
                        separate_years, 
                        output_format, 
                        selected_files,
                        year_options,
                        process_separately=True  # Always process separately, never merge
                    )
                
                if success:
                    st.success("✅ Processing completed successfully!")
                    
                    # Show results
                    if result_df is not None:
                        st.subheader("📊 Results")
                        st.metric("Total Rows", f"{len(result_df):,}")
                        st.metric("Date Range", f"{result_df['timestamp'].min().strftime('%Y-%m-%d')} to {result_df['timestamp'].max().strftime('%Y-%m-%d')}")
                        st.metric("Instruments", ", ".join(sorted(result_df['instrument'].unique())))
                        
                        # Show output files
                        output_path = Path(output_folder)
                        if output_path.exists():
                            output_files = list(output_path.glob("*"))
                            if output_files:
                                st.subheader("📁 Output Files")
                                for file_path in sorted(output_files):
                                    size_mb = file_path.stat().st_size / (1024 * 1024)
                                    st.write(f"📄 {file_path.name} ({size_mb:.2f} MB)")
                else:
                    st.error("❌ Processing failed")
    
    # ==========================================
    # FOOTER
    # ==========================================
    st.markdown("---")
    st.markdown("**💡 Tips:**")
    st.markdown("- **⚡ Bulk Process**: Enable 'Process All Files' checkbox to automatically process everything in the folder")
    st.markdown("- **File Selection**: Choose specific files in the sidebar to process only what you need, or use bulk mode for all files")
    st.markdown("- **Year Selection**: Select specific years for individual files only, or leave empty to process all years and create merged file")
    st.markdown("- **File Naming**: Files are named by instrument and year (e.g., `ES_2024.parquet`, `NQ_2024.parquet`)")
    st.markdown("- **Merged Files**: Only created when no specific years are selected (e.g., `ES_NQ_2024-2025.parquet`)")
    st.markdown("- **Use 'Parquet only'** for fastest processing")
    st.markdown("- **Check the output folder** for your processed files")
    st.markdown("- **Preview data** before processing to ensure correct format")


if __name__ == "__main__":
    main()
