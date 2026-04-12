using System;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// PHASE 3: Execution context containing both canonical and execution identities.
/// Provides compile-time safety for identity routing.
/// </summary>
public sealed class ExecutionContext
{
    /// <summary>
    /// Canonical instrument (e.g., ES) - used for logic identity.
    /// </summary>
    public string CanonicalInstrument { get; }
    
    /// <summary>
    /// Canonical stream ID (e.g., ES1) - used for logic identity.
    /// </summary>
    public string CanonicalStream { get; }
    
    /// <summary>
    /// Execution instrument (e.g., MES) - used for order placement.
    /// </summary>
    public string ExecutionInstrument { get; }
    
    /// <summary>
    /// Trading date (YYYY-MM-DD).
    /// </summary>
    public string TradingDate { get; }
    
    /// <summary>
    /// Session (S1 or S2).
    /// </summary>
    public string Session { get; }
    
    /// <summary>
    /// Slot time Chicago (HH:MM).
    /// </summary>
    public string SlotTimeChicago { get; }

    public ExecutionContext(
        string canonicalInstrument,
        string canonicalStream,
        string executionInstrument,
        string tradingDate,
        string session,
        string slotTimeChicago)
    {
        if (string.IsNullOrWhiteSpace(canonicalInstrument))
            throw new ArgumentException("Canonical instrument cannot be null or empty", nameof(canonicalInstrument));
        if (string.IsNullOrWhiteSpace(canonicalStream))
            throw new ArgumentException("Canonical stream cannot be null or empty", nameof(canonicalStream));
        if (string.IsNullOrWhiteSpace(executionInstrument))
            throw new ArgumentException("Execution instrument cannot be null or empty", nameof(executionInstrument));
        if (string.IsNullOrWhiteSpace(tradingDate))
            throw new ArgumentException("Trading date cannot be null or empty", nameof(tradingDate));
        if (string.IsNullOrWhiteSpace(session))
            throw new ArgumentException("Session cannot be null or empty", nameof(session));
        if (string.IsNullOrWhiteSpace(slotTimeChicago))
            throw new ArgumentException("Slot time cannot be null or empty", nameof(slotTimeChicago));
        
        // PHASE 3: Assert canonical stream does not contain execution instrument
        // .NET Framework 4.8 compatibility: Use IndexOf instead of Contains with StringComparison
        if (canonicalStream.IndexOf(executionInstrument, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            throw new ArgumentException(
                $"PHASE 3 ASSERTION FAILED: Execution instrument '{executionInstrument}' found in canonical stream '{canonicalStream}'. " +
                $"Canonical stream must use canonical instrument '{canonicalInstrument}'.",
                nameof(canonicalStream));
        }
        
        CanonicalInstrument = canonicalInstrument.ToUpperInvariant();
        CanonicalStream = canonicalStream.ToUpperInvariant();
        ExecutionInstrument = executionInstrument.ToUpperInvariant();
        TradingDate = tradingDate;
        Session = session;
        SlotTimeChicago = slotTimeChicago;
    }
    
    /// <summary>
    /// Create ExecutionContext from StreamStateMachine.
    /// </summary>
    public static ExecutionContext FromStream(StreamStateMachine stream)
    {
        return new ExecutionContext(
            canonicalInstrument: stream.CanonicalInstrument,
            canonicalStream: stream.Stream,
            executionInstrument: stream.ExecutionInstrument,
            tradingDate: stream.TradingDate,
            session: stream.Session,
            slotTimeChicago: stream.SlotTimeChicago);
    }
}
