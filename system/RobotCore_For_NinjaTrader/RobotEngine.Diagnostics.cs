using System;
using System.Collections.Generic;
using System.IO;

namespace QTSW2.Robot.Core;

using QTSW2.Robot.Core.Execution;

public sealed partial class RobotEngine
{
    /// <summary>
    /// Report execution policy validation failure to HealthMonitor.
    /// Centralized to ensure one notification per validation pass.
    /// Uses boolean latch to prevent multiple calls even if validation is retried/re-entered.
    /// </summary>
    private void ReportExecutionPolicyFailure(
        string summary,
        List<string>? details = null,
        Dictionary<string, object>? context = null)
    {
        // Guard: Only report once per engine startup
        if (_executionPolicyFailureReported)
            return;

        if (_healthMonitor == null)
            return;

        // Set latch BEFORE building payload (fail-safe: if payload building fails, we still won't retry)
        _executionPolicyFailureReported = true;

        var payload = new Dictionary<string, object>
        {
            ["summary"] = summary,
            ["file_path"] = _executionPolicyPath
        };

        if (details != null && details.Count > 0)
            payload["details"] = details;

        if (context != null)
        {
            foreach (var kvp in context)
                payload[kvp.Key] = kvp.Value;
        }

        // Optional: Add execution policy hash for deduplication
        if (_executionPolicy != null)
        {
            try
            {
                var policyJson = JsonUtil.Serialize(_executionPolicy);
                using (var sha256 = System.Security.Cryptography.SHA256.Create())
                {
                    var policyBytes = System.Text.Encoding.UTF8.GetBytes(policyJson);
                    var hashBytes = sha256.ComputeHash(policyBytes);
                    var policyHash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
                    payload["execution_policy_hash"] = policyHash;
                }
            }
            catch
            {
                // Hash computation failure shouldn't block notification
            }
        }

        _healthMonitor.ReportCritical("EXECUTION_POLICY_VALIDATION_FAILED", payload);
    }

    private bool _healthMonitorInitAttempted = false;

