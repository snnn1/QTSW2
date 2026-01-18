using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using QTSW2.Robot.Core;

namespace QTSW2.Robot.Harness;

/// <summary>
/// Provides bars from snapshot parquet files for DRYRUN mode.
/// Reads from data/translated_test/ directory structure.
/// Uses Python pandas to read parquet files (more reliable than Parquet.Net API issues).
/// </summary>
public sealed class SnapshotParquetBarProvider : IBarProvider
{
    private readonly string _snapshotRoot;
    private readonly TimeService _timeService;
    private readonly string _pythonScriptPath;

    public SnapshotParquetBarProvider(string snapshotRoot, TimeService timeService)
    {
        _snapshotRoot = snapshotRoot ?? throw new ArgumentNullException(nameof(snapshotRoot));
        _timeService = timeService ?? throw new ArgumentNullException(nameof(timeService));

        if (!Directory.Exists(_snapshotRoot))
        {
            throw new DirectoryNotFoundException($"Snapshot directory not found: {_snapshotRoot}");
        }

        // Find Python script - try current directory first (when running from project root)
        var currentDir = Directory.GetCurrentDirectory();
        _pythonScriptPath = Path.Combine(currentDir, "modules", "robot", "harness", "read_parquet_bars.py");
        
        if (!File.Exists(_pythonScriptPath))
        {
            // Try alternative location (if running from assembly directory)
            var assemblyLocation = System.Reflection.Assembly.GetExecutingAssembly().Location;
            var assemblyDir = Path.GetDirectoryName(assemblyLocation) ?? "";
            var projectRoot = Directory.GetParent(assemblyDir)?.Parent?.Parent?.Parent?.FullName ?? "";
            var altPath = Path.Combine(projectRoot, "modules", "robot", "harness", "read_parquet_bars.py");

            if (File.Exists(altPath))
            {
                _pythonScriptPath = altPath;
            }
            else
            {
                throw new FileNotFoundException($"Python helper script not found. Tried: {_pythonScriptPath} and {altPath}");
            }
        }
    }

    public IEnumerable<Bar> GetBars(
        string instrument,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc)
    {
        var instrumentDir = Path.Combine(_snapshotRoot, instrument, "1m");
        if (!Directory.Exists(instrumentDir))
            yield break;

        // Extract date range from UTC timestamps (convert to Chicago time for filename matching)
        var startChicago = _timeService.ConvertUtcToChicago(startUtc);
        var endChicago = _timeService.ConvertUtcToChicago(endUtc);
        var startDate = startChicago.Date;
        var endDate = endChicago.Date;

        // Find parquet files, but filter by date range in filename to avoid reading unnecessary files
        var parquetFiles = Directory.GetFiles(
                instrumentDir,
                "*.parquet",
                SearchOption.AllDirectories)
            .Where(f =>
            {
                var fileName = Path.GetFileName(f);
                if (!fileName.StartsWith($"{instrument}_1m_", StringComparison.OrdinalIgnoreCase))
                    return false;
                
                // Extract date from filename: {instrument}_1m_{YYYY-MM-DD}.parquet
                var datePart = fileName.Substring($"{instrument}_1m_".Length).Replace(".parquet", "");
                // Parse date in YYYY-MM-DD format
                if (datePart.Length == 10 && datePart[4] == '-' && datePart[7] == '-')
                {
                    if (int.TryParse(datePart.Substring(0, 4), out var year) &&
                        int.TryParse(datePart.Substring(5, 2), out var month) &&
                        int.TryParse(datePart.Substring(8, 2), out var day))
                    {
                        try
                        {
                            var fileDate = new System.DateTime(year, month, day);
                            // Only include files within our date range
                            return fileDate.Date >= startDate && fileDate.Date <= endDate;
                        }
                        catch
                        {
                            return false; // Invalid date
                        }
                    }
                }
                return false; // Skip files with invalid date format
            })
            .OrderBy(f => f)
            .ToList();

        if (parquetFiles.Count == 0)
            yield break;

        var allBars = new List<Bar>();

        foreach (var filePath in parquetFiles)
        {
            try
            {
                var fileBars = ReadBarsFromFile(
                    filePath,
                    instrument,
                    startUtc,
                    endUtc);

                allBars.AddRange(fileBars);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Error reading parquet file {filePath}: {ex.Message}",
                    ex);
            }
        }

