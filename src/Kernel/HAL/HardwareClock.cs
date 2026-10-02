using System;

namespace yggdrasilKernel.HAL;

public static class HardwareClock {
    public static DateTime GetCurrentTime() {
        return DateTime.UtcNow;
    }

    public static string GetFormattedTime() {
        DateTime currentTime = GetCurrentTime();
        return $"{currentTime.Hour:D2}:{currentTime.Minute:D2}:{currentTime.Second:D2}";
    }
}