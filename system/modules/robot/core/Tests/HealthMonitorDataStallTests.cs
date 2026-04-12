// Unit tests for HealthMonitor data stall detection (quant-grade redesign).
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test DATA_STALL_DETECTION
//
// Verifies: instrument-aware thresholds, debounce, activity-aware filtering, stall classification.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using QTSW2.Robot.Core;

namespace QTSW2.Robot.Core.Tests;

public static class HealthMonitorDataStallTests
{
    public static (bool Pass, string? Error) RunDataStallDetectionTests()
    {
        var baseTemp = Path.Combine(Path.GetTempPath(), "HealthMonitorDataStall_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(baseTemp);

            var (pass1, err1) = TestLowLiquidityNoStall(CreateTestDir(baseTemp, "1"));
            if (!pass1) return (false, $"Test 1 (Low liquidity): {err1}");

            var (pass2, err2) = TestTrueDisconnectTriggersStall(CreateTestDir(baseTemp, "2"));
            if (!pass2) return (false, $"Test 2 (True disconnect): {err2}");

            var (pass3, err3) = TestAggregationDelayClassification(CreateTestDir(baseTemp, "3"));
            if (!pass3) return (false, $"Test 3 (Aggregation delay): {err3}");

            var (pass4, err4) = TestDebounceWorks(CreateTestDir(baseTemp, "4"));
            if (!pass4) return (false, $"Test 4 (Debounce): {err4}");

            return (true, null);
        }
        finally
        {
            try { Directory.Delete(baseTemp, recursive: true); } catch { /* ignore */ }
        }
    }

    private static (string tempDir, string logDir) CreateTestDir(string baseTemp, string suffix)
    {
        var tempDir = Path.Combine(baseTemp, "t" + suffix);
        Directory.CreateDirectory(tempDir);
        var logDir = Path.Combine(tempDir, "logs", "robot");
        Directory.CreateDirectory(logDir);
        return (tempDir, logDir);
    }

    private static (bool Pass, string? Error) TestLowLiquidityNoStall((string tempDir, string logDir) paths)
    {
        var (tempDir, logDir) = paths;
        var svc = RobotLoggingService.GetOrCreate(tempDir, logDir);
        svc.Start(); // Ensure background worker flushes to disk
        var config = new HealthMonitorConfig { enabled = true, default_stall_seconds = 300 };
        var log = new RobotLogger(tempDir, logDir, "MNG");
        var hm = new HealthMonitor(tempDir, config, log);
        hm.Start();

        var baseTime = DateTimeOffset.UtcNow.AddHours(-1);
        // Feed sparse bars (150s apart) - MNG threshold 420, expected will be ~150
        for (int i = 0; i < 5; i++)
            hm.OnBar("MNG", baseTime.AddSeconds(i * 150), close: 2.5m, volume: 0);
        // Last bar at 600s; eval 400s later -> elapsed=400
        var evalTime = baseTime.AddSeconds(600 + 400);
        hm.Evaluate(evalTime);
        hm.Stop();
        System.Threading.Thread.Sleep(500); // Allow async flush
        var events = ReadLogEvents(logDir, "DATA_LOSS_DETECTED");

        // Should NOT have triggered stall: elapsed 400 < threshold*2 (840) for low-activity, or debounce
        if (events.Count > 0)
            return (false, $"Low liquidity should NOT trigger stall, got {events.Count} DATA_LOSS_DETECTED");
        return (true, null);
    }

    private static (bool Pass, string? Error) TestTrueDisconnectTriggersStall((string tempDir, string logDir) paths)
    {
        var (tempDir, logDir) = paths;
        var svc = RobotLoggingService.GetOrCreate(tempDir, logDir);
        svc.Start();
        var config = new HealthMonitorConfig { enabled = true, default_stall_seconds = 300 };
        var log = new RobotLogger(tempDir, logDir, "MES");
        var hm = new HealthMonitor(tempDir, config, log);
        hm.Start();

        var baseTime = DateTimeOffset.UtcNow.AddHours(-2);
        hm.OnBar("MES", baseTime, close: 6000m);

        // No more bars for 500s - true disconnect
        var evalTime = baseTime.AddSeconds(500);
        hm.Evaluate(evalTime);
        hm.Stop();
        svc.Stop(); // Drain queue and flush
        System.Threading.Thread.Sleep(500);
        var events = ReadLogEvents(logDir, "DATA_LOSS_DETECTED");

        if (events.Count == 0)
            return (false, "True disconnect should trigger stall, got 0 DATA_LOSS_DETECTED");
        return (true, null);
    }

