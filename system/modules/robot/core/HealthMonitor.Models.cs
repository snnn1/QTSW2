using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QTSW2.Robot.Core.Notifications;

namespace QTSW2.Robot.Core;

public enum ConnectionStatus
{
    Connected,
    ConnectionLost,
    Disconnected,
    ConnectionError
}
