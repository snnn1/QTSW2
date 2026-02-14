using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace QTSW2.Robot.Core;

public sealed class ExecutionPolicy
{
    public string schema { get; set; } = "";
    
    // PHASE 4: Case-insensitive dictionaries (normalized at load time)
    private readonly Dictionary<string, CanonicalMarketPolicy> _canonicalMarkets;
    
    public Dictionary<string, CanonicalMarketPolicy> canonical_markets => _canonicalMarkets;
    
    /// <summary>When true, canonical market lock is disabled (multiple instances per market allowed).</summary>
    public bool DisableCanonicalMarketLock { get; }

    // Private constructor - use LoadFromFile
    private ExecutionPolicy(Dictionary<string, CanonicalMarketPolicy> canonicalMarkets, string schema, bool disableCanonicalMarketLock)
    {
        _canonicalMarkets = canonicalMarkets;
        this.schema = schema;
        DisableCanonicalMarketLock = disableCanonicalMarketLock;
    }
    
    public static ExecutionPolicy LoadFromFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Execution policy file not found: {path}");
        }
        
        var json = File.ReadAllText(path);
        var rawPolicy = JsonUtil.Deserialize<ExecutionPolicyRaw>(json);
        if (rawPolicy == null)
        {
            throw new InvalidOperationException("Failed to parse execution policy JSON.");
        }
        
        // Normalize keys to case-insensitive dictionaries
        var normalizedCanonicalMarkets = new Dictionary<string, CanonicalMarketPolicy>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in rawPolicy.canonical_markets ?? new Dictionary<string, CanonicalMarketPolicyRaw>())
        {
            var canonicalKey = kvp.Key.Trim().ToUpperInvariant();
            var rawMarketPolicy = kvp.Value;
            
            if (rawMarketPolicy == null)
            {
                throw new InvalidOperationException($"Execution policy canonical market '{canonicalKey}' has null policy.");
            }
            
            // Normalize execution_instruments dictionary
            var normalizedExecInstruments = new Dictionary<string, ExecutionInstrumentPolicy>(StringComparer.OrdinalIgnoreCase);
            foreach (var execKvp in rawMarketPolicy.execution_instruments ?? new Dictionary<string, ExecutionInstrumentPolicyRaw>())
            {
                var execKey = execKvp.Key.Trim().ToUpperInvariant();
                var rawInstPolicy = execKvp.Value;
                
                if (rawInstPolicy == null)
                {
                    throw new InvalidOperationException($"Execution policy execution instrument '{execKey}' under canonical market '{canonicalKey}' has null policy.");
                }
                
                normalizedExecInstruments[execKey] = new ExecutionInstrumentPolicy
                {
                    enabled = rawInstPolicy.enabled,
                    base_size = rawInstPolicy.base_size,
                    max_size = rawInstPolicy.max_size
                };
            }
            
            normalizedCanonicalMarkets[canonicalKey] = new CanonicalMarketPolicy(normalizedExecInstruments);
        }
        
        var policy = new ExecutionPolicy(normalizedCanonicalMarkets, rawPolicy.schema ?? "", rawPolicy.disable_canonical_market_lock);
        policy.ValidateOrThrow();
        return policy;
    }
    
    public void ValidateOrThrow()
    {
        // Schema validation
        if (string.IsNullOrWhiteSpace(schema))
        {
            throw new InvalidOperationException("Execution policy schema missing or empty.");
        }
        if (schema != "qtsw2.execution_policy")
        {
            throw new InvalidOperationException($"Execution policy schema must be 'qtsw2.execution_policy' (got '{schema}').");
        }
        
        // Canonical markets validation
        if (_canonicalMarkets == null || _canonicalMarkets.Count == 0)
        {
            throw new InvalidOperationException("Execution policy canonical_markets missing or empty.");
        }
        
        // Validate each canonical market (internal consistency only)
        foreach (var kvp in _canonicalMarkets)
        {
            var canonicalMarket = kvp.Key;
            var marketPolicy = kvp.Value;
            
            marketPolicy.ValidateOrThrow(canonicalMarket);
        }
    }
    
    /// <summary>
    /// Get execution instrument policy for a canonical market and execution instrument.
    /// </summary>
    /// <param name="canonicalInstrument">Canonical instrument (e.g., "ES", "NQ")</param>
    /// <param name="executionInstrument">Execution instrument (e.g., "ES", "MES")</param>
    /// <returns>Execution instrument policy, or null if not found</returns>
    public ExecutionInstrumentPolicy? GetExecutionInstrumentPolicy(string canonicalInstrument, string executionInstrument)
    {
        var canonicalUpper = canonicalInstrument.Trim().ToUpperInvariant();
        var execUpper = executionInstrument.Trim().ToUpperInvariant();
        
        if (!_canonicalMarkets.TryGetValue(canonicalUpper, out var marketPolicy))
        {
            return null;
        }
        
        if (!marketPolicy.execution_instruments.TryGetValue(execUpper, out var instPolicy))
        {
            return null;
        }
        
        return instPolicy;
    }
    
    /// <summary>
    /// Check if canonical market exists in policy.
    /// </summary>
    public bool HasCanonicalMarket(string canonicalInstrument)
    {
        var canonicalUpper = canonicalInstrument.Trim().ToUpperInvariant();
        return _canonicalMarkets.ContainsKey(canonicalUpper);
    }
    
    /// <summary>
    /// Get the enabled execution instrument for a canonical market.
    /// Policy requires exactly one enabled execution instrument per canonical market.
    /// </summary>
    /// <param name="canonicalInstrument">Canonical instrument (e.g., "ES", "NQ")</param>
    /// <returns>Enabled execution instrument (e.g., "MES", "MNQ"), or null if not found</returns>
    public string? GetEnabledExecutionInstrument(string canonicalInstrument)
    {
        var canonicalUpper = canonicalInstrument.Trim().ToUpperInvariant();
        
        if (!_canonicalMarkets.TryGetValue(canonicalUpper, out var marketPolicy))
        {
            return null;
        }
        
        // Find the enabled execution instrument (policy guarantees exactly one)
        foreach (var kvp in marketPolicy.execution_instruments)
        {
            if (kvp.Value.enabled)
            {
                return kvp.Key;
            }
        }
        
        return null; // No enabled instrument found (should not happen if policy is valid)
    }
}

