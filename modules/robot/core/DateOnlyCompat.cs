using System;
using System.Globalization;

namespace QTSW2.Robot.Core;

/// <summary>
/// Compatibility shim for DateOnly (.NET 6+) to work with .NET Framework 4.8.
/// Provides minimal API surface needed by Robot.Core.
/// </summary>
public struct DateOnly : IComparable<DateOnly>, IEquatable<DateOnly>
{
    private readonly DateTime _date;

    public DateOnly(int year, int month, int day)
    {
        _date = new DateTime(year, month, day);
    }

    private DateOnly(DateTime date)
    {
        _date = date.Date;
    }

    public int Year => _date.Year;
    public int Month => _date.Month;
    public int Day => _date.Day;

    public static DateOnly FromDateTime(DateTime dateTime) => new DateOnly(dateTime);

    public static bool TryParseExact(string input, string format, IFormatProvider? provider, DateTimeStyles style, out DateOnly result)
    {
        if (DateTime.TryParseExact(input, format, provider, style, out var dt))
        {
            result = new DateOnly(dt);
            return true;
        }
        result = default;
        return false;
    }

    public override string ToString() => _date.ToString("yyyy-MM-dd");
    public string ToString(string format) => _date.ToString(format);
    public string ToString(string format, IFormatProvider? provider) => _date.ToString(format, provider);

    public int CompareTo(DateOnly other) => _date.CompareTo(other._date);
    public bool Equals(DateOnly other) => _date.Equals(other._date);
    public override bool Equals(object? obj) => obj is DateOnly other && Equals(other);
    public override int GetHashCode() => _date.GetHashCode();

    public static bool operator ==(DateOnly left, DateOnly right) => left.Equals(right);
    public static bool operator !=(DateOnly left, DateOnly right) => !left.Equals(right);
    public static bool operator <(DateOnly left, DateOnly right) => left.CompareTo(right) < 0;
    public static bool operator >(DateOnly left, DateOnly right) => left.CompareTo(right) > 0;
    public static bool operator <=(DateOnly left, DateOnly right) => left.CompareTo(right) <= 0;
    public static bool operator >=(DateOnly left, DateOnly right) => left.CompareTo(right) >= 0;
}
