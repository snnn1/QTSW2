#region Using declarations
using System;
using System.IO;
using System.Globalization;
using System.Text;
using NinjaTrader.NinjaScript;
using NinjaTrader.Data;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class DataExporter : Indicator
    {
        private StreamWriter sw;
        private string filePath;
        private bool headerWritten = false;
        private DateTime lastExportTime = DateTime.MinValue;
        private int totalBarsProcessed = 0;
        private int gapsDetected = 0;
        private int invalidDataSkipped = 0;
        private long maxFileSize = 500 * 1024 * 1024; // 500MB limit
        private bool isTickChart = false;
        private bool isMinuteChart = false;
        
        // Export control flags
        private bool exportInProgress = false;  // Instance flag - each chart exports independently
        private bool exportCompleted = false;  // Track if export has completed for this instance
        private static bool autoExportTriggered = false;  // Static to ensure one auto-export per session
        private static DateTime lastAutoExportDate = DateTime.MinValue;  // Track last auto-export date
        private bool exportStarted = false;  // Instance flag to track if export was initiated
        private TimeZoneInfo centralTimeZone;
        
        // Property for manual export trigger (accessible via indicator properties)
        private bool manualExportTrigger = false;
        private bool lastManualExportTrigger = false;
        
        // Property for trigger on open (starts export immediately when indicator loads)
        private bool triggerOnOpen = true;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "DataExporter";
                Calculate = Calculate.OnBarClose;
                IsOverlay = false;
                Description = "Exports minute OR tick data to CSV with validation, gap detection, auto-trigger at 07:00 CT, trigger on open, and manual export trigger";
                ManualExportTrigger = false;
                TriggerOnOpen = true;
            }
            else if (State == State.Configure)
            {
                // Initialize Central Time timezone
                try
                {
                    centralTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
                }
                catch (Exception ex)
                {
                    Print($"WARNING: Could not find Central Standard Time timezone: {ex.Message}");
                    Print("Using system local timezone as fallback.");
                    centralTimeZone = TimeZoneInfo.Local;
                }
            }
            else if (State == State.Active)
            {
                Print("DataExporter initialized.");
                if (TriggerOnOpen)
                {
                    Print("Trigger On Open: ENABLED - Export will start automatically when historical data loads.");
                }
                else
                {
                    Print("Auto-export will trigger at 07:00 CT.");
                }
                Print("To manually trigger export:");
                Print("  1. Right-click indicator → Properties");
                Print("  2. Check 'Manual Export Trigger' checkbox");
                Print("  3. Click OK (export will start on next bar update)");
            }
            else if (State == State.Historical)
            {
                // Detect chart type
                isTickChart = (BarsPeriod.BarsPeriodType == BarsPeriodType.Tick);
                isMinuteChart = (BarsPeriod.BarsPeriodType == BarsPeriodType.Minute && BarsPeriod.Value == 1);
                
                // Validate chart type
                if (!isTickChart && !isMinuteChart)
                {
                    Print("ERROR: This indicator only works on 1-minute charts OR Tick charts!");
                    return;
                }
                
                // Check for trigger on open first (if enabled)
                // Only trigger if export hasn't completed yet (prevents duplicate exports on data refresh)
                if (TriggerOnOpen && !exportInProgress && !exportCompleted)
                {
                    Print("Trigger On Open: Starting export immediately...");
                    StartExport();
                }
                else if (!TriggerOnOpen && !exportCompleted)
                {
                    // Check for auto-trigger at 07:00 CT (only if not already triggered this session)
                    CheckAutoExportTrigger();
                }
                else if (exportCompleted)
                {
                    Print("Export already completed for this session - skipping trigger.");
                }
            }
            else if (State == State.Terminated)
            {
                // Close file if export was running
                if (sw != null)
                {
                    try
                    {
                        sw.Flush();
                        sw.Close();
                        sw.Dispose();
                        
                        Print($"=== EXPORT COMPLETE ===");
                        Print($"File: {filePath}");
                        Print($"Data Type: {(isTickChart ? "Tick" : "Minute")}");
                        Print($"Total bars/ticks processed: {totalBarsProcessed:N0}");
                        Print($"Gaps detected: {gapsDetected}");
                        Print($"Invalid data skipped: {invalidDataSkipped}");
                        Print($"Export completed successfully!");
                        
                        // Report final file size
                        long finalFileSize = 0;
                        if (File.Exists(filePath))
                        {
                            FileInfo fileInfo = new FileInfo(filePath);
                            finalFileSize = fileInfo.Length;
                            Print($"Final file size: {finalFileSize / (1024 * 1024):F1} MB");
                        }
                        
                        // 🔧 Create completion signal file for pipeline conductor (in logs subfolder)
                        try
                        {
                            string logsPath = Path.Combine(Path.GetDirectoryName(filePath), "logs");
                            Directory.CreateDirectory(logsPath); // Ensure logs folder exists
                            string completionSignalPath = Path.Combine(logsPath, 
                                $"export_complete_{Path.GetFileNameWithoutExtension(filePath)}.json");
                            
                            string instrument = Instrument?.MasterInstrument?.Name ?? "UNKNOWN";
                            string completionDataType = isTickChart ? "Tick" : "Minute";
                            string fileName = Path.GetFileName(filePath);
                            string completedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                            double fileSizeMB = finalFileSize / (1024.0 * 1024.0);
                            
                            // Write simple JSON manually (no external library needed)
                            StringBuilder json = new StringBuilder();
                            json.AppendLine("{");
                            json.AppendLine($"  \"status\": \"complete\",");
                            json.AppendLine($"  \"filePath\": \"{EscapeJsonString(filePath)}\",");
                            json.AppendLine($"  \"fileName\": \"{EscapeJsonString(fileName)}\",");
                            json.AppendLine($"  \"dataType\": \"{completionDataType}\",");
                            json.AppendLine($"  \"instrument\": \"{EscapeJsonString(instrument)}\",");
                            json.AppendLine($"  \"totalBarsProcessed\": {totalBarsProcessed},");
                            json.AppendLine($"  \"gapsDetected\": {gapsDetected},");
                            json.AppendLine($"  \"invalidDataSkipped\": {invalidDataSkipped},");
                            json.AppendLine($"  \"fileSizeBytes\": {finalFileSize},");
                            json.AppendLine($"  \"fileSizeMB\": {fileSizeMB:F2},");
                            json.AppendLine($"  \"completedAt\": \"{completedAt}\"");
                            json.AppendLine("}");
                            
                            File.WriteAllText(completionSignalPath, json.ToString());
                            Print($"Completion signal created: {completionSignalPath}");
                        }
                        catch (Exception ex)
                        {
                            Print($"WARNING: Could not create completion signal: {ex.Message}");
                        }
                        
                        // Remove progress file if it exists (from logs subfolder)
                        try
                        {
                            string logsPath = Path.Combine(Path.GetDirectoryName(filePath), "logs");
                            string progressFilePath = Path.Combine(logsPath, 
                                $"export_progress_{Path.GetFileNameWithoutExtension(filePath)}.json");
                            if (File.Exists(progressFilePath))
                            {
                                File.Delete(progressFilePath);
                            }
                        }
                        catch (Exception ex)
                        {
                            Print($"WARNING: Could not remove progress file: {ex.Message}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Print($"ERROR closing file: {ex.Message}");
                    }
                    finally
                    {
                        // Reset export flags (instance-level, so each chart resets independently)
                        exportInProgress = false;
                        exportStarted = false;
                        exportCompleted = true;  // Mark as completed to prevent re-triggering on data refresh
                    }
                }
            }
        }
        
        // 🔧 Check for external trigger file from conductor
        private void CheckExternalTrigger()
        {
            try
            {
                string qtsw2Path = @"C:\Users\jakej\QTSW2\data\raw";
                string triggerFile = Path.Combine(qtsw2Path, "export_trigger.txt");
                
                if (File.Exists(triggerFile))
                {
                    // Read trigger file to get run_id
                    string triggerContent = File.ReadAllText(triggerFile).Trim();
                    
                    // Delete trigger file immediately to prevent re-triggering
                    try
                    {
                        File.Delete(triggerFile);
                    }
                    catch (Exception ex)
                    {
                        Print($"WARNING: Could not delete trigger file: {ex.Message}");
                    }
                    
                    // Trigger export
                    if (!exportInProgress && State == State.Historical)
                    {
                        Print($"External trigger detected (Run ID: {triggerContent}). Starting export...");
                        StartExport();
                    }
                    else if (exportInProgress)
                    {
                        Print("External trigger detected but export already in progress - ignoring.");
                    }
                    else
                    {
                        Print("External trigger detected but not in historical state - export will start when historical data loads.");
                    }
                }
            }
            catch (Exception ex)
            {
                Print($"WARNING: Error checking external trigger: {ex.Message}");
            }
        }
        
        // Check if current time is between 07:00 and 07:05 CT, trigger export if so
        private void CheckAutoExportTrigger()
        {
            try
            {
                // Get current time in Central Time
                DateTime nowUtc = DateTime.UtcNow;
                DateTime nowCentral = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, centralTimeZone);
                
                // Reset flag if it's a new day
                if (lastAutoExportDate.Date != nowCentral.Date)
                {
                    autoExportTriggered = false;
                    lastAutoExportDate = nowCentral.Date;
                }
                
                // Check if already triggered today
                if (autoExportTriggered && lastAutoExportDate.Date == nowCentral.Date)
                {
                    return; // Already triggered today
                }
                
                TimeSpan currentTime = nowCentral.TimeOfDay;
                TimeSpan triggerStart = new TimeSpan(7, 0, 0);  // 07:00:00
                TimeSpan triggerEnd = new TimeSpan(7, 5, 0);    // 07:05:00
                
                if (currentTime >= triggerStart && currentTime <= triggerEnd)
                {
                    Print($"Auto export starting at 07:00 CT... (Current CT time: {nowCentral:yyyy-MM-dd HH:mm:ss})");
                    autoExportTriggered = true;  // Set flag to prevent multiple triggers today
                    lastAutoExportDate = nowCentral.Date;
                    StartExport();
                }
            }
            catch (Exception ex)
            {
                Print($"WARNING: Auto-export time check failed: {ex.Message}");
            }
        }
        
        // Helper method to escape JSON strings
        private string EscapeJsonString(string input)
        {
            if (string.IsNullOrEmpty(input))
                return "";
            
            return input.Replace("\\", "\\\\")
                       .Replace("\"", "\\\"")
                       .Replace("\n", "\\n")
                       .Replace("\r", "\\r")
                       .Replace("\t", "\\t");
        }
        
        // Manual export trigger from button click
        private void TriggerManualExport()
        {
            if (exportInProgress)
            {
                Print("Export already in progress — skipping.");
                return;
            }
            
            Print("Manual export triggered by user button.");
            StartExport();
        }
        
        // Main export method - wraps all existing export logic
        private void StartExport()
        {
            // Safety guard: prevent multiple exports
            if (exportInProgress)
            {
                Print("Export already in progress — skipping.");
                return;
            }
            
            // Check if we're in historical state
            if (State != State.Historical)
            {
                Print("WARNING: Export can only run during historical data processing.");
                return;
            }
            
            // Validate chart type
            if (!isTickChart && !isMinuteChart)
            {
                Print("ERROR: Cannot start export - invalid chart type!");
                return;
            }
            
            // Check for recently created files or in-progress exports for this instrument (prevent duplicates on chart reload)
            string instrumentName = Instrument?.MasterInstrument?.Name ?? "UNKNOWN";
            if (instrumentName.Contains(" "))
            {
                instrumentName = instrumentName.Split(' ')[0];
            }
            
            string qtsw2Path = @"C:\Users\jakej\QTSW2\data\raw";
            string logsPath = Path.Combine(qtsw2Path, "logs");
            
            // First check for progress files (export currently in progress)
            if (Directory.Exists(logsPath))
            {
                string progressPattern = $"export_progress_DataExport_{instrumentName}_*.json";
                var progressFiles = Directory.GetFiles(logsPath, progressPattern);
                if (progressFiles.Length > 0)
                {
                    // Check if progress file was updated recently (within last 5 minutes)
                    foreach (string progressFile in progressFiles)
                    {
                        FileInfo progressInfo = new FileInfo(progressFile);
                        TimeSpan timeSinceUpdate = DateTime.Now - progressInfo.LastWriteTime;
                        
                        if (timeSinceUpdate.TotalSeconds < 300) // Less than 5 minutes since last update
                        {
                            Print($"Export already in progress for {instrumentName} (progress file updated {timeSinceUpdate.TotalSeconds:F0} seconds ago) - skipping duplicate export.");
                            return; // Skip export if one is already running
                        }
                    }
                }
            }
            
            // Check if a CSV file was created in the last 2 minutes (120 seconds)
            string pattern = $"DataExport_{instrumentName}_*.csv";
            var existingFiles = Directory.GetFiles(qtsw2Path, pattern);
            
            foreach (string existingFile in existingFiles)
            {
                FileInfo fileInfo = new FileInfo(existingFile);
                TimeSpan timeSinceCreation = DateTime.Now - fileInfo.CreationTime;
                
                if (timeSinceCreation.TotalSeconds < 120) // Less than 2 minutes old
                {
                    Print($"Recent export file found for {instrumentName} (created {timeSinceCreation.TotalSeconds:F0} seconds ago) - skipping duplicate export.");
                    Print($"Existing file: {fileInfo.Name}");
                    return; // Skip export if file was created recently
                }
            }
            
            exportInProgress = true;
            exportStarted = true;
            
            // Reset export tracking variables
            headerWritten = false;
            lastExportTime = DateTime.MinValue;
            totalBarsProcessed = 0;
            gapsDetected = 0;
            invalidDataSkipped = 0;
            
            // 🔧 Export to QTSW2 raw_data folder for pipeline integration
            // (qtsw2Path and logsPath already declared above)
            Directory.CreateDirectory(qtsw2Path); // Ensure folder exists (creates if doesn't exist)
            Directory.CreateDirectory(logsPath); // Ensure logs folder exists
            
            // 🔧 Create export start signal file
            string startDataType = isTickChart ? "Tick" : "Minute";
            try
            {
                string startSignalPath = Path.Combine(logsPath, $"export_start_{DateTime.Now:yyyyMMdd_HHmmss}.json");
                string instrument = Instrument?.MasterInstrument?.Name ?? "UNKNOWN";
                string startedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                
                // Write simple JSON manually (no external library needed)
                StringBuilder json = new StringBuilder();
                json.AppendLine("{");
                json.AppendLine($"  \"status\": \"started\",");
                json.AppendLine($"  \"instrument\": \"{EscapeJsonString(instrument)}\",");
                json.AppendLine($"  \"dataType\": \"{startDataType}\",");
                json.AppendLine($"  \"startedAt\": \"{startedAt}\"");
                json.AppendLine("}");
                
                File.WriteAllText(startSignalPath, json.ToString());
            }
            catch (Exception ex)
            {
                Print($"WARNING: Could not create start signal: {ex.Message}");
            }
            
            // (instrumentName already declared and processed above)
            
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string exportDataType = isTickChart ? "Tick" : "Minute";
            
            filePath = Path.Combine(qtsw2Path, $"DataExport_{instrumentName}_{timestamp}_UTC.csv");
            
            Print($"Starting export to: {filePath}");
            Print($"Instrument: {instrumentName}");
            Print($"Data Type: {exportDataType}");
            Print($"Data Range: {Bars.GetTime(0)} to {Bars.GetTime(Count-1)}");
            
            // Create file with exception handling
            try
            {
                sw = new StreamWriter(filePath, false);
                // Remove AutoFlush for better performance - we'll flush manually
                Print($"File created successfully: {filePath}");
            }
            catch (Exception ex)
            {
                Print($"ERROR: Cannot create file {filePath}: {ex.Message}");
                Print($"Please check folder permissions and try again.");
                exportInProgress = false;
                exportStarted = false;
                return;
            }
            
            // Export will continue in OnBarUpdate() for each bar
        }

        protected override void OnBarUpdate()
        {
            // Only run for historical processing, not realtime
            if (State != State.Historical)
                return;

            // Check for manual export trigger (property-based)
            // Manual trigger bypasses exportCompleted check (allows user to force re-export)
            if (ManualExportTrigger && !lastManualExportTrigger && !exportInProgress)
            {
                Print("Manual export triggered by user (via Properties).");
                exportCompleted = false;  // Reset completed flag to allow manual re-export
                TriggerManualExport();
                lastManualExportTrigger = true;
            }
            else if (!ManualExportTrigger)
            {
                lastManualExportTrigger = false;  // Reset when unchecked
            }

            // 🔧 Check for external trigger file from conductor (on every bar update)
            CheckExternalTrigger();

            // Safety validation: ensure export was started
            if (!exportStarted)
            {
                // Check for auto-trigger on first bar
                if (CurrentBar == 0)
                {
                    CheckAutoExportTrigger();
                }
                return; // Wait for export to be triggered
            }

            // Safety guard: prevent processing if export not in progress
            if (!exportInProgress)
            {
                return;
            }

            // Validate chart type (should already be checked, but double-check)
            if (!isTickChart && !isMinuteChart)
                return;
            
            // Validate file stream is still open
            if (sw == null)
            {
                Print("ERROR: File stream is null - export aborted.");
                exportInProgress = false;
                exportStarted = false;
                return;
            }

            // For minute charts, validate OHLCV data
            if (isMinuteChart)
            {
                if (double.IsNaN(Open[0]) || double.IsNaN(High[0]) || double.IsNaN(Low[0]) || 
                    double.IsNaN(Close[0]) || double.IsNaN(Volume[0]))
                {
                    invalidDataSkipped++;
                    if (invalidDataSkipped <= 10) // Only print first 10 warnings
                        Print($"WARNING: Invalid data at {Time[0]} - skipping (OHLCV contains NaN)");
                    return;
                }

                // Validate OHLC relationships for minute bars
                if (High[0] < Low[0] || High[0] < Open[0] || High[0] < Close[0] || 
                    Low[0] > Open[0] || Low[0] > Close[0])
                {
                    invalidDataSkipped++;
                    if (invalidDataSkipped <= 10) // Only print first 10 warnings
                        Print($"WARNING: Invalid OHLC at {Time[0]} - High={High[0]}, Low={Low[0]}, Open={Open[0]}, Close={Close[0]}");
                    return;
                }
            }
            else // Tick chart - only validate Price and Volume
            {
                if (double.IsNaN(Close[0]) || double.IsNaN(Volume[0]))
                {
                    invalidDataSkipped++;
                    if (invalidDataSkipped <= 10)
                        Print($"WARNING: Invalid tick data at {Time[0]} - skipping (Price/Volume contains NaN)");
                    return;
                }
            }

            // Check for data gaps (only after first bar, and mainly for minute data)
            if (CurrentBar > 0 && lastExportTime != DateTime.MinValue && isMinuteChart)
            {
                TimeSpan timeDiff = Time[0] - lastExportTime;
                double minutesDiff = timeDiff.TotalMinutes;
                
                if (minutesDiff > 1.5) // Allow small tolerance for clock differences
                {
                    gapsDetected++;
                    if (gapsDetected <= 20) // Only print first 20 gaps
                    {
                        Print($"DATA GAP: {lastExportTime} to {Time[0]} - {minutesDiff:F1} minutes missing");
                    }
                }
                else if (minutesDiff < 0.5)
                {
                    Print($"WARNING: Very small time difference at {Time[0]}: {minutesDiff:F3} minutes");
                }
            }

            // Write header if not written yet
            if (!headerWritten)
            {
                try
                {
                    // Same format for both tick and minute - translator can detect automatically
                    sw.WriteLine("Date,Time,Open,High,Low,Close,Volume,Instrument");
                    headerWritten = true;
                    Print("Header written to file");
                }
                catch (Exception ex)
                {
                    Print($"ERROR writing header: {ex.Message}");
                    return;
                }
            }

            // Smart timezone conversion + Bar Time Convention Fix (for minute bars only)
            DateTime exportTime;
            try
            {
                if (isMinuteChart)
                {
                    // CRITICAL: NinjaTrader timestamps bars with CLOSE time, but most platforms use OPEN time
                    // We need to subtract 1 minute to get the bar's OPEN time
                    DateTime barOpenTime = Time[0].AddMinutes(-1);
                    
                    // Check if Time[0] is already timezone-aware
                    if (Time[0].Kind == DateTimeKind.Utc)
                    {
                        // Already UTC, use as-is
                        exportTime = barOpenTime;
                    }
                    else if (Time[0].Kind == DateTimeKind.Local)
                    {
                        // Convert from Central Time to UTC
                        TimeZoneInfo centralTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
                        exportTime = TimeZoneInfo.ConvertTimeToUtc(barOpenTime, centralTimeZone);
                    }
                    else
                    {
                        // Unspecified - assume it's Central Time and convert to UTC
                        TimeZoneInfo centralTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
                        exportTime = TimeZoneInfo.ConvertTimeToUtc(barOpenTime, centralTimeZone);
                    }
                    
                    // Log the time conversion for first few bars to verify
                    if (totalBarsProcessed < 5)
                    {
                        Print($"Bar Time Fix: NT Close={Time[0]:HH:mm:ss} -> Export Open UTC={exportTime:HH:mm:ss}");
                    }
                }
                else // Tick chart - no time fix needed, use actual trade time
                {
                    // For tick data, use the actual trade time (no need to subtract)
                    if (Time[0].Kind == DateTimeKind.Utc)
                    {
                        exportTime = Time[0];
                    }
                    else if (Time[0].Kind == DateTimeKind.Local)
                    {
                        // Convert from Central Time to UTC
                        TimeZoneInfo centralTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
                        exportTime = TimeZoneInfo.ConvertTimeToUtc(Time[0], centralTimeZone);
                    }
                    else
                    {
                        // Unspecified - assume it's Central Time and convert to UTC
                        TimeZoneInfo centralTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
                        exportTime = TimeZoneInfo.ConvertTimeToUtc(Time[0], centralTimeZone);
                    }
                }
            }
            catch (Exception ex)
            {
                Print($"WARNING: Timezone conversion failed at {Time[0]}: {ex.Message}. Using original time.");
                exportTime = Time[0];
            }

            // Get instrument name safely
            string instrumentName = Instrument?.MasterInstrument?.Name ?? "UNKNOWN";

            // Format the data line based on chart type
            string line;
            if (isMinuteChart)
            {
                // Minute bars: use actual OHLCV values
                line = $"{exportTime:yyyy-MM-dd},{exportTime:HH:mm:ss},{Open[0]:F2},{High[0]:F2},{Low[0]:F2},{Close[0]:F2},{Volume[0]:F1},{instrumentName}";
            }
            else // Tick chart
            {
                // Tick data: use Price (Close[0]) for all OHLC columns
                // Include milliseconds for tick precision
                double tickPrice = Close[0];
                line = $"{exportTime:yyyy-MM-dd},{exportTime:HH:mm:ss.fff},{tickPrice:F2},{tickPrice:F2},{tickPrice:F2},{tickPrice:F2},{Volume[0]:F1},{instrumentName}";
            }

            // Write the line with error handling
            try
            {
                sw.WriteLine(line);
            }
            catch (Exception ex)
            {
                Print($"ERROR writing data at {Time[0]}: {ex.Message}");
                return;
            }
            
            // Update tracking variables
            lastExportTime = Time[0];
            totalBarsProcessed++;

            // Manual flush every 10,000 records for better performance
            if (totalBarsProcessed % 10000 == 0)
            {
                try
                {
                    sw.Flush();
                }
                catch (Exception ex)
                {
                    Print($"WARNING: Flush failed at {totalBarsProcessed:N0} records: {ex.Message}");
                }
            }

            // Check file size periodically and update progress file
            if (totalBarsProcessed % 100000 == 0)
            {
                try
                {
                    if (File.Exists(filePath))
                    {
                        FileInfo fileInfo = new FileInfo(filePath);
                        if (fileInfo.Length > maxFileSize)
                        {
                            Print($"WARNING: File size approaching limit: {fileInfo.Length / (1024*1024):F1}MB");
                        }
                        
                        // 🔧 Update progress file for pipeline conductor (in logs subfolder)
                        try
                        {
                            string logsPath = Path.Combine(Path.GetDirectoryName(filePath), "logs");
                            Directory.CreateDirectory(logsPath); // Ensure logs folder exists
                            string progressFilePath = Path.Combine(logsPath, 
                                $"export_progress_{Path.GetFileNameWithoutExtension(filePath)}.json");
                            
                            string instrument = Instrument?.MasterInstrument?.Name ?? "UNKNOWN";
                            string progressDataType = isTickChart ? "Tick" : "Minute";
                            string fileName = Path.GetFileName(filePath);
                            string lastUpdateTime = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                            string lastBarTime = Time[0].ToString("yyyy-MM-ddTHH:mm:ss");
                            long fileSizeBytes = fileInfo.Length;
                            double fileSizeMB = fileSizeBytes / (1024.0 * 1024.0);
                            
                            // Write simple JSON manually (no external library needed)
                            StringBuilder json = new StringBuilder();
                            json.AppendLine("{");
                            json.AppendLine($"  \"status\": \"in_progress\",");
                            json.AppendLine($"  \"filePath\": \"{EscapeJsonString(filePath)}\",");
                            json.AppendLine($"  \"fileName\": \"{EscapeJsonString(fileName)}\",");
                            json.AppendLine($"  \"dataType\": \"{progressDataType}\",");
                            json.AppendLine($"  \"instrument\": \"{EscapeJsonString(instrument)}\",");
                            json.AppendLine($"  \"totalBarsProcessed\": {totalBarsProcessed},");
                            json.AppendLine($"  \"gapsDetected\": {gapsDetected},");
                            json.AppendLine($"  \"invalidDataSkipped\": {invalidDataSkipped},");
                            json.AppendLine($"  \"fileSizeBytes\": {fileSizeBytes},");
                            json.AppendLine($"  \"fileSizeMB\": {fileSizeMB:F2},");
                            json.AppendLine($"  \"lastUpdateTime\": \"{lastUpdateTime}\",");
                            json.AppendLine($"  \"lastBarTime\": \"{lastBarTime}\"");
                            json.AppendLine("}");
                            
                            File.WriteAllText(progressFilePath, json.ToString());
                        }
                        catch (Exception ex)
                        {
                            // Don't fail export if progress file write fails
                            Print($"WARNING: Could not update progress file: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Print($"WARNING: Could not check file size: {ex.Message}");
                }
                
                Print($"Progress: {totalBarsProcessed:N0} {(isTickChart ? "ticks" : "bars")} processed...");
            }
            
            // Check if we've reached the end of historical data
            // When historical data processing completes, the export will finish in OnStateChange(Terminated)
        }
        
        // Helper method to get current Central Time
        private DateTime GetCurrentCentralTime()
        {
            try
            {
                DateTime nowUtc = DateTime.UtcNow;
                return TimeZoneInfo.ConvertTimeFromUtc(nowUtc, centralTimeZone);
            }
            catch
            {
                return DateTime.Now; // Fallback to local time
            }
        }
        
        // Public property for manual export trigger (appears in indicator Properties dialog)
        // Right-click indicator → Properties → find "Manual Export Trigger" checkbox
        [NinjaScriptProperty]
        public bool ManualExportTrigger
        {
            get { return manualExportTrigger; }
            set { manualExportTrigger = value; }
        }
        
        // Public property for trigger on open (appears in indicator Properties dialog)
        // Right-click indicator → Properties → find "Trigger On Open" checkbox
        // When enabled, export starts automatically when indicator loads and historical data is available
        [NinjaScriptProperty]
        public bool TriggerOnOpen
        {
            get { return triggerOnOpen; }
            set { triggerOnOpen = value; }
        }

        // Optional: Add method to handle real-time data if needed
        protected override void OnMarketData(MarketDataEventArgs marketDataUpdate)
        {
            // This method is called for real-time data
            // You can add real-time export logic here if needed
            // For now, we only handle historical data
        }
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private DataExporter[] cacheDataExporter;
		public DataExporter DataExporter()
		{
			return DataExporter(Input);
		}

		public DataExporter DataExporter(ISeries<double> input)
		{
			if (cacheDataExporter != null)
				for (int idx = 0; idx < cacheDataExporter.Length; idx++)
					if (cacheDataExporter[idx] != null &&  cacheDataExporter[idx].EqualsInput(input))
						return cacheDataExporter[idx];
			return CacheIndicator<DataExporter>(new DataExporter(), input, ref cacheDataExporter);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.DataExporter DataExporter()
		{
			return indicator.DataExporter(Input);
		}

		public Indicators.DataExporter DataExporter(ISeries<double> input )
		{
			return indicator.DataExporter(input);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.DataExporter DataExporter()
		{
			return indicator.DataExporter(Input);
		}

		public Indicators.DataExporter DataExporter(ISeries<double> input )
		{
			return indicator.DataExporter(input);
		}
	}
}

#endregion

