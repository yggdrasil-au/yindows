using System;

namespace yggdrasilKernel.OS;

public readonly struct TimeZoneTransitionDefinition {
    public TimeZoneTransitionDefinition(int month, int week, int day, int dayOfWeek, bool isFixedDateRule, long timeOfDayTicks) {
        Month = month;
        Week = week;
        Day = day;
        DayOfWeek = dayOfWeek;
        IsFixedDateRule = isFixedDateRule;
        TimeOfDayTicks = timeOfDayTicks;
    }

    public int Month { get; }
    public int Week { get; }
    public int Day { get; }
    public int DayOfWeek { get; }
    public bool IsFixedDateRule { get; }
    public long TimeOfDayTicks { get; }
}

public readonly struct TimeZoneAdjustmentRuleDefinition {
    public TimeZoneAdjustmentRuleDefinition(
        long dateStartTicks,
        long dateEndTicks,
        int daylightDeltaMinutes,
        int baseUtcOffsetDeltaMinutes,
        TimeZoneTransitionDefinition daylightTransitionStart,
        TimeZoneTransitionDefinition daylightTransitionEnd
    ) {
        DateStartTicks = dateStartTicks;
        DateEndTicks = dateEndTicks;
        DaylightDeltaMinutes = daylightDeltaMinutes;
        BaseUtcOffsetDeltaMinutes = baseUtcOffsetDeltaMinutes;
        DaylightTransitionStart = daylightTransitionStart;
        DaylightTransitionEnd = daylightTransitionEnd;
    }

    public long DateStartTicks { get; }
    public long DateEndTicks { get; }
    public int DaylightDeltaMinutes { get; }
    public int BaseUtcOffsetDeltaMinutes { get; }
    public TimeZoneTransitionDefinition DaylightTransitionStart { get; }
    public TimeZoneTransitionDefinition DaylightTransitionEnd { get; }
}

public sealed class TimeZoneDefinition {
    public TimeZoneDefinition(string id, string displayName, int baseUtcOffsetMinutes, TimeZoneAdjustmentRuleDefinition[] adjustmentRules) {
        Id = id;
        DisplayName = displayName;
        BaseUtcOffsetMinutes = baseUtcOffsetMinutes;
        AdjustmentRules = adjustmentRules;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public int BaseUtcOffsetMinutes { get; }
    public TimeZoneAdjustmentRuleDefinition[] AdjustmentRules { get; }
}

public static class TimeZones {
    private const long TicksPerMinute = TimeSpan.TicksPerMinute;
    private static readonly TimeZoneDefinition[] _available = GeneratedTimeZoneCatalog.Zones;
    private static TimeZoneDefinition _current = FindDefaultZone();

    public static int Count => _available.Length;
    public static string CurrentId => _current.Id;
    public static string CurrentDisplayName => _current.DisplayName;

    public static int GetCurrentUtcOffsetMinutes(DateTime utcTime) {
        DateTime utc = DateTime.SpecifyKind(utcTime, DateTimeKind.Utc);
        return GetUtcOffsetMinutes(utc, _current);
    }