    /// <summary>
    /// Initialize health monitor (fail-closed: if config missing/invalid, monitoring disabled).
    /// This is delayed until Start() so that all health-monitor-related logs include run_id.
    /// </summary>
    private void InitializeHealthMonitorIfNeeded()
    {
        if (_healthMonitorInitAttempted)
            return;
        _healthMonitorInitAttempted = true;

        try
        {
            var healthMonitorPath = Path.Combine(_root, "configs", "robot", "health_monitor.json");
            if (File.Exists(healthMonitorPath))
            {
                var healthMonitorJson = File.ReadAllText(healthMonitorPath);
                var healthMonitorConfig = JsonUtil.Deserialize<HealthMonitorConfig>(healthMonitorJson);
                if (healthMonitorConfig != null)
                {
                    // Secrets handling: allow a local (gitignored) secrets file to provide credentials
                    // File: configs/robot/health_monitor.secrets.json
                    var healthMonitorSecretsPath = Path.Combine(_root, "configs", "robot", "health_monitor.secrets.json");
                    if (File.Exists(healthMonitorSecretsPath))
                    {
                        try
                        {
                            var secretsJson = File.ReadAllText(healthMonitorSecretsPath);
                            var secrets = JsonUtil.Deserialize<Dictionary<string, object>>(secretsJson);
                            if (secrets != null)
                            {
                                if (secrets.TryGetValue("pushover_enabled", out var poEnabledObj))
                                {
                                    var poEnabledStr = Convert.ToString(poEnabledObj);
                                    if (!string.IsNullOrWhiteSpace(poEnabledStr) && bool.TryParse(poEnabledStr, out var poEnabled))
                                        healthMonitorConfig.pushover_enabled = poEnabled;
                                }

                                if (secrets.TryGetValue("pushover_user_key", out var userKeyObj))
                                {
                                    var userKey = Convert.ToString(userKeyObj);
                                    if (!string.IsNullOrWhiteSpace(userKey))
                                        healthMonitorConfig.pushover_user_key = userKey;
                                }

                                if (secrets.TryGetValue("pushover_app_token", out var appTokenObj))
                                {
                                    var appToken = Convert.ToString(appTokenObj);
                                    if (!string.IsNullOrWhiteSpace(appToken))
                                        healthMonitorConfig.pushover_app_token = appToken;
                                }
                            }
                        }
                        catch
                        {
                            // Fail-closed: ignore secrets file parse errors.
                        }
                    }

                    // Secrets handling: allow environment variables to provide credentials (never store in git)
                    try
                    {
                        var envHmEnabled = Environment.GetEnvironmentVariable("QTSW2_HEALTH_MONITOR_ENABLED");
                        if (!string.IsNullOrWhiteSpace(envHmEnabled) && bool.TryParse(envHmEnabled, out var hmEnabled))
                        {
                            healthMonitorConfig.enabled = hmEnabled;
                        }

                        var envPushoverEnabled = Environment.GetEnvironmentVariable("QTSW2_PUSHOVER_ENABLED");
                        if (!string.IsNullOrWhiteSpace(envPushoverEnabled) && bool.TryParse(envPushoverEnabled, out var poEnabled))
                        {
                            healthMonitorConfig.pushover_enabled = poEnabled;
                        }

                        var envUserKey =
                            Environment.GetEnvironmentVariable("QTSW2_PUSHOVER_USER_KEY") ??
                            Environment.GetEnvironmentVariable("PUSHOVER_USER_KEY");
                        if (!string.IsNullOrWhiteSpace(envUserKey))
                        {
                            healthMonitorConfig.pushover_user_key = envUserKey;
                        }

                        var envAppToken =
                            Environment.GetEnvironmentVariable("QTSW2_PUSHOVER_APP_TOKEN") ??
                            Environment.GetEnvironmentVariable("PUSHOVER_APP_TOKEN");
                        if (!string.IsNullOrWhiteSpace(envAppToken))
                        {
                            healthMonitorConfig.pushover_app_token = envAppToken;
                        }
                    }
                    catch
                    {
                        // Fail-closed: if env parsing fails, continue with file config as-is.
                    }

                    // Log config load result for debugging
                    LogEvent(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "HEALTH_MONITOR_CONFIG_LOADED", state: "ENGINE",
                        new
                        {
                            enabled = healthMonitorConfig.enabled,
                            enabled_property = healthMonitorConfig.enabled,
                            pushover_enabled = healthMonitorConfig.pushover_enabled,
                            pushover_user_key_length = healthMonitorConfig.pushover_user_key?.Length ?? 0,
                            pushover_app_token_length = healthMonitorConfig.pushover_app_token?.Length ?? 0,
                            config_not_null = true
                        }));

                    // If Pushover is enabled but not configured, log a loud warning event (no secrets).
                    var pushoverConfigured = healthMonitorConfig.pushover_enabled &&
                                             !string.IsNullOrWhiteSpace(healthMonitorConfig.pushover_user_key) &&
                                             !string.IsNullOrWhiteSpace(healthMonitorConfig.pushover_app_token);
                    if (healthMonitorConfig.pushover_enabled && !pushoverConfigured)
                    {
                        LogEvent(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "PUSHOVER_CONFIG_MISSING", state: "ENGINE",
                            new
                            {
                                pushover_enabled = true,
                                pushover_configured = false,
                                secrets_file = Path.Combine(_root, "configs", "robot", "health_monitor.secrets.json"),
                                env_vars = "QTSW2_PUSHOVER_USER_KEY / QTSW2_PUSHOVER_APP_TOKEN (or PUSHOVER_USER_KEY / PUSHOVER_APP_TOKEN)",
                                note = "Pushover enabled but credentials are missing. No push notifications will be sent until configured."
                            }));
                    }

                    if (healthMonitorConfig.enabled)
                    {
                        _healthMonitor = new HealthMonitor(_persistenceBase, healthMonitorConfig, _log);
                        _healthMonitor.SetPlaybackTelemetryMode(_isolatedPlaybackPersistence);
                        _healthMonitor.SetActiveStreamsCallback(() => HasActiveStreams());
                        _healthMonitor.SetMarketOpenCallback(() => _time != null && TimeService.IsCmeMarketOpen(_time.ConvertUtcToChicago(DateTimeOffset.UtcNow)));
                        _healthMonitor.SetConnectivityShutdownCallback((reason, callbackUtc, payload) =>
                            RequestConnectivityHealthShutdown(reason, callbackUtc, payload));
                    }
                    else
                    {
                        LogEvent(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "HEALTH_MONITOR_DISABLED", state: "ENGINE",
                            new { reason = "config_enabled_false" }));
                    }
                }
                else
                {
                    LogEvent(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "HEALTH_MONITOR_CONFIG_NULL", state: "ENGINE",
                        new { reason = "deserialization_returned_null" }));
                }
            }
            else
            {
                LogEvent(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "HEALTH_MONITOR_CONFIG_MISSING", state: "ENGINE",
                    new { path = healthMonitorPath }));
            }
        }
        catch (Exception ex)
        {
            LogEvent(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "HEALTH_MONITOR_CONFIG_ERROR", state: "ENGINE",
                new { error = ex.Message, error_type = ex.GetType().Name }));
        }
    }

    /// <summary>
    /// Unified logging method: converts old event format to RobotLogEvent and logs via async service.
    /// Falls back to RobotLogger.Write() if conversion fails (which handles its own fallback logic).
    /// </summary>
    private void LogEvent(object evt)
    {
        // Try async logging service first (preferred path)
        if (_loggingService != null && evt is Dictionary<string, object?> dict)
        {
            try
            {
                var logEvent = ConvertToRobotLogEvent(dict);
                _loggingService.Log(logEvent);
                return; // Successfully logged via async service
            }
            catch (Exception ex)
            {
                // Fail loudly: log conversion failure as ERROR event before falling back
                try
                {
                    var utcNow = DateTimeOffset.UtcNow;
                    var errorEvent = RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "LOG_CONVERSION_ERROR", state: "ENGINE",
                        new
                        {
                            exception_type = ex.GetType().Name,
                            error = ex.Message,
                            stack_trace = ex.StackTrace != null && ex.StackTrace.Length > 500 ? ex.StackTrace.Substring(0, 500) : ex.StackTrace,
                            original_event_type = dict.TryGetValue("event_type", out var et) ? et?.ToString() : "UNKNOWN",
                            note = "Failed to convert event dictionary to RobotLogEvent - falling back to RobotLogger.Write()"
                        });
                    _log.Write(errorEvent);
                }
                catch
                {
                    // If even error logging fails, silently fall through to fallback
                }
            }
        }

        // Fallback to RobotLogger.Write() (handles conversion and fallback internally)
        _log.Write(evt);
    }

    /// <summary>
    /// Convert old event dictionary format to RobotLogEvent.
    /// Handles EngineBase, Base, and ExecutionBase event types.
    /// </summary>
    private RobotLogEvent ConvertToRobotLogEvent(Dictionary<string, object?> dict)
    {
        var utcNow = DateTimeOffset.UtcNow;
        if (dict.TryGetValue("ts_utc", out var tsObj) && tsObj is string tsStr && DateTimeOffset.TryParse(tsStr, out var parsed))
        {
            utcNow = parsed;
        }

        var eventType = dict.TryGetValue("event_type", out var et) ? et?.ToString() ?? "" : "";
        // Use centralized event registry for level assignment (replaces fragile string matching)
        var level = RobotEventTypes.GetLevel(eventType);

        // Determine source based on stream field
        var source = "RobotEngine";
        if (dict.TryGetValue("stream", out var streamObj) && streamObj is string streamStr)
        {
            if (streamStr == "__engine__")
                source = "RobotEngine";
            else if (streamStr.StartsWith("EXECUTION") || dict.ContainsKey("intent_id"))
                source = "ExecutionAdapter";
            else
                source = "StreamStateMachine";
        }

        var instrument = dict.TryGetValue("instrument", out var inst) ? inst?.ToString() ?? "" : "";
        var message = eventType; // Use event_type as message

        // Extract data payload - include all non-standard fields in data
        var data = new Dictionary<string, object?>();
        if (dict.TryGetValue("data", out var dataObj))
        {
            if (dataObj is Dictionary<string, object?> dataDict)
            {
                foreach (var kvp in dataDict)
                    data[kvp.Key] = kvp.Value;
            }
            else if (dataObj is System.Collections.IDictionary dataMap)
            {
                foreach (System.Collections.DictionaryEntry entry in dataMap)
                {
                    var key = entry.Key?.ToString();
                    if (!string.IsNullOrWhiteSpace(key))
                        data[key] = entry.Value;
                }
            }
            else if (dataObj != null)
            {
                var converted = ConvertAnonymousObjectToDictionary(dataObj);
                if (converted != null)
                {
                    foreach (var kvp in converted)
                        data[kvp.Key] = kvp.Value;
                }
                else
                {
                    data["payload"] = dataObj;
                }
            }
        }

        // Include additional context fields in data
        foreach (var kvp in dict)
        {
            if (kvp.Key != "ts_utc" && kvp.Key != "ts_chicago" && kvp.Key != "event_type" &&
                kvp.Key != "instrument" && kvp.Key != "data" && kvp.Key != "stream")
            {
                data[kvp.Key] = kvp.Value;
            }
        }

        return new RobotLogEvent(utcNow, level, source, instrument, eventType, message, runId: _runId, data: data.Count > 0 ? data : null);
    }

    private static Dictionary<string, object?>? ConvertAnonymousObjectToDictionary(object obj)
    {
        var dict = new Dictionary<string, object?>();
        var props = obj.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        foreach (var prop in props)
        {
            try
            {
                dict[prop.Name] = prop.GetValue(obj);
            }
            catch
            {
                // Logging conversion must never disturb runtime flow.
            }
        }

        return dict.Count > 0 ? dict : null;
    }

    /// <summary>
    /// Expose logging service for components that need direct access (e.g., adapters).
    /// </summary>
    internal RobotLoggingService? GetLoggingService() => _loggingService;

    /// <summary>
    /// Expose health monitor for strategy files (replaces reflection-based access).
    /// </summary>
    internal HealthMonitor? GetHealthMonitor() => _healthMonitor;

    /// <summary>
    /// Send a test notification to verify Pushover is working.
    /// Public method for testing from strategy or external code.
    /// </summary>
    public void SendTestNotification()
    {
        _healthMonitor?.SendTestNotification();
    }

    /// <summary>
    /// Expose execution adapter for strategy files (replaces reflection-based access).
    /// Public so NinjaTrader.Custom assembly can access when using DLL-only deployment.
    /// </summary>
    public IExecutionAdapter? GetExecutionAdapter() => _executionAdapter;

    /// <summary>
    /// Submit test inject entry (RegisterIntent + RegisterIntentPolicy + SubmitEntryOrder).
    /// Used by RobotSimStrategy when TestInjectTradeOnStart is enabled.
    /// Returns null if adapter does not support intent registration.
    /// </summary>
    public OrderSubmissionResult? SubmitTestInjectEntry(Intent intent, int qty, DateTimeOffset utcNow)
    {
        if (_executionAdapter == null) return null;
        if (_executionAdapter is not IIntentRegistrationAdapter reg) return null;
        var intentId = intent.ComputeIntentId();
        reg.RegisterIntent(intent);
        reg.RegisterIntentPolicy(intentId, qty, qty, intent.Instrument, intent.ExecutionInstrument, "TEST_INJECT");
        return _executionAdapter.SubmitEntryOrder(intentId, intent.ExecutionInstrument, intent.Direction ?? "Long", null, qty, "MARKET", null, utcNow);
    }

    /// <summary>
    /// Expose time service for components that need direct access (e.g., bar providers).
    /// </summary>
    internal TimeService? GetTimeService() => _time;

    /// <summary>
    /// Public method to log engine events from external callers (e.g., RobotSimStrategy).
    /// </summary>
    public void LogEngineEvent(DateTimeOffset utcNow, string eventType, object? data = null)
    {
        lock (_engineLock)
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: eventType, state: "ENGINE", data));
        }
    }

    private bool _robotBuildSignatureEmitted;
    private static readonly object RobotBuildSignatureCacheLock = new();
    private static string _cachedRobotBuildSignaturePath = "";
    private static DateTime _cachedRobotBuildSignatureWriteUtc;
    private static long _cachedRobotBuildSignatureLength = -1;
    private static string _cachedRobotBuildSignatureHash = "";

    /// <summary>
    /// Emit ROBOT_BUILD_SIGNATURE once per engine run from the DLL start path.
    /// Proves which Robot.Core.dll is actually loaded (assembly path, hash, last write time).
    /// </summary>
    public void LogEngineBuildSignatureIfNeeded(DateTimeOffset utcNow, string instrumentName)
    {
        if (_robotBuildSignatureEmitted) return;
        lock (_engineLock)
        {
            if (_robotBuildSignatureEmitted) return;
            _robotBuildSignatureEmitted = true;
        }
        var asm = typeof(RobotEngine).Assembly;
        var loc = asm.Location;
        DateTime utcWrite = default;
        long assemblyLength = -1;
        try
        {
            if (!string.IsNullOrEmpty(loc) && File.Exists(loc))
            {
                utcWrite = File.GetLastWriteTimeUtc(loc);
                assemblyLength = new FileInfo(loc).Length;
            }
        }
        catch { /* best-effort */ }
        var ver = asm.GetName().Version;
        var assemblyHash = TryGetRobotCoreAssemblyHash(loc, utcWrite, assemblyLength, out var hashCacheHit);
        var payload = new Dictionary<string, object>
        {
            ["assembly_name"] = asm.GetName().Name ?? "Robot.Core",
            ["assembly_version"] = ver?.ToString() ?? "0.0.0.0",
            ["assembly_location"] = loc ?? "(null)",
            ["assembly_hash_algorithm"] = "SHA256",
            ["assembly_hash"] = assemblyHash,
            ["assembly_hash_cache_hit"] = hashCacheHit,
            ["assembly_last_write_utc"] = utcWrite.ToString("o"),
            ["assembly_length"] = assemblyLength,
            ["build_configuration"] =
#if DEBUG
                "Debug"
#else
                "Release"
#endif
            ,
            ["instrument"] = instrumentName,
            ["note"] = "Runtime proof of loaded Robot.Core.dll; validate this signature appears in robot_ENGINE.jsonl after restart"
        };
        LogEngineEvent(utcNow, "ROBOT_BUILD_SIGNATURE", payload);
    }

    private static string TryGetRobotCoreAssemblyHash(string path, DateTime utcWrite, long assemblyLength, out bool cacheHit)
    {
        cacheHit = false;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return "unavailable";

            var fullPath = Path.GetFullPath(path.Trim());
            lock (RobotBuildSignatureCacheLock)
            {
                if (string.Equals(_cachedRobotBuildSignaturePath, fullPath, StringComparison.OrdinalIgnoreCase) &&
                    _cachedRobotBuildSignatureWriteUtc == utcWrite &&
                    _cachedRobotBuildSignatureLength == assemblyLength &&
                    !string.IsNullOrWhiteSpace(_cachedRobotBuildSignatureHash))
                {
                    cacheHit = true;
                    return _cachedRobotBuildSignatureHash;
                }

                var hash = TryComputeRobotCoreAssemblyHash(fullPath);
                _cachedRobotBuildSignaturePath = fullPath;
                _cachedRobotBuildSignatureWriteUtc = utcWrite;
                _cachedRobotBuildSignatureLength = assemblyLength;
                _cachedRobotBuildSignatureHash = hash;
                return hash;
            }
        }
        catch (Exception ex)
        {
            return "error:" + ex.GetType().Name;
        }
    }

    private static string TryComputeRobotCoreAssemblyHash(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return "unavailable";

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var bytes = sha256.ComputeHash(stream);
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }
        catch (Exception ex)
        {
            return "error:" + ex.GetType().Name;
        }
    }
}