        allBars.Sort((a, b) =>
            a.TimestampUtc.CompareTo(b.TimestampUtc));

        for (int i = 1; i < allBars.Count; i++)
        {
            if (allBars[i].TimestampUtc < allBars[i - 1].TimestampUtc)
            {
                throw new InvalidDataException(
                    $"Bars out of order: " +
                    $"{allBars[i - 1].TimestampUtc:o} > " +
                    $"{allBars[i].TimestampUtc:o}");
            }
        }

        foreach (var bar in allBars)
            yield return bar;
    }

    private List<Bar> ReadBarsFromFile(
        string filePath,
        string instrument,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc)
    {
        var bars = new List<Bar>();

        var processStartInfo = new ProcessStartInfo
        {
            FileName = "python",
            Arguments =
                $"\"{_pythonScriptPath}\" " +
                $"\"{filePath}\" " +
                $"\"{instrument}\" " +
                $"\"{startUtc:o}\" " +
                $"\"{endUtc:o}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(processStartInfo);
        if (process == null)
            throw new InvalidOperationException("Failed to start Python process");

        var output = process.StandardOutput.ReadToEnd();
        var error  = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Python script failed with exit code {process.ExitCode}: {error}");
        }

        // Parse JSON output
        // Try CASE A: { "bars": [[...], [...]] } or { "error": "..." }
        try
        {
            var wrapper = JsonUtil.Deserialize<BarsWrapper>(output);
            if (wrapper.Error != null)
                throw new InvalidOperationException(wrapper.Error);
            
            if (wrapper.Bars != null)
            {
                foreach (var row in wrapper.Bars)
                {
                    if (row == null || row.Length < 5)
                        throw new InvalidOperationException("Expected array-shaped row with at least 5 elements");

                    bars.Add(new Bar(
                        DateTimeOffset.Parse(row[0]),
                        decimal.Parse(row[1]),
                        decimal.Parse(row[2]),
                        decimal.Parse(row[3]),
                        decimal.Parse(row[4]),
                        row.Length > 5 ? decimal.Parse(row[5]) : 0m
                    ));
                }
                return bars;
            }
        }
        catch
        {
            // Fall through to try CASE B
        }

        // CASE B: [ [...], [...], ... ] - handle mixed types (string timestamp, numeric prices)
        try
        {
            // Use System.Text.Json directly for better mixed-type handling (.NET 8.0)
            using var doc = System.Text.Json.JsonDocument.Parse(output);
            var root = doc.RootElement;
            
            if (root.ValueKind != System.Text.Json.JsonValueKind.Array)
                throw new InvalidOperationException("Expected JSON array");
            
            foreach (var rowElement in root.EnumerateArray())
            {
                if (rowElement.ValueKind != System.Text.Json.JsonValueKind.Array)
                    throw new InvalidOperationException("Expected array-shaped row");
                
                var rowArray = rowElement.EnumerateArray().ToList();
                if (rowArray.Count < 5)
                    throw new InvalidOperationException("Expected array-shaped row with at least 5 elements");

                var timestamp = DateTimeOffset.Parse(rowArray[0].GetString() ?? throw new InvalidOperationException("Invalid timestamp"));
                var open = rowArray[1].GetDecimal();
                var high = rowArray[2].GetDecimal();
                var low = rowArray[3].GetDecimal();
                var close = rowArray[4].GetDecimal();
                var volume = rowArray.Count > 5 && rowArray[5].ValueKind != System.Text.Json.JsonValueKind.Null 
                    ? rowArray[5].GetDecimal() 
                    : (decimal?)null;

                bars.Add(new Bar(timestamp, open, high, low, close, volume));
            }
            return bars;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to parse Python JSON output: {ex.Message}", ex);
        }
    }
}

internal class BarsWrapper
{
    public string? Error { get; set; }

    public List<string[]>? Bars { get; set; }
}
