using System;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;
using QTSW2.Robot.Core;

namespace NinjaTrader.NinjaScript.AddOns
{
    /// <summary>
    /// Connection Status AddOn - Monitors NinjaTrader connection status and logs to robot engine.
    /// This AddOn subscribes to connection status updates and forwards them to RobotEngine.
    /// 
    /// NOTE: Strategies already handle connection status via OnConnectionStatusUpdate() override.
    /// This AddOn is optional and provides global connection monitoring independent of strategies.
    /// </summary>
    public class ConnectionStatusAddOn : NinjaScriptBase
    {
        private static RobotEngine? _engine;
        private static bool _initialized = false;

        public override string LogTypeName => "ConnectionStatusAddOn";

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                // AddOns don't have Name/Description properties like Strategies
            }
            else if (State == State.Configure)
            {
                // Subscribe to connection status updates
                Connection.ConnectionStatusUpdate += OnConnectionStatusUpdate;
            }
            else if (State == State.Terminated)
            {
                // Unsubscribe from connection status updates
                Connection.ConnectionStatusUpdate -= OnConnectionStatusUpdate;
            }
        }

        /// <summary>
        /// Initialize with RobotEngine instance.
        /// Call this from your strategy after RobotEngine is created.
        /// </summary>
        public static void Initialize(RobotEngine engine)
        {
            if (_initialized)
                return;

            _engine = engine;
            _initialized = true;

            // Note: We don't log initial connection status here because:
            // 1. Connection.Options is not accessible statically from AddOn context
            // 2. Strategies already handle initial connection status via OnConnectionStatusUpdate()
            // 3. This AddOn will catch all future connection status changes
        }

        private static void OnConnectionStatusUpdate(object sender, ConnectionStatusEventArgs e)
        {
            if (_engine == null || !_initialized)
                return;

            try
            {
                var connection = e.Connection;
                if (connection == null)
                    return;

                var ntStatus = connection.Status;
                var healthMonitorStatus = ntStatus.ToHealthMonitorStatus();
                // Get connection name from event args or connection options
                var connectionName = e.Connection?.Options?.Name ?? "Unknown";

                // Forward to RobotEngine
                _engine.OnConnectionStatusUpdate(healthMonitorStatus, connectionName);
            }
            catch (Exception ex)
            {
                // Use NinjaScript logging instead of Print
                NinjaTrader.Code.Output.Process($"ConnectionStatusAddOn: Error handling connection status update: {ex.Message}", 
                    PrintTo.OutputTab1);
            }
        }
    }
}
