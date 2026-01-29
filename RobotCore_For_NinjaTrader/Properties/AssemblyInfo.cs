using System.Reflection;
using System.Runtime.Versioning;

// Only include attributes that NinjaTrader doesn't auto-generate
// NinjaTrader auto-generates: AssemblyDescription, AssemblyCopyright, AssemblyTrademark, AssemblyCulture, ComVisible
[assembly: AssemblyTitle("Robot.Core")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("Robot.Core")]

// Version information
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0")]

// Target framework attribute - explicitly set to prevent duplicate
[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
