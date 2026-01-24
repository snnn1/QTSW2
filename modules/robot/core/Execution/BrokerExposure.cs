namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Broker exposure per instrument (derived, never commanded).
/// This is computed from AccountSnapshot - it represents what the broker currently holds.
/// </summary>
public class BrokerExposure
{
    public string Instrument { get; set; } = "";
    public int NetQuantity { get; set; }  // What broker currently holds (positive = long, negative = short, zero = flat)
    
    // Derived from AccountSnapshot - never commanded
}