    private static (bool Pass, string? Error) TestAggregationDelayClassification((string tempDir, string logDir) paths)
    {
        var (tempDir, logDir) = paths;
        var svc = RobotLoggingService.GetOrCreate(tempDir, logDir);
        svc.Start();
        var config = new HealthMonitorConfig { enabled = true, default_stall_seconds = 300 };
        var log = new RobotLogger(tempDir, logDir, "MULTI");
        var hm = new HealthMonitor(tempDir, config, log);
        hm.Start();

        var baseTime = DateTimeOffset.UtcNow.AddHours(-2);
        hm.OnBar("MES", baseTime, close: 6000m);
        hm.OnBar("NQ", baseTime, close: 21000m);

        // Both stall simultaneously (no bars for 350s)
        var evalTime = baseTime.AddSeconds(350);
        hm.Evaluate(evalTime);
        hm.Stop();
        System.Threading.Thread.Sleep(500);
        var events = ReadLogEvents(logDir, "DATA_LOSS_DETECTED");

        if (events.Count < 2)
            return (false, $"Expected 2 DATA_LOSS_DETECTED (MES+NQ), got {events.Count}");
        var classifications = events.Select(e => GetPayloadString(e, "stall_classification")).ToList();
        if (!classifications.Any(c => c == "AGGREGATION_DELAY"))
            return (false, $"Expected at least one AGGREGATION_DELAY when multiple instruments stall, got [{string.Join(", ", classifications)}]");
        return (true, null);
    }

    private static (bool Pass, string? Error) TestDebounceWorks((string tempDir, string logDir) paths)
    {
        var (tempDir, logDir) = paths;
        var svc = RobotLoggingService.GetOrCreate(tempDir, logDir);
        svc.Start();
        var config = new HealthMonitorConfig { enabled = true, default_stall_seconds = 300 };
        var log = new RobotLogger(tempDir, logDir, "MES");
        var hm = new HealthMonitor(tempDir, config, log);
        hm.Start();

        var baseTime = DateTimeOffset.UtcNow.AddHours(-2);
        // Feed bars at 60s intervals to establish expected_interval ~60
        for (int i = 0; i < 10; i++)
            hm.OnBar("MES", baseTime.AddSeconds(i * 60), close: 6000m + i);

        // One delayed bar: gap 120s (2x expected) - debounce requires max(300, 180)=300, so 120 < 300, no stall
        var afterGap = baseTime.AddSeconds(9 * 60 + 120);
        hm.OnBar("MES", afterGap, close: 6010m);
        var evalAfterOneGap = afterGap.AddSeconds(10);
        hm.Evaluate(evalAfterOneGap);

        System.Threading.Thread.Sleep(500);
        var events1 = ReadLogEvents(logDir, "DATA_LOSS_DETECTED");
        if (events1.Count > 0)
            return (false, $"One 120s delayed bar should NOT trigger stall (debounce), got {events1.Count}");

        // Sustained delay: no more bars for 350s from last bar
        var evalSustained = afterGap.AddSeconds(350);
        hm.Evaluate(evalSustained);
        hm.Stop();
        System.Threading.Thread.Sleep(500);
        var events2 = ReadLogEvents(logDir, "DATA_LOSS_DETECTED");

        if (events2.Count == 0)
            return (false, "Sustained 350s gap should trigger stall");
        return (true, null);
    }

    private static string? GetPayloadString(JsonElement root, string key)
    {
        if (root.TryGetProperty("data", out var data))
        {
            if (data.TryGetProperty(key, out var v))
                return v.GetString();
            if (data.TryGetProperty("payload", out var p) && p.ValueKind == JsonValueKind.Object)
            {
                if (p.TryGetProperty(key, out var pv))
                    return pv.GetString();
            }
        }
        return null;
    }

    private static List<JsonElement> ReadLogEvents(string logDir, string eventType)
    {
        var results = new List<JsonElement>();
        if (!Directory.Exists(logDir)) return results;
        var files = Directory.GetFiles(logDir, "*.jsonl");
        foreach (var f in files)
        {
            try
            {
                foreach (var line in File.ReadLines(f))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        var ev = root.TryGetProperty("event", out var e) ? e.GetString() : root.TryGetProperty("event_type", out var et) ? et.GetString() : null;
                        if (ev == eventType)
                            results.Add(root.Clone());
                    }
                    catch { /* skip malformed */ }
                }
            }
            catch { /* skip */ }
        }
        return results;
    }
}
