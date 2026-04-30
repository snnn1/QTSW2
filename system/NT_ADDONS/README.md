# NT_ADDONS Legacy Mirror

`system/NT_ADDONS` is not the runtime NinjaTrader source.

Runtime deploy uses the pre-built `Robot.Core.dll` from `system/RobotCore_For_NinjaTrader` plus `RobotSimStrategy.cs`. NinjaTrader does not compile this AddOns tree during the normal deploy path.

Use `tools/sync_nt_addons_from_robotcore.ps1 -CheckOnly` only for explicit legacy mirror audits. Run the sync script without `-CheckOnly` only when you intentionally want to refresh this mirror; it may create many files because the mirror is currently partial.
