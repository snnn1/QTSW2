using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace QTSW2.Robot.Core;

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

        // Find Python script in the same directory as this DLL or in project root
        var assemblyLocation = System.Reflection.Assembly.GetExecutingAssembly().Location;
        var assemblyDir = Path.GetDirectoryName(assemblyLocation) ?? "";
        var projectRoot = Directory.GetParent(assemblyDir)?.Parent?.Parent?.Parent?.FullName ?? "";
        _pythonScriptPath = Path.Combine(projectRoot, "modules", "robot", "core", "read_parquet_bars.py");
        
        if (!File.Exists(_pythonScriptPath))
        {
            // Try alternative location (if running from project root)
            var altPath = Path.Combine(
                Directory.GetCurrentDirectory(),
                "modules",
                "robot",
                "core",
                "read_parquet_bars.py"
            );

            if (File.Exists(altPath))
            {
                _pythonScriptPath = altPath;
            }
            else
            {
                throw new FileNotFoundException($"Python helper script not found: {_pythonScriptPath}");
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

        var parquetFiles = Directory.GetFiles(
                instrumentDir,
                "*.parquet",
                SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f)
                .StartsWith($"{instrument}_1m_", StringComparison.OrdinalIgnoreCase))
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

        // CASE B: [ [...], [...], ... ]
        try
        {
            var rows = JsonUtil.Deserialize<List<List<string>>>(output);
            foreach (var row in rows)
            {
                if (row == null || row.Count < 5)
                    throw new InvalidOperationException("Expected array-shaped row with at least 5 elements");

                bars.Add(new Bar(
                    DateTimeOffset.Parse(row[0]),
                    decimal.Parse(row[1]),
                    decimal.Parse(row[2]),
                    decimal.Parse(row[3]),
                    decimal.Parse(row[4]),
                    row.Count > 5 ? decimal.Parse(row[5]) : 0m
                ));
            }
            return bars;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to parse Python JSON output: {ex.Message}");
        }
    }
}

internal class BarsWrapper
{
    public string? Error { get; set; }

    public List<string[]>? Bars { get; set; }
}
