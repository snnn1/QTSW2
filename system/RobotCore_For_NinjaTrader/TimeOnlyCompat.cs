using System;

namespace QTSW2.Robot.Core;

/// <summary>
/// Compatibility shim for TimeOnly (.NET 6+) to work with .NET Framework 4.8.
/// Provides minimal API surface needed by Robot.Core.
/// </summary>
public struct TimeOnly : IComparable<TimeOnly>, IEquatable<TimeOnly>
{
    private readonly TimeSpan _timeOfDay;

    public TimeOnly(int hour, int minute) : this(hour, minute, 0)
    {
    }

    public TimeOnly(int hour, int minute, int second)
    {
        if (hour < 0 || hour >= 24)
            throw new ArgumentOutOfRangeException(nameof(hour));
        if (minute < 0 || minute >= 60)
            throw new ArgumentOutOfRangeException(nameof(minute));
        if (second < 0 || second >= 60)
            throw new ArgumentOutOfRangeException(nameof(second));
        
        _timeOfDay = new TimeSpan(hour, minute, second);
    }

    private TimeOnly(TimeSpan timeOfDay)
    {
        _timeOfDay = timeOfDay;
    }

    public int Hour => _timeOfDay.Hours;
    public int Minute => _timeOfDay.Minutes;
    public int Second => _timeOfDay.Seconds;

    public static TimeOnly FromDateTime(DateTime dateTime) => new TimeOnly(dateTime.TimeOfDay);

    public static bool operator <(TimeOnly left, TimeOnly right) => left._timeOfDay < right._timeOfDay;
    public static bool operator >(TimeOnly left, TimeOnly right) => left._timeOfDay > right._timeOfDay;
    public static bool operator <=(TimeOnly left, TimeOnly right) => left._timeOfDay <= right._timeOfDay;
    public static bool operator >=(TimeOnly left, TimeOnly right) => left._timeOfDay >= right._timeOfDay;
    public static bool operator ==(TimeOnly left, TimeOnly right) => left._timeOfDay == right._timeOfDay;
    public static bool operator !=(TimeOnly left, TimeOnly right) => left._timeOfDay != right._timeOfDay;

    public override string ToString() => _timeOfDay.ToString(@"hh\:mm");
    public string ToString(string format) => DateTime.Today.Add(_timeOfDay).ToString(format);
    public string ToString(string format, IFormatProvider? provider) => DateTime.Today.Add(_timeOfDay).ToString(format, provider);

    public int CompareTo(TimeOnly other) => _timeOfDay.CompareTo(other._timeOfDay);
    public bool Equals(TimeOnly other) => _timeOfDay.Equals(other._timeOfDay);
    public override bool Equals(object? obj) => obj is TimeOnly other && Equals(other);
    public override int GetHashCode() => _timeOfDay.GetHashCode();
}
