namespace QTSW2.Robot.Core;

/// <summary>
/// NinjaTrader connection status enumeration (maps from NinjaTrader.Cbi.ConnectionStatus).
/// </summary>
public enum ConnectionStatus
{
    Connected,
    ConnectionLost,
    Disconnected,
    ConnectionError
}
