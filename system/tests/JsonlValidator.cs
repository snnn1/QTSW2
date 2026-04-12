using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QTSW2.Robot.Core;

namespace QTSW2.Tests;

/// <summary>
/// Validator utility for JSONL log files.
/// Validates JSON parsing, schema consistency, and timestamp format.
/// </summary>
public class JsonlValidator
{
    public class ValidationResult
    {
        public int TotalLines { get; set; }
        public int ValidLines { get; set; }
        public int ParseErrors { get; set; }
        public int SchemaErrors { get; set; }
        public int TimestampErrors { get; set; }
        public List<string> Errors { get; set; } = new();
    }

    public static ValidationResult ValidateFile(string filePath)
    {
        var result = new ValidationResult();
        
        if (!File.Exists(filePath))
        {
            result.Errors.Add($"File does not exist: {filePath}");
            return result;
        }

        var lines = File.ReadAllLines(filePath);
        result.TotalLines = lines.Length;

        var requiredKeys = new[] { "ts_utc", "level", "source", "instrument", "event", "message" };

        foreach (var (line, index) in lines.Select((l, i) => (l, i)))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                result.ValidLines++;
                continue;
            }

            // Validate JSON parse
            try
            {
                var dict = JsonUtil.Deserialize<Dictionary<string, object>>(line);
                
                // Validate required keys
                bool hasAllKeys = true;
                foreach (var key in requiredKeys)
                {
                    if (!dict.ContainsKey(key))
                    {
                        result.SchemaErrors++;
                        result.Errors.Add($"Line {index + 1}: Missing required key '{key}'");
                        hasAllKeys = false;
                        break;
                    }
                }

                if (!hasAllKeys) continue;

                // Validate timestamp format (UTC)
                if (dict.TryGetValue("ts_utc", out var tsObj))
                {
                    var tsStr = tsObj?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(tsStr))
                    {
                        result.TimestampErrors++;
                        result.Errors.Add($"Line {index + 1}: ts_utc is empty");
                    }
                    else if (!tsStr.EndsWith("Z") && !tsStr.Contains("+00:00") && !tsStr.Contains("-00:00"))
                    {
                        // Check if it's valid ISO8601 and parseable as UTC
                        if (!DateTimeOffset.TryParse(tsStr, out var parsed) || parsed.Offset != TimeSpan.Zero)
                        {
                            result.TimestampErrors++;
                            result.Errors.Add($"Line {index + 1}: ts_utc is not UTC format: {tsStr}");
                        }
                    }
                }

                // Check for multi-line writes (embedded newlines)
                if (line.Contains("\n") || line.Contains("\r"))
                {
                    result.Errors.Add($"Line {index + 1}: Contains embedded newline (multi-line write detected)");
                }

                result.ValidLines++;
            }
            catch (Exception ex)
            {
                result.ParseErrors++;
                result.Errors.Add($"Line {index + 1}: JSON parse error: {ex.Message}");
            }
        }

        return result;
    }

    public static void ValidateDirectory(string directoryPath)
    {
        var jsonlFiles = Directory.GetFiles(directoryPath, "*.jsonl", SearchOption.AllDirectories);
        
        Console.WriteLine($"Validating {jsonlFiles.Length} JSONL files in {directoryPath}");
        Console.WriteLine();

        var totalValid = 0;
        var totalErrors = 0;

        foreach (var file in jsonlFiles)
        {
            Console.WriteLine($"Validating: {Path.GetFileName(file)}");
            var result = ValidateFile(file);
            
            Console.WriteLine($"  Total lines: {result.TotalLines:N0}");
            Console.WriteLine($"  Valid lines: {result.ValidLines:N0}");
            Console.WriteLine($"  Parse errors: {result.ParseErrors}");
            Console.WriteLine($"  Schema errors: {result.SchemaErrors}");
            Console.WriteLine($"  Timestamp errors: {result.TimestampErrors}");

            if (result.Errors.Count > 0)
            {
                Console.WriteLine($"  First 5 errors:");
                foreach (var error in result.Errors.Take(5))
                {
                    Console.WriteLine($"    {error}");
                }
            }

            totalValid += result.ValidLines;
            totalErrors += result.ParseErrors + result.SchemaErrors + result.TimestampErrors;
            Console.WriteLine();
        }

        Console.WriteLine("=== SUMMARY ===");
        Console.WriteLine($"Total valid lines: {totalValid:N0}");
        Console.WriteLine($"Total errors: {totalErrors}");
        Console.WriteLine($"Validation: {(totalErrors == 0 ? "PASSED" : "FAILED")}");
    }
}
