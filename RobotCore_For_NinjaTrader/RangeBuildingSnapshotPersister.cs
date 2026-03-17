using System;
using System.Collections.Generic;
using System.IO;

namespace QTSW2.Robot.Core;

/// <summary>Diagnostics for RANGE_BUILDING restore failure investigation.</summary>
public sealed class RestoreDiagnostics
{
    public string file_path { get; set; } = "";
    public bool file_exists { get; set; }
    public int total_lines { get; set; }
    public int stream_line_count { get; set; }
}

/// <summary>
/// Persists RANGE_BUILDING snapshots to range_building_{trading_date}.jsonl.
/// Append-only; latest snapshot per stream wins on restore.
/// Fail-safe: never throws to caller.
/// </summary>
/// <summary>Path info for RANGE_BUILDING_SNAPSHOT_PATH_INFO diagnostics.</summary>
public sealed class SnapshotPathInfo
{
    public string resolved_project_root { get; set; } = "";
    public string absolute_snapshot_path { get; set; } = "";
}

public sealed class RangeBuildingSnapshotPersister
{
    private readonly string _projectRoot;
    private readonly string _logDir;
    private static RangeBuildingSnapshotPersister? _instance;
    private static readonly object _instanceLock = new();
    private static readonly Dictionary<string, object> _fileLocks = new();
    private static readonly object _locksLock = new();

    private RangeBuildingSnapshotPersister(string projectRoot)
    {
        _projectRoot = projectRoot ?? "";
        _logDir = Path.Combine(projectRoot ?? "", "logs", "robot");
        Directory.CreateDirectory(_logDir);
    }

    /// <summary>Path info for RANGE_BUILDING_SNAPSHOT_PATH_INFO (write and read time).</summary>
    public SnapshotPathInfo GetPathInfo(string tradingDay)
    {
        var absPath = GetFilePathForDate(tradingDay);
        return new SnapshotPathInfo
        {
            resolved_project_root = Path.GetFullPath(_projectRoot),
            absolute_snapshot_path = Path.GetFullPath(absPath)
        };
    }

    public static RangeBuildingSnapshotPersister GetInstance(string projectRoot)
    {
        if (_instance == null)
        {
            lock (_instanceLock)
            {
                _instance ??= new RangeBuildingSnapshotPersister(projectRoot);
            }
        }
        return _instance;
    }

    /// <summary>
    /// Persist a RANGE_BUILDING snapshot. Append-only; multiple snapshots per stream per day allowed.
    /// </summary>
    public void Persist(RangeBuildingSnapshot snapshot)
    {
        try
        {
            var filePath = GetFilePath(snapshot.trading_date);
            var json = JsonUtil.Serialize(snapshot);
            var fileLock = GetFileLock(filePath);

            lock (fileLock)
            {
                try
                {
                    File.AppendAllText(filePath, json + Environment.NewLine);
                }
                catch (IOException)
                {
                    // Fail-safe: file might be locked
                }
            }
        }
        catch (Exception)
        {
            // Fail-safe: never throw to caller
        }
    }

    /// <summary>
    /// Load the latest RANGE_BUILDING snapshot for a stream on a trading day.
    /// Returns null if none found or invalid.
    /// </summary>
    public RangeBuildingSnapshot? LoadLatest(string tradingDay, string streamId)
    {
        try
        {
            var filePath = GetFilePath(tradingDay);
            if (!File.Exists(filePath))
                return null;

            var fileLock = GetFileLock(filePath);
            lock (fileLock)
            {
                var lines = File.ReadAllLines(filePath);
                RangeBuildingSnapshot? latest = null;

                for (var i = lines.Length - 1; i >= 0; i--)
                {
                    var line = lines[i];
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    try
                    {
                        var snap = JsonUtil.Deserialize<RangeBuildingSnapshot>(line);
                        if (snap != null &&
                            snap.source == RangeBuildingSnapshot.SourceMarker &&
                            snap.trading_date == tradingDay &&
                            snap.stream_id == streamId)
                        {
                            latest = snap;
                            break; // Last occurrence in file = latest
                        }
                    }
                    catch
                    {
                        // Skip malformed lines
                    }
                }

                return latest;
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Exact file path for diagnostics (write and read time).</summary>
    public string GetFilePathForDate(string tradingDay)
    {
        return Path.Combine(_logDir, $"range_building_{tradingDay}.jsonl");
    }

    /// <summary>Diagnostics for restore failure investigation. Call when LoadLatest returns null.</summary>
    public RestoreDiagnostics GetRestoreDiagnostics(string tradingDay, string streamId)
    {
        var filePath = GetFilePathForDate(tradingDay);
        var fileExists = File.Exists(filePath);
        var totalLines = 0;
        var streamLineCount = 0;
        if (fileExists)
        {
            try
            {
                var lines = File.ReadAllLines(filePath);
                totalLines = lines.Length;
                var streamMarker = "\"stream_id\":\"" + streamId + "\"";
                foreach (var line in lines)
                {
                    if (!string.IsNullOrWhiteSpace(line) && line.IndexOf(streamMarker, StringComparison.Ordinal) >= 0)
                        streamLineCount++;
                }
            }
            catch { /* best-effort */ }
        }
        return new RestoreDiagnostics
        {
            file_path = filePath,
            file_exists = fileExists,
            total_lines = totalLines,
            stream_line_count = streamLineCount
        };
    }

    private string GetFilePath(string tradingDay)
    {
        return GetFilePathForDate(tradingDay);
    }

    private static object GetFileLock(string path)
    {
        lock (_locksLock)
        {
            if (!_fileLocks.TryGetValue(path, out var fileLock))
            {
                fileLock = new object();
                _fileLocks[path] = fileLock;
            }
            return fileLock;
        }
    }
}
