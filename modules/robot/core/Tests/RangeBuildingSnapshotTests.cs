// Unit tests for RANGE_BUILDING snapshot persistence and restore.
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test RANGE_BUILDING_SNAPSHOT
//
// Verifies: Persist/LoadLatest, identity validation, missing snapshot, latest-wins.

using System;
using System.IO;
using System.Linq;

namespace QTSW2.Robot.Core.Tests;

public static class RangeBuildingSnapshotTests
{
    public static (bool Pass, string? Error) RunRangeBuildingSnapshotTests()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "RangeBuildingSnapshotTests_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            var logDir = Path.Combine(tempDir, "logs", "robot");
            Directory.CreateDirectory(logDir);

            // Create persister with temp root (override singleton by using fresh instance via reflection or new)
            // Persister is singleton - we need to test with a real file. Use a subdir that we control.
            var persister = RangeBuildingSnapshotPersister.GetInstance(tempDir);

            // 1. Persist and load - basic roundtrip
            var snapshot1 = new RangeBuildingSnapshot
            {
                source = RangeBuildingSnapshot.SourceMarker,
                trading_date = "2026-03-16",
                stream_id = "NG1",
                instrument = "NG",
                session = "S1",
                slot_time = "09:00",
                range_start_chicago = "2026-03-16T02:00:00-05:00",
                range_start_utc = "2026-03-16T07:00:00Z",
                last_processed_bar_time_utc = "2026-03-16T14:15:00Z",
                bar_count = 120,
                range_high = 3.05m,
                range_low = 2.98m,
                freeze_close = 3.01m,
                freeze_close_source = "BAR_CLOSE",
                tick_size = 0.001m,
                snapshot_timestamp_utc = DateTimeOffset.UtcNow.ToString("o"),
                bars = new System.Collections.Generic.List<RangeBuildingSnapshotBar>
                {
                    new() { timestamp_utc = "2026-03-16T07:00:00Z", open = 3.0m, high = 3.02m, low = 2.99m, close = 3.01m }
                }
            };

            persister.Persist(snapshot1);
            var loaded = persister.LoadLatest("2026-03-16", "NG1");
            if (loaded == null)
                return (false, "LoadLatest returned null after Persist");
            if (loaded.stream_id != "NG1" || loaded.bar_count != 120 || loaded.range_high != 3.05m)
                return (false, $"Loaded snapshot mismatch: stream={loaded.stream_id} bar_count={loaded.bar_count} range_high={loaded.range_high}");

            // 2. Multiple snapshots - latest wins
            var snapshot2 = new RangeBuildingSnapshot
            {
                source = RangeBuildingSnapshot.SourceMarker,
                trading_date = "2026-03-16",
                stream_id = "NG1",
                instrument = "NG",
                session = "S1",
                slot_time = "09:00",
                last_processed_bar_time_utc = "2026-03-16T14:18:00Z",
                bar_count = 135,
                range_high = 3.06m,
                range_low = 2.97m,
                bars = new System.Collections.Generic.List<RangeBuildingSnapshotBar>()
            };
            persister.Persist(snapshot2);

            loaded = persister.LoadLatest("2026-03-16", "NG1");
            if (loaded == null)
                return (false, "LoadLatest returned null after second Persist");
            if (loaded.bar_count != 135 || loaded.range_high != 3.06m)
                return (false, $"Latest snapshot should win: expected bar_count=135 range_high=3.06, got bar_count={loaded.bar_count} range_high={loaded.range_high}");

            // 3. Missing snapshot - returns null
            var missing = persister.LoadLatest("2026-03-16", "NONEXISTENT");
            if (missing != null)
                return (false, "LoadLatest for non-existent stream should return null");

            // 4. Different stream - returns its own snapshot
            var snapshot3 = new RangeBuildingSnapshot
            {
                source = RangeBuildingSnapshot.SourceMarker,
                trading_date = "2026-03-16",
                stream_id = "CL2",
                instrument = "CL",
                session = "S2",
                slot_time = "11:00",
                bar_count = 50,
                bars = new System.Collections.Generic.List<RangeBuildingSnapshotBar>()
            };
            persister.Persist(snapshot3);

            var loadedNg1 = persister.LoadLatest("2026-03-16", "NG1");
            var loadedCl2 = persister.LoadLatest("2026-03-16", "CL2");
            if (loadedNg1?.stream_id != "NG1" || loadedCl2?.stream_id != "CL2")
                return (false, $"Stream isolation failed: NG1={loadedNg1?.stream_id} CL2={loadedCl2?.stream_id}");

            // 5. RANGE_LOCKED takes precedence - tested implicitly: if RANGE_LOCKED restore runs first and succeeds,
            //    RANGE_BUILDING restore is never attempted (constructor logic). Unit test for that would need
            //    full StreamStateMachine setup - covered by integration/manual test.

            // 6. GetRestoreDiagnostics - missing file
            var diagMissing = persister.GetRestoreDiagnostics("2026-03-99", "NG1");
            if (diagMissing.file_exists)
                return (false, "GetRestoreDiagnostics for non-existent date should have file_exists=false");
            if (string.IsNullOrEmpty(diagMissing.file_path) || !diagMissing.file_path.Contains("range_building_2026-03-99"))
                return (false, $"GetRestoreDiagnostics file_path should contain date: {diagMissing.file_path}");

            // 7. GetRestoreDiagnostics - file exists, stream not in file
            var diagNoStream = persister.GetRestoreDiagnostics("2026-03-16", "NONEXISTENT");
            if (!diagNoStream.file_exists)
                return (false, "GetRestoreDiagnostics for existing file should have file_exists=true");
            if (diagNoStream.stream_line_count != 0)
                return (false, $"GetRestoreDiagnostics for non-existent stream should have stream_line_count=0, got {diagNoStream.stream_line_count}");
            if (diagNoStream.total_lines < 3)
                return (false, $"GetRestoreDiagnostics total_lines should reflect file (NG1+CL2): {diagNoStream.total_lines}");

            // 8. GetPathInfo - resolved paths
            var pathInfo = persister.GetPathInfo("2026-03-16");
            if (string.IsNullOrEmpty(pathInfo.resolved_project_root))
                return (false, "GetPathInfo resolved_project_root should be non-empty");
            if (string.IsNullOrEmpty(pathInfo.absolute_snapshot_path) || !pathInfo.absolute_snapshot_path.EndsWith("range_building_2026-03-16.jsonl", StringComparison.OrdinalIgnoreCase))
                return (false, $"GetPathInfo absolute_snapshot_path should end with range_building_2026-03-16.jsonl: {pathInfo.absolute_snapshot_path}");

            // 9. Snapshot identity soft mismatch - persister returns snapshot regardless of session/slot
            //    (StreamStateMachine applies relaxed identity: stream+instrument+date match; session/slot change = soft mismatch, still restore)
            loaded = persister.LoadLatest("2026-03-16", "NG1");
            if (loaded == null)
                return (false, "LoadLatest for NG1 should return snapshot (identity check is in consumer)");
            if (loaded.session != "S1" || loaded.slot_time != "09:00")
                return (false, $"Snapshot has session={loaded.session} slot_time={loaded.slot_time} - consumer may still restore if stream/instrument/date match");

            return (true, null);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch { /* best effort */ }
        }
    }
}
