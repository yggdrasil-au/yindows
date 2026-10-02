using yggdrasilKernel.OS;

internal static class Program {
    private static int Main() {
        if (!OperatingSystem.IsWindows()) {
            Console.Error.WriteLine("Run the timezone catalog tests on Windows.");
            return 1;
        }

        if (TimeZones.Count < 100) {
            throw new InvalidOperationException($"Expected the full Windows catalog, found {TimeZones.Count} zones.");
        }

        string initialTimeZone = TimeZones.CurrentId;
        if (TimeZones.TrySetCurrent("Not a Windows Timezone")) {
            throw new InvalidOperationException("An unknown timezone ID was accepted.");
        }
        if (!string.Equals(TimeZones.CurrentId, initialTimeZone, StringComparison.Ordinal)) {
            throw new InvalidOperationException("An unknown timezone changed the active selection.");
        }

        DateTime start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        const int hoursInYear = 365 * 24;
        int comparisons = 0;

        for (int zoneIndex = 0; zoneIndex < TimeZones.Count; zoneIndex++) {
            TimeZoneDefinition generatedZone = TimeZones.GetAt(zoneIndex);
            if (!TimeZones.TrySetCurrent(generatedZone.Id)) {
                throw new InvalidOperationException($"Could not select generated timezone {generatedZone.Id}.");
            }

            TimeZoneInfo hostZone = TimeZoneInfo.FindSystemTimeZoneById(generatedZone.Id);
            for (int hour = 0; hour < hoursInYear; hour++) {
                DateTime utc = start.AddHours(hour);
                DateTime expected = TimeZoneInfo.ConvertTimeFromUtc(utc, hostZone);
                DateTime actual = TimeZones.ConvertUtcToLocal(utc);
                if (actual.Ticks != expected.Ticks) {
                    throw new InvalidOperationException(
                        $"{generatedZone.Id} mismatch at {utc:O}: expected {expected:O}, got {actual:O}.");
                }
                comparisons++;
            }
        }

        if (!TimeZones.TrySetCurrent("uTc")) {
            throw new InvalidOperationException("Timezone lookup did not match IDs case-insensitively.");
        }

        Console.WriteLine($"Passed {comparisons:N0} UTC-to-local comparisons across {TimeZones.Count} Windows timezones.");
        return 0;
    }
}