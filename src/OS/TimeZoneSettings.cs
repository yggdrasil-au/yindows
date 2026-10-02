using System;
using yggdrasilKernel.Vfs;

namespace yggdrasilKernel.OS;

public static class TimeZoneSettings {
    private const string SettingsDirectory = @"C:\System\Settings";
    private const string SettingsPath = SettingsDirectory + @"\timezone.txt";

    public static bool TryLoad(out string error) {
        error = string.Empty;
        try {
            if (!YFile.Exists(SettingsPath)) {
                return true;
            }

            string savedId = YFile.ReadAllText(SettingsPath).Trim();
            if (savedId.Length == 0 || TimeZones.TrySetCurrent(savedId)) {
                return true;
            }

            error = $"Saved timezone '{savedId}' is not in the generated catalog.";
            return false;
        } catch (Exception exception) {
            error = exception.Message;
            return false;
        }
    }

    public static bool TrySaveCurrent(out string error) {
        try {
            YDirectory.CreateDirectory(SettingsDirectory);
            YFile.WriteAllText(SettingsPath, TimeZones.CurrentId);
            error = string.Empty;
            return true;
        } catch (Exception exception) {
            error = exception.Message;
            return false;
        }
    }
}