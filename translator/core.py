"""
Core Processing Logic
Main data transformation and processing functions
"""

from pathlib import Path
from typing import Optional, List, Dict, Any
import pandas as pd
import re


def root_symbol(contract: str) -> str:
    """
    Extract root instrument from contract name
    
    Args:
        contract: Contract name string
        
    Returns:
        Root instrument symbol (ES, NQ, etc.)
    """
    if "MinuteDataExport_ES" in contract:
        return "ES"
    elif "MinuteDataExport_NQ" in contract:
        return "NQ"
    elif "MinuteDataExport_YM" in contract:
        return "YM"
    elif "MinuteDataExport_CL" in contract:
        return "CL"
    elif "MinuteDataExport_NG" in contract:
        return "NG"
    elif "MinuteDataExport_GC" in contract:
        return "GC"
    else:
        match = re.match(r"([A-Za-z]+)", contract)
        return match.group(1).upper() if match else contract.upper()


def infer_contract_from_filename(filepath: Path) -> str:
    """
    Extract contract name from filename
    
    Args:
        filepath: Path object
        
    Returns:
        Contract name without extension
    """
    filename = filepath.name
    name_without_ext = filename.rsplit('.', 1)[0]
    return name_without_ext


def process_data(
    input_folder: str,
    output_folder: str,
    separate_years: bool,
    output_format: str,
    selected_files: Optional[List[str]] = None,
    selected_years: Optional[List[int]] = None
) -> tuple[bool, Optional[pd.DataFrame]]:
    """
    Main data processing function
    
    Args:
        input_folder: Folder containing raw data files
        output_folder: Folder to save processed files
        separate_years: Whether to create separate files per year
        output_format: "parquet", "csv", or "both"
        selected_files: List of filenames to process (None = all files)
        selected_years: List of years to process (None = all years)
        
    Returns:
        Tuple of (success: bool, result_dataframe: Optional[DataFrame])
        
    Note:
        This function doesn't use Streamlit widgets - it's pure backend.
        Use progress callbacks if needed for UI updates.
    """
    try:
        from .file_loader import get_data_files, load_single_file
        
        # Get data files
        all_data_files = get_data_files(input_folder)
        if not all_data_files:
            raise ValueError(f"No data files found in {input_folder}")
        
        # Filter by selected files if provided
        if selected_files:
            data_files = [f for f in all_data_files if f.name in selected_files]
            if not data_files:
                raise ValueError("No selected files found")
        else:
            data_files = all_data_files
        
        # Load selected files
        dfs = []
        for i, filepath in enumerate(data_files):
            print(f"Loading {filepath.name}... ({i+1}/{len(data_files)})")
            df = load_single_file(filepath)
            if df is not None:
                dfs.append(df)
        
        if not dfs:
            raise ValueError("No files could be loaded successfully")
        
        # Combine data
        print("Combining data...")
        combined_df = pd.concat(dfs, ignore_index=True)
        combined_df = combined_df.sort_values("timestamp").reset_index(drop=True)
        
        # Remove duplicates
        print("Removing duplicates...")
        initial_count = len(combined_df)
        combined_df = combined_df.drop_duplicates(
            subset=["timestamp", "instrument"], 
            keep="first"
        ).reset_index(drop=True)
        final_count = len(combined_df)
        duplicates_removed = initial_count - final_count
        if duplicates_removed > 0:
            print(f"Removed {duplicates_removed:,} duplicate rows")
        
        # Check if contract rollover is needed (only if multiple contracts detected)
        from .contract_rollover import needs_rollover, create_continuous_series
        
        if needs_rollover(combined_df):
            print("Detected multiple contract months - creating continuous series...")
            combined_df = create_continuous_series(
                combined_df,
                rollover_days_before_exp=14,
                back_adjust=True
            )
            print("Continuous series created with back-adjustment")
        else:
            print("Single contract per instrument detected - skipping rollover")
        
        # Create output folder
        output_path = Path(output_folder)
        output_path.mkdir(exist_ok=True)
        
        if separate_years:
            print("Separating by year and instrument...")
            combined_df['year'] = combined_df['timestamp'].dt.year
            
            if selected_years:
                years = [int(y) for y in selected_years if str(y) in str(combined_df['year'].unique())]
                if not years:
                    raise ValueError(f"No data found for selected years: {selected_years}")
            else:
                years = sorted(combined_df['year'].unique())
            
            # Get unique instruments
            instruments = sorted(combined_df['instrument'].unique())
            
            for year in years:
                for instrument in instruments:
                    # Filter by year and instrument
                    year_instrument_data = combined_df[
                        (combined_df['year'] == year) & 
                        (combined_df['instrument'] == instrument)
                    ].copy()
                    
                    if len(year_instrument_data) > 0:
                        year_instrument_data = year_instrument_data.drop(columns=['year'])
                        
                        if output_format in ["parquet", "both"]:
                            parquet_file = output_path / f"{instrument}_{year}.parquet"
                            year_instrument_data.to_parquet(parquet_file, index=False)
                            print(f"Saved: {parquet_file}")
                        
                        if output_format in ["csv", "both"]:
                            csv_file = output_path / f"{instrument}_{year}.csv"
                            year_instrument_data.to_csv(csv_file, index=False)
                            print(f"Saved: {csv_file}")
            
            # Only save complete dataset if no specific years were selected
            if not selected_years:
                complete_data = combined_df.drop(columns=['year'])
            else:
                complete_data = None
        else:
            complete_data = combined_df
        
        # Save complete dataset only if we have complete data and no specific years selected
        if complete_data is not None:
            print("Saving complete dataset...")
            
            # Get date range for filename
            start_year = complete_data['timestamp'].min().year
            end_year = complete_data['timestamp'].max().year
            instruments_list = "_".join(sorted(complete_data['instrument'].unique()))
            
            if output_format in ["parquet", "both"]:
                if start_year == end_year:
                    complete_parquet = output_path / f"{instruments_list}_{start_year}.parquet"
                else:
                    complete_parquet = output_path / f"{instruments_list}_{start_year}-{end_year}.parquet"
                complete_data.to_parquet(complete_parquet, index=False)
                print(f"Saved: {complete_parquet}")
            
            if output_format in ["csv", "both"]:
                if start_year == end_year:
                    complete_csv = output_path / f"{instruments_list}_{start_year}.csv"
                else:
                    complete_csv = output_path / f"{instruments_list}_{start_year}-{end_year}.csv"
                complete_data.to_csv(complete_csv, index=False)
                print(f"Saved: {complete_csv}")
        
        print("Processing complete!")
        return True, combined_df
        
    except Exception as e:
        print(f"Processing error: {e}")
        return False, None