// Raw deserialization model (before normalization)
internal sealed class ExecutionPolicyRaw
{
    public string? schema { get; set; }
    public Dictionary<string, CanonicalMarketPolicyRaw>? canonical_markets { get; set; }
    /// <summary>When true, skip canonical market lock (allow multiple instances per market). Default: false.</summary>
    public bool disable_canonical_market_lock { get; set; }
}

internal sealed class CanonicalMarketPolicyRaw
{
    public Dictionary<string, ExecutionInstrumentPolicyRaw>? execution_instruments { get; set; }
}

internal sealed class ExecutionInstrumentPolicyRaw
{
    public bool enabled { get; set; }
    public int base_size { get; set; }
    public int max_size { get; set; }
}

public sealed class CanonicalMarketPolicy
{
    // PHASE 4: Case-insensitive dictionary (normalized at load time)
    private readonly Dictionary<string, ExecutionInstrumentPolicy> _executionInstruments;
    
    public Dictionary<string, ExecutionInstrumentPolicy> execution_instruments => _executionInstruments;
    
    public CanonicalMarketPolicy(Dictionary<string, ExecutionInstrumentPolicy> executionInstruments)
    {
        _executionInstruments = executionInstruments ?? throw new ArgumentNullException(nameof(executionInstruments));
    }
    
    public void ValidateOrThrow(string canonicalMarket)
    {
        if (_executionInstruments.Count == 0)
        {
            throw new InvalidOperationException($"Execution policy canonical market '{canonicalMarket}' has no execution_instruments.");
        }
        
        // Validate each execution instrument
        var enabledCount = 0;
        foreach (var kvp in _executionInstruments)
        {
            var execInst = kvp.Key;
            var instPolicy = kvp.Value;
            
            instPolicy.ValidateOrThrow(execInst, canonicalMarket);
            
            if (instPolicy.enabled)
            {
                enabledCount++;
            }
        }
        
        // Exactly one enabled execution instrument per canonical market
        if (enabledCount == 0)
        {
            throw new InvalidOperationException($"Execution policy canonical market '{canonicalMarket}' has zero enabled execution instruments. Exactly one must be enabled.");
        }
        if (enabledCount > 1)
        {
            throw new InvalidOperationException($"Execution policy canonical market '{canonicalMarket}' has {enabledCount} enabled execution instruments. Exactly one must be enabled.");
        }
    }
}

public sealed class ExecutionInstrumentPolicy
{
    public bool enabled { get; set; }
    public int base_size { get; set; }
    public int max_size { get; set; }
    
    public void ValidateOrThrow(string executionInstrument, string canonicalMarket)
    {
        if (base_size <= 0)
        {
            throw new InvalidOperationException($"Execution policy execution instrument '{executionInstrument}' under canonical market '{canonicalMarket}' has base_size <= 0 (got {base_size}).");
        }
        
        if (max_size <= 0)
        {
            throw new InvalidOperationException($"Execution policy execution instrument '{executionInstrument}' under canonical market '{canonicalMarket}' has max_size <= 0 (got {max_size}).");
        }
        
        if (base_size > max_size)
        {
            throw new InvalidOperationException($"Execution policy execution instrument '{executionInstrument}' under canonical market '{canonicalMarket}' has base_size ({base_size}) > max_size ({max_size}).");
        }
    }
}
