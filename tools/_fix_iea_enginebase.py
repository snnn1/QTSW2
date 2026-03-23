import re
from pathlib import Path

files = [
    Path(r"c:/Users/jakej/QTSW2/RobotCore_For_NinjaTrader/Execution/InstrumentExecutionAuthority.RecoveryPhase3.cs"),
    Path(r"c:/Users/jakej/QTSW2/RobotCore_For_NinjaTrader/Execution/InstrumentExecutionAuthority.BootstrapPhase4.cs"),
    Path(r"c:/Users/jakej/QTSW2/RobotCore_For_NinjaTrader/Execution/InstrumentExecutionAuthority.SupervisoryPhase5.cs"),
    Path(r"c:/Users/jakej/QTSW2/RobotCore_For_NinjaTrader/Execution/InstrumentExecutionAuthority.Flatten.cs"),
]
for p in files:
    s = p.read_text(encoding="utf-8")
    s2 = re.sub(
        r'RobotEvents\.EngineBase\(utcNow, "", instrument, "([^"]+)", new',
        r'RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "\1", state: "ENGINE", new',
        s,
    )
    s2 = re.sub(
        r'RobotEvents\.EngineBase\(utcNow, "", ExecutionInstrumentKey, "([^"]+)", new',
        r'RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "\1", state: "ENGINE", new',
        s2,
    )
    s2 = re.sub(
        r'RobotEvents\.EngineBase\(utcNow, "", instrumentKey, "([^"]+)", new',
        r'RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "\1", state: "ENGINE", new',
        s2,
    )
    if s2 != s:
        p.write_text(s2, encoding="utf-8")
        print("updated", p.name)
    else:
        print("no change", p.name)
