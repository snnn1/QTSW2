using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QTSW2.Robot.Core;

namespace QTSW2.Tests;

/// <summary>
/// Smoke test harness for RobotLoggingService.
/// Tests concurrent logging from multiple threads at high rate.
/// </summary>
public class RobotLoggingServiceSmokeTest
{
    public static void Run(int numThreads = 8, int eventsPerThread = 25000)
    {
        Console.WriteLine($"Starting smoke test: {numThreads} threads, {eventsPerThread} events/thread");
        Console.WriteLine($"Total events: {numThreads * eventsPerThread:N0}");

        var tempDir = Path.Combine(Path.GetTempPath(), $"robot_logging_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var service = new RobotLoggingService(tempDir);
            service.Start();

            var totalEvents = numThreads * eventsPerThread;
            var completedEvents = 0L;
            var exceptions = new List<Exception>();

            // Spawn producer threads
            var tasks = Enumerable.Range(0, numThreads).Select(threadId =>
                Task.Run(() =>
                {
                    var random = new Random(threadId);
                    var instruments = new[] { "ES", "NQ", "CL", "GC", "YM" };
                    
                    for (int i = 0; i < eventsPerThread; i++)
                    {
                        try
                        {
                            var instrument = instruments[random.Next(instruments.Length)];
                            var level = random.Next(100) < 80 ? "INFO" : 
                                       random.Next(100) < 95 ? "DEBUG" : 
                                       random.Next(100) < 99 ? "WARN" : "ERROR";
                            
                            var evt = new RobotLogEvent(
                                DateTimeOffset.UtcNow,
                                level,
                                "TestSource",
                                instrument,
                                $"TEST_EVENT_{i}",
                                $"Test message {i} from thread {threadId}",
                                data: new Dictionary<string, object?> { ["thread_id"] = threadId, ["event_index"] = i }
                            );
                            
                            service.Log(evt);
                            Interlocked.Increment(ref completedEvents);
                        }
                        catch (Exception ex)
                        {
                            lock (exceptions)
                            {
                                exceptions.Add(ex);
                            }
                        }
                    }
                })
            ).ToArray();

            // Wait for all producers
            Task.WaitAll(tasks);

            Console.WriteLine($"Produced {completedEvents:N0} events");
            Console.WriteLine($"Exceptions: {exceptions.Count}");

            // Stop service (drains queue)
            service.Stop();
            service.Dispose();

            // Validate output
            var jsonlFiles = Directory.GetFiles(tempDir, "robot_*.jsonl");
            Console.WriteLine($"Found {jsonlFiles.Length} log files");

            var totalLines = 0L;
            var parseErrors = 0;
            var schemaErrors = 0;

            foreach (var file in jsonlFiles)
            {
                var lines = File.ReadAllLines(file);
                totalLines += lines.Length;
                Console.WriteLine($"  {Path.GetFileName(file)}: {lines.Length:N0} lines");

                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    // Validate JSON parse
                    try
                    {
                        var dict = JsonUtil.Deserialize<Dictionary<string, object>>(line);
                        
                        // Validate required keys
                        var required = new[] { "ts_utc", "level", "source", "instrument", "event", "message" };
                        foreach (var key in required)
                        {
                            if (!dict.ContainsKey(key))
                            {
                                schemaErrors++;
                                Console.WriteLine($"  Schema error: missing key '{key}' in line: {line.Substring(0, Math.Min(100, line.Length))}");
                                break;
                            }
                        }

                        // Validate timestamp format (UTC)
                        if (dict.TryGetValue("ts_utc", out var tsObj))
                        {
                            var tsStr = tsObj?.ToString() ?? "";
                            if (!tsStr.EndsWith("Z") && !tsStr.Contains("+00:00") && !tsStr.Contains("-00:00"))
                            {
                                schemaErrors++;
                                Console.WriteLine($"  Timestamp error: not UTC format: {tsStr}");
                            }
                        }
                    }
                    catch
                    {
                        parseErrors++;
                        Console.WriteLine($"  Parse error in line: {line.Substring(0, Math.Min(100, line.Length))}");
                    }
                }
            }

            Console.WriteLine();
            Console.WriteLine("=== RESULTS ===");
            Console.WriteLine($"Total lines written: {totalLines:N0}");
            Console.WriteLine($"Expected (minus drops): ~{totalEvents:N0} (some DEBUG/INFO may be dropped under backpressure)");
            Console.WriteLine($"Parse errors: {parseErrors}");
            Console.WriteLine($"Schema errors: {schemaErrors}");
            Console.WriteLine($"Exceptions during test: {exceptions.Count}");

            if (exceptions.Count > 0)
            {
                Console.WriteLine("\nExceptions:");
                foreach (var ex in exceptions.Take(5))
                {
                    Console.WriteLine($"  {ex.GetType().Name}: {ex.Message}");
                }
            }

            // Pass criteria
            var passed = exceptions.Count == 0 && parseErrors == 0 && schemaErrors == 0;
            Console.WriteLine($"\nTest {(passed ? "PASSED" : "FAILED")}");

            if (!passed)
            {
                throw new Exception($"Test failed: exceptions={exceptions.Count}, parse_errors={parseErrors}, schema_errors={schemaErrors}");
            }
        }
        finally
        {
            // Cleanup
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }
}
