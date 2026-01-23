// SINGLE SOURCE OF TRUTH
// This file provides sensitive data filtering for log events.
// It is compiled into Robot.Core.dll and should be referenced from that DLL.
//
// Linked into: Robot.Core.csproj (modules/robot/core/)
// Referenced by: RobotCore_For_NinjaTrader (via Robot.Core.dll)

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace QTSW2.Robot.Core;

/// <summary>
/// Filters and redacts sensitive data from log events.
/// Prevents API keys, tokens, passwords, and PII from appearing in logs.
/// </summary>
public static class SensitiveDataFilter
{
    private static readonly Regex[] _sensitivePatterns = new[]
    {
        // API keys (common patterns)
        new Regex(@"api[_-]?key["":\s=]+([a-zA-Z0-9]{20,})", RegexOptions.IgnoreCase),
        new Regex(@"apikey["":\s=]+([a-zA-Z0-9]{20,})", RegexOptions.IgnoreCase),
        new Regex(@"api_key["":\s=]+([a-zA-Z0-9]{20,})", RegexOptions.IgnoreCase),
        
        // Tokens
        new Regex(@"token["":\s=]+([a-zA-Z0-9]{20,})", RegexOptions.IgnoreCase),
        new Regex(@"access[_-]?token["":\s=]+([a-zA-Z0-9]{20,})", RegexOptions.IgnoreCase),
        new Regex(@"bearer["":\s=]+([a-zA-Z0-9]{20,})", RegexOptions.IgnoreCase),
        
        // Passwords
        new Regex(@"password["":\s=]+([^\s""']+)", RegexOptions.IgnoreCase),
        new Regex(@"pwd["":\s=]+([^\s""']+)", RegexOptions.IgnoreCase),
        new Regex(@"pass["":\s=]+([^\s""']+)", RegexOptions.IgnoreCase),
        
        // Secrets
        new Regex(@"secret["":\s=]+([a-zA-Z0-9]{20,})", RegexOptions.IgnoreCase),
        new Regex(@"secret[_-]?key["":\s=]+([a-zA-Z0-9]{20,})", RegexOptions.IgnoreCase),
        
        // Account numbers (keep last 4 digits)
        new Regex(@"account["":\s=]+([a-zA-Z0-9]{8,})", RegexOptions.IgnoreCase),
    };
    
    private const string REDACTION_PLACEHOLDER = "[REDACTED]";
    private const string ACCOUNT_REDACTION_PATTERN = "****{0}"; // Last 4 digits
    
    /// <summary>
    /// Filter sensitive data from a dictionary (log event data).
    /// Returns a new dictionary with sensitive values redacted.
    /// </summary>
    public static Dictionary<string, object?> FilterDictionary(Dictionary<string, object?>? data)
    {
        if (data == null) return new Dictionary<string, object?>();
        
        var filtered = new Dictionary<string, object?>();
        foreach (var kvp in data)
        {
            var key = kvp.Key;
            var value = kvp.Value;
            
            // Check key name for sensitive indicators
            if (IsSensitiveKey(key))
            {
                filtered[key] = REDACTION_PLACEHOLDER;
                continue;
            }
            
            // Filter value based on type
            if (value is string str)
            {
                filtered[key] = FilterString(key, str);
            }
            else if (value is Dictionary<string, object?> nestedDict)
            {
                filtered[key] = FilterDictionary(nestedDict);
            }
            else
            {
                // For other types, check string representation
                var strValue = value?.ToString();
                if (!string.IsNullOrEmpty(strValue))
                {
                    filtered[key] = FilterString(key, strValue);
                }
                else
                {
                    filtered[key] = value;
                }
            }
        }
        
        return filtered;
    }
    
    /// <summary>
    /// Filter sensitive data from a string value.
    /// </summary>
    public static string FilterString(string key, string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        
        // Special handling for account numbers (keep last 4 digits)
        if (key.ToLowerInvariant().Contains("account") && value.Length >= 4)
        {
            var last4 = value.Substring(Math.Max(0, value.Length - 4));
            return string.Format(ACCOUNT_REDACTION_PATTERN, last4);
        }
        
        // Apply regex patterns
        var filtered = value;
        foreach (var pattern in _sensitivePatterns)
        {
            filtered = pattern.Replace(filtered, match =>
            {
                var fullMatch = match.Value;
                var captured = match.Groups.Count > 1 ? match.Groups[1].Value : "";
                
                // For account numbers, keep last 4 digits
                if (key.ToLowerInvariant().Contains("account") && captured.Length >= 4)
                {
                    var last4 = captured.Substring(captured.Length - 4);
                    return fullMatch.Replace(captured, string.Format(ACCOUNT_REDACTION_PATTERN, last4));
                }
                
                return fullMatch.Replace(captured, REDACTION_PLACEHOLDER);
            });
        }
        
        return filtered;
    }
    
    /// <summary>
    /// Check if a key name indicates sensitive data.
    /// </summary>
    private static bool IsSensitiveKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        var lower = key.ToLowerInvariant();
        return lower.Contains("password") ||
               lower.Contains("pwd") ||
               lower.Contains("secret") ||
               (lower.Contains("api") && lower.Contains("key")) ||
               (lower.Contains("access") && lower.Contains("token")) ||
               lower.Contains("bearer");
    }
}
