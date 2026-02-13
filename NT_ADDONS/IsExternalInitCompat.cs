namespace System.Runtime.CompilerServices;

/// <summary>
/// Compatibility shim for IsExternalInit (.NET 5+) to work with .NET Framework 4.8.
/// Required for init-only property setters (C# 9+).
/// </summary>
internal static class IsExternalInit
{
}
