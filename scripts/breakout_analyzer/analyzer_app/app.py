import streamlit as st
import pandas as pd
from pathlib import Path
import datetime
import time
import sys


# Add breakout_analyzer root to PYTHONPATH
ROOT = Path(__file__).resolve().parents[1]
sys.path.append(str(ROOT))
from breakout_core.engine import run_strategy
from logic.config_logic import RunParams

# Custom triggers removed - using default T1 trigger (65% of target)

# Import optimizations if available
try:
    import sys
    import os
    
    # Add optimizations directory to path
    optimizations_path = str(ROOT / "optimizations")
    if optimizations_path not in sys.path:
        sys.path.insert(0, optimizations_path)
    
    # Try to import optimization modules
    from optimized_engine_integration import OptimizedBreakoutEngine
    from ui_performance_optimizer import UIPerformanceOptimizer, PerformanceMetrics
    OPTIMIZATIONS_AVAILABLE = True
    print("✅ Optimizations loaded successfully!")
    
except ImportError as e:
    OPTIMIZATIONS_AVAILABLE = False
    print(f"⚠️ Optimizations not available: {e}")
    print(f"   Optimizations path: {str(ROOT / 'optimizations')}")
    print(f"   Path exists: {os.path.exists(str(ROOT / 'optimizations'))}")

# -------------------------------------------------------------------
# Utility: find available input parquet files
# -------------------------------------------------------------------
def list_parquet_files(base_dir="data/processed"):
    base = Path(base_dir)
    if not base.exists():
        return []
    return sorted(base.glob("*.parquet"))

# -------------------------------------------------------------------
# UI: Slot selectors
# -------------------------------------------------------------------
ALL_SLOTS = ["07:30", "08:00", "09:00", "09:30", "10:00", "10:30", "11:00"]

