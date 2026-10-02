param(
	[string]$OutputPath = (Join-Path $PSScriptRoot "..\src\OS\TimeZoneCatalog.Generated.cs")
)

$ErrorActionPreference = "Stop"

if ($env:OS -ne "Windows_NT") {
	throw "Generate the Windows timezone catalog on Windows so the output uses Windows timezone IDs."
}

function ConvertTo-CSharpString {
	param([string]$Value)

	return ConvertTo-Json -InputObject $Value -Compress
}

function Format-Transition {
	param([TimeZoneInfo+TransitionTime]$Transition)

	$dayOfWeek = [int]$Transition.DayOfWeek
	$isFixedDateRule = if ($Transition.IsFixedDateRule) { "true" } else { "false" }
	return "new TimeZoneTransitionDefinition($($Transition.Month), $($Transition.Week), $($Transition.Day), $dayOfWeek, $isFixedDateRule, $($Transition.TimeOfDay.Ticks)L)"
}

$timeZones = [TimeZoneInfo]::GetSystemTimeZones() | Sort-Object Id
if ($timeZones.Count -eq 0) {
	throw "The host returned no installed Windows timezones."
}

$source = [System.Text.StringBuilder]::new()
[void]$source.AppendLine("#nullable enable")
[void]$source.AppendLine("namespace yggdrasilKernel.OS;")
[void]$source.AppendLine()
[void]$source.AppendLine("internal static class GeneratedTimeZoneCatalog {")
[void]$source.AppendLine("    internal static readonly TimeZoneDefinition[] Zones = new TimeZoneDefinition[] {")

foreach ($timeZone in $timeZones) {
	$id = ConvertTo-CSharpString $timeZone.Id
	$displayName = ConvertTo-CSharpString $timeZone.DisplayName
	$baseOffsetMinutes = [int]$timeZone.BaseUtcOffset.TotalMinutes

	[void]$source.AppendLine("        new TimeZoneDefinition($id, $displayName, $baseOffsetMinutes, new TimeZoneAdjustmentRuleDefinition[] {")
	foreach ($rule in $timeZone.GetAdjustmentRules()) {
		$daylightDeltaMinutes = [int]$rule.DaylightDelta.TotalMinutes
		$baseOffsetDeltaMinutes = [int]$rule.BaseUtcOffsetDelta.TotalMinutes
		$startTransition = Format-Transition $rule.DaylightTransitionStart
		$endTransition = Format-Transition $rule.DaylightTransitionEnd
		[void]$source.AppendLine("            new TimeZoneAdjustmentRuleDefinition($($rule.DateStart.Ticks)L, $($rule.DateEnd.Ticks)L, $daylightDeltaMinutes, $baseOffsetDeltaMinutes, $startTransition, $endTransition),")
	}
	[void]$source.AppendLine("        }),")
}

[void]$source.AppendLine("    }; ")
[void]$source.AppendLine("}")

$fullOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($fullOutputPath)) | Out-Null
[System.IO.File]::WriteAllText($fullOutputPath, $source.ToString(), [System.Text.UTF8Encoding]::new($false))
Write-Host "Generated $($timeZones.Count) Windows timezones at $fullOutputPath"
