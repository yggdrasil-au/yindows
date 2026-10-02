using System;
using Cosmos.HAL;

public static class HardwareClock {
    public static DateTime GetCurrentTime() {
        // RTC.Year only returns the 2-digit year (e.g., 26), 
        // so combine it with Century (e.g., 20) or add 2000.
        int year = (RTC.Century * 100) + RTC.Year;
        int month = RTC.Month;
        int day = RTC.DayOfTheMonth;
        int hour = RTC.Hour;
        int minute = RTC.Minute;
        int second = RTC.Second;

        return new DateTime(year, month, day, hour, minute, second);
    }

    public static string GetFormattedTime() {
        return $"{RTC.Hour:D2}:{RTC.Minute:D2}:{RTC.Second:D2}";
    }
}