def main():
    st.markdown("<h1 style='white-space: nowrap;'>Optimized Breakout Strategy Analyzer</h1>", unsafe_allow_html=True)
    
    # Initialize UI performance optimizer
    ui_optimizer = None
    if OPTIMIZATIONS_AVAILABLE:
        ui_optimizer = UIPerformanceOptimizer(enable_async=True, enable_progress=True)
    
    # Performance optimization settings - Hidden UI but maintain variables for functionality
    if OPTIMIZATIONS_AVAILABLE:
        use_optimizations = True  # Always enabled
        show_performance = False  # Hidden for now
    else:
        use_optimizations = False
        show_performance = False
    
    # Separator line
    st.divider()

    # ---------------------------------------------------------------
    # Input file selector
    # ---------------------------------------------------------------
    st.header("Select Input Data File")
    parquet_files = list_parquet_files("data/processed")
    if not parquet_files:
        st.error("No Parquet files found in data/processed/. Run your data translator first.")
        return

    file_options = [f.name for f in parquet_files]
    choice = st.selectbox("Choose input parquet file:", file_options)
    in_file = Path("data/processed") / choice
    
    # Extract instrument from filename (e.g., "NQ_2006-2025.parquet" -> "NQ")
    selected_instrument = choice.split('_')[0].upper()
    
    # Year filter
    st.subheader("📅 Year Filter (Optional)")
    
    # Load data to get available years
    try:
        df_temp = pd.read_parquet(in_file)
        if 'timestamp' in df_temp.columns:
            df_temp['year'] = pd.to_datetime(df_temp['timestamp']).dt.year
            available_years = sorted(df_temp['year'].unique())
            
            selected_years = st.multiselect(
                "Select years to analyze:",
                options=available_years,
                default=[],  # Nothing selected by default
                help=f"Available years: {', '.join(map(str, available_years))}"
            )
            
            if selected_years:
                st.info(f"📅 **Year Filter Active:** Analyzing years {', '.join(map(str, selected_years))}")
            else:
                pass
        else:
            st.warning("⚠️ No timestamp column found in data. Year filtering not available.")
            selected_years = []
    except Exception as e:
        st.error(f"❌ Error reading file: {e}")
        selected_years = []

    
    
    # ---------------------------------------------------------------
    # Slot selectors (session-aware selection)
    # ---------------------------------------------------------------
    st.header("Select Slot Times")
    
    # Show session info - Hidden for now
    # st.info("📋 **Session Definitions:**\n- **S1**: 02:00-09:00 (slots: 07:30, 08:00, 09:00)\n- **S2**: 08:00-11:00 (slots: 09:30, 10:00, 10:30, 11:00)")
    
    # Select slots with session context
    selected_slots = []
    selected_s1_slots = []
    selected_s2_slots = []
    
    cols = st.columns(len(ALL_SLOTS))
    for i, slot in enumerate(ALL_SLOTS):
        # Determine which session this slot belongs to
        session = "S1" if slot in ["07:30", "08:00", "09:00"] else "S2" if slot in ["09:30", "10:00", "10:30", "11:00"] else "Unknown"
        
        if cols[i].checkbox(f"{slot} ({session})", value=False, key=f"slot_{slot}"):
            selected_slots.append(slot)
            if session == "S1":
                selected_s1_slots.append(slot)
            elif session == "S2":
                selected_s2_slots.append(slot)
    
    # Show selected slots status
    if len(selected_slots) >= 1:
        st.success(f"📌 **Selected Slots:** {', '.join(selected_slots)}")
    else:
        st.warning("⚠️ **No slots selected**")
    
    # Show session breakdown
    st.write("**Session Breakdown:**")
    col1, col2 = st.columns(2)
    with col1:
        if selected_s1_slots:
            st.write(f"• **S1**: {', '.join(selected_s1_slots)}")
        else:
            st.write("• **S1**: No slots selected")
    
    with col2:
        if selected_s2_slots:
            st.write(f"• **S2**: {', '.join(selected_s2_slots)}")
        else:
            st.write("• **S2**: No slots selected")

    st.header("Target Configuration")

    # ---------------------------------------------------------------
    # Analysis options
    # ---------------------------------------------------------------
    debug_mode = st.checkbox("Debug Mode", value=False, help="Enable detailed debug output for trade analysis")
    
    # T1 trigger: Always 65% of base target (no customization)
    
    
    # ---------------------------------------------------------------
    # Data inspection option
    # ---------------------------------------------------------------
    inspect_data = st.checkbox("Inspect Data", value=False, help="Show raw data for selected date/time")
    
    # ---------------------------------------------------------------
    # Data validation option
    # ---------------------------------------------------------------
    validate_data = st.checkbox("Validate Data Quality", value=False, help="Run automated data quality checks")
    
    # ---------------------------------------------------------------
    # Export options
    # ---------------------------------------------------------------
    st.subheader("📁 Export Options")
    col1, col2 = st.columns(2)
    
    with col1:
        save_csv = st.checkbox("Save CSV File", value=False, help="Generate CSV file with results")
    
    with col2:
        save_summary = st.checkbox("Save Summary File", value=False, help="Generate summary text file with statistics")
    
    if inspect_data:
        st.subheader("Data Inspection")
        inspect_date = st.date_input("Select Date", value=pd.to_datetime("2025-01-02").date())
        inspect_time = st.selectbox("Select Time Slot", ALL_SLOTS, index=1)  # Default to 08:00
        inspect_time_obj = pd.to_datetime(inspect_time).time()
        
        if st.button("Show Data"):
            df_inspect = pd.read_parquet(in_file)
            # Data is already in correct timezone from NinjaTrader export with bar time fix
            df_inspect['date'] = df_inspect['timestamp'].dt.date
            df_inspect['time'] = df_inspect['timestamp'].dt.time
            
            # Filter by date
            df_date = df_inspect[df_inspect['date'] == inspect_date]
            
            if len(df_date) > 0:
                st.write(f"**Data for {inspect_date}:**")
                st.write(f"Total records: {len(df_date)}")
                
                # Show data around the selected time
                df_time = df_date[df_date['time'] == inspect_time_obj]
                if len(df_time) > 0:
                    st.write(f"**Data at {inspect_time}:**")
                    st.dataframe(df_time[['timestamp', 'open', 'high', 'low', 'close', 'instrument']])
                else:
                    st.write(f"No data found at {inspect_time}")
                    # Show nearby times
                    st.write("**Nearby times:**")
                    nearby = df_date.head(10)
                    st.dataframe(nearby[['timestamp', 'open', 'high', 'low', 'close', 'instrument']])
                
                # Show range calculation for selected slot (02:00 to selected time)
                st.write(f"**Range Calculation for {inspect_time} slot (02:00 to {inspect_time}):**")
                # Calculate range like the engine does
                # Check if data has timezone info
                if df_date['timestamp'].dt.tz is not None:
                    # Data is timezone-aware, create timezone-aware timestamps
                    tz = df_date['timestamp'].dt.tz
                    start_time = pd.to_datetime(f"{inspect_date} 02:00").tz_localize(tz)
                    end_time = pd.to_datetime(f"{inspect_date} {inspect_time}").tz_localize(tz)
                else:
                    # Data is naive, create naive timestamps
                    start_time = pd.to_datetime(f"{inspect_date} 02:00")
                    end_time = pd.to_datetime(f"{inspect_date} {inspect_time}")
                
                slot_data = df_date[(df_date['timestamp'] >= start_time) & (df_date['timestamp'] < end_time)]
                if len(slot_data) > 0:
                    range_high = slot_data['high'].max()
                    range_low = slot_data['low'].min()
                    range_size = range_high - range_low
                    freeze_close = df_date[df_date['timestamp'] == end_time]['close'].iloc[-1] if len(df_date[df_date['timestamp'] == end_time]) > 0 else "N/A"
                    
                    st.write(f"Range High: {range_high}")
                    st.write(f"Range Low: {range_low}")
                    st.write(f"Range Size: {range_size}")
                    st.write(f"Freeze Close at {inspect_time}: {freeze_close}")
                    
                    # Show breakout levels (using ES tick size for display)
                    brk_long = range_high + 0.25  # ES tick size
                    brk_short = range_low - 0.25
                    st.write(f"Breakout Long: {brk_long}")
                    st.write(f"Breakout Short: {brk_short}")
            else:
                st.write(f"No data found for {inspect_date}")
    
    if validate_data:
        st.subheader("Data Quality Validation")
        if st.button("Run Validation"):
            df_validate = pd.read_parquet(in_file)
            # Data is already in correct timezone from NinjaTrader export with bar time fix
            
            # Basic checks
            st.write("**Basic Statistics:**")
            st.write(f"Total rows: {len(df_validate):,}")
            st.write(f"Date range: {df_validate['timestamp'].min()} to {df_validate['timestamp'].max()}")
            
            # Missing values
            missing = df_validate.isnull().sum()
            st.write("**Missing Values:**")
            for col, count in missing.items():
                if count > 0:
                    st.error(f"❌ {col}: {count} missing values")
                else:
                    st.success(f"✅ {col}: No missing values")
            
            # Duplicates
            duplicates = df_validate.duplicated(subset=['timestamp', 'instrument']).sum()
            if duplicates > 0:
                st.error(f"❌ {duplicates} duplicate timestamps found")
            else:
                st.success("✅ No duplicate timestamps")
            
            # Price relationships
            invalid_high_low = (df_validate['high'] < df_validate['low']).sum()
            if invalid_high_low > 0:
                st.error(f"❌ {invalid_high_low} bars where High < Low")
            else:
                st.success("✅ All bars have High >= Low")
            
            # Check 2025-01-02 range analysis
            st.write("**2025-01-02 Range Analysis:**")
            df_validate['date'] = df_validate['timestamp'].dt.date
            jan2_data = df_validate[df_validate['date'] == pd.to_datetime('2025-01-02').date()]
            
            if len(jan2_data) > 0:
                st.write(f"2025-01-02 total rows: {len(jan2_data)}")
                
                # Check 08:00 slot range (02:00 to 08:00)
                st.write("**08:00 Slot Range (02:00 to 08:00):**")
                
                # Check if data has timezone info and handle accordingly
                if jan2_data['timestamp'].dt.tz is not None:
                    # Data is timezone-aware, create timezone-aware timestamps
                    tz = jan2_data['timestamp'].dt.tz
                    start_time = pd.to_datetime('2025-01-02 02:00').tz_localize(tz)
                    end_time = pd.to_datetime('2025-01-02 08:00').tz_localize(tz)
                else:
                    # Data is naive, create naive timestamps
                    start_time = pd.to_datetime('2025-01-02 02:00')
                    end_time = pd.to_datetime('2025-01-02 08:00')
                
                slot_data = jan2_data[(jan2_data['timestamp'] >= start_time) & (jan2_data['timestamp'] < end_time)]
                
                if len(slot_data) > 0:
                    st.write(f"Range data rows: {len(slot_data)}")
                    
                    # Calculate range like the engine does
                    range_high = slot_data['high'].max()
                    range_low = slot_data['low'].min()
                    range_size = range_high - range_low
                    
                    st.write(f"**Range Calculation:**")
                    st.write(f"Range High: {range_high}")
                    st.write(f"Range Low: {range_low}")
                    st.write(f"Range Size: {range_size}")
                    
                    # Show bars with highest and lowest values
                    st.write("**Top 5 highest bars in range:**")
                    top_highs = slot_data.nlargest(5, 'high')
                    st.dataframe(top_highs[['timestamp', 'open', 'high', 'low', 'close']])
                    
                    st.write("**Top 5 lowest bars in range:**")
                    top_lows = slot_data.nsmallest(5, 'low')
                    st.dataframe(top_lows[['timestamp', 'open', 'high', 'low', 'close']])
                    
                    # Check freeze close at 08:00
                    freeze_data = jan2_data[jan2_data['timestamp'] == end_time]
                    if len(freeze_data) > 0:
                        freeze_close = freeze_data.iloc[-1]['close']
                        st.write(f"**Freeze Close at 08:00:** {freeze_close}")
                    else:
                        # Try to find close to 08:00
                        near_8am = jan2_data[jan2_data['timestamp'].dt.time == pd.to_datetime('08:00').time()]
                        if len(near_8am) > 0:
                            freeze_close = near_8am.iloc[-1]['close']
                            st.write(f"**Freeze Close near 08:00:** {freeze_close}")
                        else:
                            st.error("❌ No freeze close found at 08:00")
                    
                    # Calculate breakout levels
                    brk_long = range_high + 0.25  # ES tick size
                    brk_short = range_low - 0.25
                    st.write(f"**Breakout Levels:**")
                    st.write(f"Long Breakout: {brk_long}")
                    st.write(f"Short Breakout: {brk_short}")
                    
                    # Check if freeze close triggers immediate entry
                    if 'freeze_close' in locals():
                        if freeze_close >= brk_long:
                            st.error(f"❌ IMMEDIATE LONG ENTRY: freeze_close({freeze_close}) >= brk_long({brk_long})")
                        elif freeze_close <= brk_short:
                            st.error(f"❌ IMMEDIATE SHORT ENTRY: freeze_close({freeze_close}) <= brk_short({brk_short})")
                        else:
                            st.success(f"✅ No immediate entry: freeze_close({freeze_close}) between breakouts")
                    
                    # Check for breakouts after 08:00
                    st.write("**Post-08:00 Breakouts:**")
                    post_data = jan2_data[jan2_data['timestamp'] > end_time]
                    if len(post_data) > 0:
                        long_touches = post_data[post_data['high'] >= brk_long]
                        short_touches = post_data[post_data['low'] <= brk_short]
                        
                        st.write(f"Long touches after 08:00: {len(long_touches)}")
                        if len(long_touches) > 0:
                            first_long = long_touches.iloc[0]
                            st.write(f"First long touch: {first_long['timestamp']} H={first_long['high']}")
                        
                        st.write(f"Short touches after 08:00: {len(short_touches)}")
                        if len(short_touches) > 0:
                            first_short = short_touches.iloc[0]
                            st.write(f"First short touch: {first_short['timestamp']} L={first_short['low']}")
                        
                        if len(long_touches) > 0 and len(short_touches) > 0:
                            if long_touches.iloc[0]['timestamp'] < short_touches.iloc[0]['timestamp']:
                                st.success("✅ Long breakout happened first - explains Long entry")
                            else:
                                st.error("❌ Short breakout happened first but Long was chosen")
                else:
                    st.error("❌ No data found in 02:00-08:00 range")
            else:
                st.error("❌ No data found for 2025-01-02")
    
    
    # ---------------------------------------------------------------
    # Run button
    # ---------------------------------------------------------------
    if st.button("🚀 Run Optimized Analyzer"):
        if not selected_slots:
            st.error("Please select at least one time slot.")
            return
        
        # Start performance tracking
        metrics = None
        if ui_optimizer:
            metrics = ui_optimizer.start_performance_tracking()
            progress_bar = ui_optimizer.create_progress_bar(5, "Starting analysis...")
        else:
            progress_bar = None
        
            
        # Update progress
        if progress_bar:
            ui_optimizer.update_progress(progress_bar, 1, 5, "Loading data...")
        
        # Load data efficiently
        df = pd.read_parquet(in_file)
        if "instrument" not in df.columns:
            st.error("Input data must have an 'instrument' column.")
            return
        
        # Apply year filter if years are selected
        if selected_years:
            print(f"🔍 Applying year filter: {', '.join(map(str, selected_years))}")
            df['year'] = pd.to_datetime(df['timestamp']).dt.year
            df = df[df['year'].isin(selected_years)]
            df = df.drop('year', axis=1)  # Remove the temporary year column
            print(f"📅 Year filter applied: {len(df)} rows from years {', '.join(map(str, selected_years))}")
            st.info(f"📅 **Year Filter Applied:** Analyzing {len(df)} rows from years {', '.join(map(str, selected_years))}")
        else:
            print(f"📅 No year filter: Analyzing entire file with {len(df)} rows")
        
        # Track data loading time
        if metrics:
            metrics.data_loading_time = time.time() - metrics.start_time
        
        # Data is already in Chicago timezone - no fix needed

        instruments = df["instrument"].str.upper().unique()
        print(f"📊 Found instruments: {', '.join(instruments)}")
        all_results = []
        
        # Update progress
        if progress_bar:
            ui_optimizer.update_progress(progress_bar, 2, 5, f"Processing {len(instruments)} instruments...")
        
        # Start processing time tracking
        processing_start_time = time.time()

        for inst in instruments:
            # Determine which sessions to enable based on slot selection
            enabled_sessions = []
            if selected_s1_slots:
                enabled_sessions.append("S1")
            if selected_s2_slots:
                enabled_sessions.append("S2")
            
            if not enabled_sessions:
                st.error("Please select at least one slot for at least one session.")
                return
            
            rp = RunParams(
                instrument=inst,
                enabled_sessions=enabled_sessions,
                enabled_slots={"S1": selected_s1_slots, "S2": selected_s2_slots},
                trade_days=[0, 1, 2, 3, 4],  # Mon-Fri
                same_bar_priority="STOP_FIRST",
                write_setup_rows=False,
                write_no_trade_rows=True  # Always show NoTrade entries
            )

            print(f"🚀 Starting analysis for {inst}...")
            print(f"   Sessions: {', '.join(enabled_sessions)}")
            print(f"   Slots: S1={selected_s1_slots}, S2={selected_s2_slots}")
            print(f"   Data shape: {df.shape}")
            print(f"   Date range: {df['timestamp'].min()} to {df['timestamp'].max()}")
            print(f"   Price range: {df['low'].min()} to {df['high'].max()}")
            st.write(f"Running {inst} ...")
            
            if debug_mode:
                st.write("**Debug Output:**")
                debug_container = st.empty()
                
                # Capture debug output and show in terminal
                import io
                import sys
                from contextlib import redirect_stdout
                
                debug_output = io.StringIO()
                with redirect_stdout(debug_output):
                    if use_optimizations and OPTIMIZATIONS_AVAILABLE:
                        # Use optimized engine
                        optimized_engine = OptimizedBreakoutEngine(enable_optimizations=True, enable_parallel=True, enable_caching=True, enable_algorithms=True)
                        res = optimized_engine.run_optimized_strategy(df, rp, debug=True)
                    else:
                        # Use original engine
                        res = run_strategy(df, rp, debug=True)
                
                debug_text = debug_output.getvalue()
                if debug_text:
                    # Show debug output in terminal
                    print("=" * 60)
                    print(f"🔍 DEBUG OUTPUT FOR {inst}")
                    print("=" * 60)
                    print(debug_text)
                    print("=" * 60)
                    
                    debug_container.text_area("Debug Details", debug_text, height=400)
                else:
                    print(f"⚠️ No debug output generated for {inst}")
                    debug_container.write("No debug output generated.")
            else:
                # Always run with debug=True to show processing in terminal
                import io
                import sys
                from contextlib import redirect_stdout
                
                debug_output = io.StringIO()
                with redirect_stdout(debug_output):
                    if use_optimizations and OPTIMIZATIONS_AVAILABLE:
                        # Use optimized engine
                        optimized_engine = OptimizedBreakoutEngine(enable_optimizations=True, enable_parallel=True, enable_caching=True, enable_algorithms=True)
                        res = optimized_engine.run_optimized_strategy(df, rp, debug=True)
                    else:
                        # Use original engine
                        res = run_strategy(df, rp, debug=True)
                
                debug_text = debug_output.getvalue()
                if debug_text:
                    # Show debug output in terminal
                    print("=" * 60)
                    print(f"🔍 PROCESSING OUTPUT FOR {inst}")
                    print("=" * 60)
                    print(debug_text)
                    print("=" * 60)
            if not res.empty:
                print(f"✅ {inst} analysis completed: {len(res)} trades generated")
                print(f"   Results breakdown:")
                print(f"   - Wins: {len(res[res['Result'] == 'Win'])}")
                print(f"   - Losses: {len(res[res['Result'] == 'Loss'])}")
                print(f"   - Break-Even: {len(res[res['Result'] == 'BE'])}")
                print(f"   - No Trades: {len(res[res['Result'] == 'NoTrade'])}")
                print(f"   - Total Profit: ${res['Profit'].sum():.2f}")
                
                # Show performance metrics if enabled
                if use_optimizations and OPTIMIZATIONS_AVAILABLE:
                    print(f"   🚀 Performance: Optimizations enabled")
                    print(f"   📊 Memory: Optimized data types")
                    print(f"   ⚡ Speed: Vectorized operations")
                    print(f"   🔄 Parallel: Multi-threaded processing")
                    print(f"   💾 Caching: Smart data loading")
                    print(f"   🧮 Algorithms: Optimized core logic")
                    if show_performance:
                        print(f"   📈 Detailed metrics: Available")
                
                all_results.append(res)
            else:
                print(f"⚠️ {inst} analysis completed: No trades generated")

        # Track processing time
        if metrics:
            metrics.processing_time = time.time() - processing_start_time
        
        # Update progress
        if progress_bar:
            ui_optimizer.update_progress(progress_bar, 4, 5, "Finalizing results...")
        
        if not all_results:
            print("❌ No results generated for any instrument")
            st.warning("No results generated.")
            return

        print(f"📊 Combining results from {len(all_results)} instruments...")
        final = pd.concat(all_results, ignore_index=True)
        print(f"🎯 Total results: {len(final)} trades")
        
        # Update progress
        if progress_bar:
            ui_optimizer.update_progress(progress_bar, 5, 5, "Analysis complete!")
        
        # End performance tracking and show dashboard
        if metrics:
            metrics = ui_optimizer.end_performance_tracking(metrics)
            
            # Show performance dashboard
            if show_performance:
                ui_optimizer.create_performance_dashboard(metrics)
                ui_optimizer.create_performance_summary(metrics)


        # -----------------------------------------------------------
        # Save with descriptive filename into data/analyzer_runs/
        # -----------------------------------------------------------
        out_dir = Path("data/analyzer_runs")
        out_dir.mkdir(parents=True, exist_ok=True)
        ts = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
        
        # Create descriptive filename
        instruments = "_".join(sorted(final['Instrument'].unique()))
        date_range = f"{final['Date'].min()}_to_{final['Date'].max()}"
        total_trades = len(final)
        win_rate = (len(final[final['Result'] == 'Win']) / len(final[final['Result'].isin(['Win', 'Loss', 'BE'])]) * 100) if len(final[final['Result'].isin(['Win', 'Loss', 'BE'])]) > 0 else 0
        total_profit = final['Profit'].sum()
        
        # Create descriptive filename
        desc_filename = f"{instruments}_{date_range}_{total_trades}trades_{win_rate:.0f}winrate_{total_profit:.0f}profit_{ts}"
        
        parquet_path = out_dir / f"{desc_filename}.parquet"
        csv_path = out_dir / f"{desc_filename}.csv"
        summary_path = out_dir / f"{desc_filename}_SUMMARY.txt"

        print(f"💾 Saving results to files...")
        
        if save_csv:
            print(f"   📄 CSV: {csv_path.name}")
        
        if save_summary:
            print(f"   📋 Summary: {summary_path.name}")
        
        # Save parquet file - ensure proper data types
        # Convert string columns to proper types to avoid PyArrow conversion issues
        final_copy = final.copy()
        
        # Hide internal columns from output
        columns_to_hide = ['_sortTime', '_rank']
        for col in columns_to_hide:
            if col in final_copy.columns:
                final_copy = final_copy.drop(columns=[col])
        
        # Ensure Result column is string type
        if 'Result' in final_copy.columns:
            final_copy['Result'] = final_copy['Result'].astype(str)
        
        # Ensure other string columns are properly typed
        string_columns = ['Direction', 'Instrument', 'Session', 'Date', 'Time']
        for col in string_columns:
            if col in final_copy.columns:
                final_copy[col] = final_copy[col].astype(str)
        
        final_copy.to_parquet(parquet_path, index=False)
        
        # Save CSV with descriptive header (only if requested)
        if save_csv:
            with open(csv_path, 'w', encoding='utf-8') as f:
                # Write header with run information
                f.write(f"# BREAKOUT ANALYZER RESULTS\n")
                f.write(f"# Generated: {datetime.datetime.now().strftime('%Y-%m-%d %H:%M:%S')}\n")
                f.write(f"# Instruments: {instruments}\n")
                f.write(f"# Date Range: {date_range}\n")
                f.write(f"# Total Trades: {total_trades:,}\n")
                f.write(f"# Win Rate: {win_rate:.1f}%\n")
                f.write(f"# Total Profit: ${total_profit:,.2f}\n")
                f.write(f"# Sessions: {', '.join(rp.enabled_sessions)}\n")
                f.write(f"# Time Slots: {', '.join([f'{sess}:{time}' for sess in rp.enabled_sessions for time in rp.enabled_slots.get(sess, [])])}\n")
                f.write(f"# Trade Days: {', '.join([['Mon','Tue','Wed','Thu','Fri'][d] for d in rp.trade_days])}\n")
                f.write(f"#\n")
                
                # Write the actual data
                final.to_csv(f, index=False, encoding='utf-8')
        
        # Create summary file (only if requested)
        if save_summary:
            with open(summary_path, 'w') as f:
                f.write("BREAKOUT ANALYZER RESULTS SUMMARY\n")
                f.write("=" * 50 + "\n\n")
                f.write(f"Run Information:\n")
                f.write(f"  Generated: {datetime.datetime.now().strftime('%Y-%m-%d %H:%M:%S')}\n")
                f.write(f"  Instruments: {instruments}\n")
                f.write(f"  Date Range: {date_range}\n")
                f.write(f"  Sessions: {', '.join(rp.enabled_sessions)}\n")
                f.write(f"  Time Slots: {', '.join([f'{sess}:{time}' for sess in rp.enabled_sessions for time in rp.enabled_slots.get(sess, [])])}\n")
                f.write(f"  Trade Days: {', '.join([['Mon','Tue','Wed','Thu','Fri'][d] for d in rp.trade_days])}\n")
                
                f.write(f"\nConfiguration Settings:\n")
                f.write(f"  Debug Mode: {debug_mode}\n")
                f.write(f"  T1 Trigger: 65% of base target (fixed)\n")
                f.write(f"  Use Optimizations: {use_optimizations}\n")
                f.write(f"  Show Performance Metrics: {show_performance}\n")
                
                # Add year filter info if applied
                if selected_years:
                    f.write(f"\nYear Filter:\n")
                    f.write(f"  Selected Years: {', '.join(map(str, selected_years))}\n")
                
                # Add performance metrics if available
                if metrics:
                    f.write(f"\nPerformance Metrics:\n")
                    f.write(f"  Data Loading Time: {metrics.data_loading_time:.2f} seconds\n")
                    f.write(f"  Processing Time: {metrics.processing_time:.2f} seconds\n")
                    f.write(f"  Total Execution Time: {metrics.total_time:.2f} seconds\n")
                    f.write(f"  Memory Usage: {metrics.memory_usage_mb:.1f} MB\n")
                
                f.write(f"Performance Summary:\n")
                f.write(f"  Total Trades: {total_trades:,}\n")
                f.write(f"  Wins: {len(final[final['Result'] == 'Win']):,}\n")
                f.write(f"  Losses: {len(final[final['Result'] == 'Loss']):,}\n")
                f.write(f"  Break-Even: {len(final[final['Result'] == 'BE']):,}\n")
                f.write(f"  No Trades: {len(final[final['Result'] == 'NoTrade']):,}\n")
                f.write(f"  Win Rate: {win_rate:.1f}%\n")
                f.write(f"  Total Profit: ${total_profit:,.2f}\n")
                f.write(f"  Average Profit per Trade: ${total_profit/total_trades:,.2f}\n\n")
                
                # By instrument breakdown
                f.write(f"By Instrument:\n")
                for instrument in final['Instrument'].unique():
                    inst_data = final[final['Instrument'] == instrument]
                    inst_profit = inst_data['Profit'].sum()
                    inst_trades = len(inst_data)
                    inst_wins = len(inst_data[inst_data['Result'] == 'Win'])
                    inst_actual_trades = len(inst_data[inst_data['Result'].isin(['Win', 'Loss', 'BE'])])
                    inst_win_rate = (inst_wins / inst_actual_trades * 100) if inst_actual_trades > 0 else 0
                    f.write(f"  {instrument}: {inst_trades:,} trades, {inst_win_rate:.1f}% win rate, ${inst_profit:,.2f} profit\n")
                
                # By target breakdown
                f.write(f"\nBy Target Points:\n")
                target_stats = final.groupby('Target').agg({
                    'Result': lambda x: (x == 'Win').sum() / len(x) * 100,
                    'Profit': ['sum', 'count']
                }).round(1)
                target_stats.columns = ['Win_Rate_%', 'Total_Profit', 'Trade_Count']
                for target in sorted(final['Target'].unique()):
                    row = target_stats.loc[target]
                    f.write(f"  {target} points: {row['Trade_Count']:.0f} trades, {row['Win_Rate_%']:.1f}% win rate, ${row['Total_Profit']:.2f} profit\n")

        print(f"🎉 Analysis completed successfully!")
        print(f"📈 Performance: {total_trades} trades, {win_rate:.1f}% win rate, ${total_profit:,.2f} profit")
        print(f"📁 Files saved to data/analyzer_runs/ folder")
        print("=" * 60)
        
        st.success(f"✅ Analysis completed successfully!")
        st.info(f"📁 Files saved to analyzer_runs folder")
        st.success(f"💾 Parquet file saved to analyzer_runs: {parquet_path.name}")
        
        if save_csv:
            st.success(f"📄 CSV saved to analyzer_runs: {csv_path.name}")
        
        if save_summary:
            st.success(f"📋 Summary saved to analyzer_runs: {summary_path.name}")
        
        
        
        # Results Summary
        st.header("📊 Results Summary")
        
        if len(final) > 0:
            col1, col2, col3, col4 = st.columns(4)
            
            with col1:
                total_trades = len(final)
                st.metric("Total Trades", total_trades)
            
            with col2:
                wins = len(final[final['Result'] == 'Win'])
                win_rate = (wins / total_trades * 100) if total_trades > 0 else 0
                st.metric("Win Rate", f"{win_rate:.1f}%")
            
            with col3:
                total_profit = final['Profit'].sum()
                st.metric("Total Profit", f"{total_profit:.1f} pts")
            
            with col4:
                avg_profit = final['Profit'].mean()
                st.metric("Avg Profit", f"{avg_profit:.1f} pts")
            
            # Level-specific results
            if 'Target' in final.columns:
                st.subheader("Results by Level")
                level_summary = final.groupby('Target').agg({
                    'Result': ['count', lambda x: (x == 'Win').sum()],
                    'Profit': ['sum', 'mean']
                }).round(1)
                level_summary.columns = ['Total Trades', 'Wins', 'Total Profit', 'Avg Profit']
                level_summary['Win Rate %'] = (level_summary['Wins'] / level_summary['Total Trades'] * 100).round(1)
                st.dataframe(level_summary, width='stretch')
        
        st.header("📋 Detailed Results")
        
        # Show metrics above the table
        col1, col2, col3, col4 = st.columns(4)
        with col1:
            st.metric("Rows Shown", min(100, len(final)))
        with col2:
            st.metric("Total Rows", len(final))
        with col3:
            st.metric("Columns", len(final.columns))
        with col4:
            st.metric("Memory", f"{final.memory_usage(deep=True).sum() / 1024 / 1024:.1f} MB")
        
        # Full width detailed results table with maximum width
        st.markdown("""
        <style>
        .stDataFrame {
            width: 100% !important;
            max-width: none !important;
        }
        .stDataFrame > div {
            width: 100% !important;
            max-width: none !important;
        }
        .stDataFrame table {
            width: 100% !important;
            table-layout: auto !important;
        }
        </style>
        """, unsafe_allow_html=True)
        
        # Try both approaches for maximum width
        st.dataframe(
            final.head(100), 
            width='stretch',
            height=600
        )
        
        # Save to analyzer_runs folder button
        if st.button("💾 Save to analyzer_runs folder", help="Save results to the analyzer_runs folder with timestamp"):
            try:
                # Create timestamp for filename
                timestamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
                
                # Generate filename based on analysis parameters
                instrument = final['Instrument'].iloc[0] if len(final) > 0 else "UNKNOWN"
                total_trades = len(final)
                wins = len(final[final['Result'] == 'Win']) if len(final) > 0 else 0
                win_rate = (wins / total_trades * 100) if total_trades > 0 else 0
                total_profit = final['Profit'].sum() if len(final) > 0 else 0
                
                # Create filename
                filename = f"{instrument}_{total_trades}trades_{int(win_rate)}winrate_{int(total_profit)}profit_{timestamp}"
                
                # Save as parquet
                parquet_path = Path("data/analyzer_runs") / f"{filename}.parquet"
                parquet_path.parent.mkdir(parents=True, exist_ok=True)
                final.to_parquet(parquet_path, index=False)
                
                # Save as CSV
                csv_path = Path("data/analyzer_runs") / f"{filename}.csv"
                final.to_csv(csv_path, index=False)
                
                # Save summary
                summary_path = Path("data/analyzer_runs") / f"{filename}_SUMMARY.txt"
                with open(summary_path, 'w') as f:
                    f.write(f"Analysis Summary - {timestamp}\n")
                    f.write("=" * 50 + "\n")
                    f.write(f"Instrument: {instrument}\n")
                    f.write(f"Total Trades: {total_trades}\n")
                    f.write(f"Win Rate: {win_rate:.1f}%\n")
                    f.write(f"Total Profit: {total_profit:.1f} points\n")
                    f.write(f"Average Profit: {total_profit/total_trades if total_trades > 0 else 0:.1f} points\n")
                    
                    f.write(f"\nConfiguration Settings:\n")
                    f.write(f"  Debug Mode: {debug_mode}\n")
                    f.write(f"  T1 Trigger: 65% of base target (fixed)\n")
                    f.write(f"  Use Optimizations: {use_optimizations}\n")
                    f.write(f"  Show Performance Metrics: {show_performance}\n")
                    
                    # Add year filter info if applied
                    if selected_years:
                        f.write(f"\nYear Filter:\n")
                        f.write(f"  Selected Years: {', '.join(map(str, selected_years))}\n")
                    
                    # Add performance metrics if available
                    if metrics:
                        f.write(f"\nPerformance Metrics:\n")
                        f.write(f"  Data Loading Time: {metrics.data_loading_time:.2f} seconds\n")
                        f.write(f"  Processing Time: {metrics.processing_time:.2f} seconds\n")
                        f.write(f"  Total Execution Time: {metrics.total_time:.2f} seconds\n")
                        f.write(f"  Memory Usage: {metrics.memory_usage_mb:.1f} MB\n")
                    
                    f.write(f"\nFiles saved:\n")
                    f.write(f"- {parquet_path}\n")
                    f.write(f"- {csv_path}\n")
                    f.write(f"- {summary_path}\n")
                
                st.success(f"✅ Results saved to analyzer_runs folder!")
                st.info(f"📁 Files created:\n- {parquet_path.name}\n- {csv_path.name}\n- {summary_path.name}")
                
            except Exception as e:
                st.error(f"❌ Error saving files: {str(e)}")

        # Optional: Download button for CSV
        st.download_button(
            "Download Results as CSV",
            final.to_csv(index=False, encoding='utf-8').encode("utf-8"),
            file_name=csv_path.name,
            mime="text/csv"
        )

if __name__ == "__main__":
    main()  # Restart to clear cache