    public static TimeZoneDefinition GetAt(int index) {
        if ((uint)index >= (uint)_available.Length) {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return _available[index];
    }

    public static bool TryFind(string id, out TimeZoneDefinition? timeZone) {
        for (int index = 0; index < _available.Length; index++) {
            if (string.Equals(_available[index].Id, id, StringComparison.OrdinalIgnoreCase)) {
                timeZone = _available[index];
                return true;
            }
        }

        timeZone = null;
        return false;
    }

    public static bool TrySetCurrent(string id) {
        if (!TryFind(id, out TimeZoneDefinition? timeZone) || timeZone is null) {
            return false;
        }

        _current = timeZone;
        return true;
    }

    public static DateTime ConvertUtcToLocal(DateTime utcTime) {
        DateTime utc = DateTime.SpecifyKind(utcTime, DateTimeKind.Utc);
        int offsetMinutes = GetUtcOffsetMinutes(utc, _current);
        long localTicks = utc.Ticks + (offsetMinutes * TicksPerMinute);
        return new DateTime(localTicks, DateTimeKind.Unspecified);
    }

    private static TimeZoneDefinition FindDefaultZone() {
        for (int index = 0; index < _available.Length; index++) {
            if (string.Equals(_available[index].Id, "UTC", StringComparison.OrdinalIgnoreCase)) {
                return _available[index];
            }
        }

        throw new InvalidOperationException("The generated timezone catalog does not contain UTC.");
    }

    private static int GetUtcOffsetMinutes(DateTime utcTime, TimeZoneDefinition timeZone) {
        long utcTicks = utcTime.Ticks;
        for (int index = 0; index < timeZone.AdjustmentRules.Length; index++) {
            TimeZoneAdjustmentRuleDefinition rule = timeZone.AdjustmentRules[index];
            int standardOffsetMinutes = timeZone.BaseUtcOffsetMinutes + rule.BaseUtcOffsetDeltaMinutes;
            long standardLocalTicks = utcTicks + (standardOffsetMinutes * TicksPerMinute);
            DateTime standardLocalDate = new DateTime(standardLocalTicks, DateTimeKind.Unspecified).Date;
            if (standardLocalDate.Ticks < rule.DateStartTicks || standardLocalDate.Ticks > rule.DateEndTicks) {
                continue;
            }

            if (rule.DaylightDeltaMinutes == 0) {
                return standardOffsetMinutes;
            }

            bool isDaylight = IsInDaylightPeriod(utcTicks, standardOffsetMinutes, rule, standardLocalDate.Year);
            return standardOffsetMinutes + (isDaylight ? rule.DaylightDeltaMinutes : 0);
        }

        return timeZone.BaseUtcOffsetMinutes;
    }

    private static bool IsInDaylightPeriod(
        long utcTicks,
        int standardOffsetMinutes,
        TimeZoneAdjustmentRuleDefinition rule,
        int localYear
    ) {
        for (int startYear = localYear - 1; startYear <= localYear; startYear++) {
            if (startYear < new DateTime(rule.DateStartTicks, DateTimeKind.Unspecified).Year
                || startYear > new DateTime(rule.DateEndTicks, DateTimeKind.Unspecified).Year) {
                continue;
            }

            DateTime startLocal = GetTransitionDate(startYear, rule.DaylightTransitionStart);
            DateTime endLocal = GetTransitionDate(startYear, rule.DaylightTransitionEnd);
            if (startLocal > endLocal) {
                if (startYear == 9999) {
                    continue;
                }
                endLocal = GetTransitionDate(startYear + 1, rule.DaylightTransitionEnd);
            }

            long startUtcTicks = startLocal.Ticks - (standardOffsetMinutes * TicksPerMinute);
            long endUtcTicks = endLocal.Ticks - ((standardOffsetMinutes + rule.DaylightDeltaMinutes) * TicksPerMinute);
            if (utcTicks >= startUtcTicks && utcTicks < endUtcTicks) {
                return true;
            }
        }

        return false;
    }

    private static DateTime GetTransitionDate(int year, TimeZoneTransitionDefinition transition) {
        int day = transition.Day;
        if (!transition.IsFixedDateRule) {
            DateTime firstOfMonth = new DateTime(year, transition.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
            int firstDayOfWeek = (int)firstOfMonth.DayOfWeek;
            day = 1 + ((transition.DayOfWeek - firstDayOfWeek + 7) % 7) + ((transition.Week - 1) * 7);
            int daysInMonth = DateTime.DaysInMonth(year, transition.Month);
            if (day > daysInMonth) {
                day -= 7;
            }
        } else {
            int daysInMonth = DateTime.DaysInMonth(year, transition.Month);
            if (day > daysInMonth) {
                day = daysInMonth;
            }
        }

        return new DateTime(year, transition.Month, day, 0, 0, 0, DateTimeKind.Unspecified)
            .AddTicks(transition.TimeOfDayTicks);
    }
}