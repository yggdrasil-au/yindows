param(
    [Parameter(Mandatory = $true)]
    [string]$InputPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

Add-Type -AssemblyName System.Drawing

Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class CursorInterop
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint loadFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr icon, int width, int height, uint stepIfAniCur, IntPtr hbrFlickerFreeDraw, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyCursor(IntPtr cursor);
}
'@

$imageCursor = 2
$loadFromFile = 0x10
$drawNormal = 0x0003
$size = 32
$cursor = [CursorInterop]::LoadImage([IntPtr]::Zero, $InputPath, $imageCursor, $size, $size, $loadFromFile)

if ($cursor -eq [IntPtr]::Zero) {
    throw "Could not load cursor '$InputPath'."
}

try {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)

    try {
        $hdc = $graphics.GetHdc()
        try {
            if (-not [CursorInterop]::DrawIconEx($hdc, 0, 0, $cursor, $size, $size, 0, [IntPtr]::Zero, $drawNormal)) {
                throw "Could not render cursor '$InputPath'."
            }
        } finally {
            $graphics.ReleaseHdc($hdc)
        }

        $builder = [System.Text.StringBuilder]::new('namespace yggdrasilKernel; public static class CursorAsset { public const int Width = ' + $size + '; public const int Height = ' + $size + '; public static readonly int[] Pixels = [')
        for ($y = 0; $y -lt $size; $y++) {
            for ($x = 0; $x -lt $size; $x++) {
                [void]$builder.Append($bitmap.GetPixel($x, $y).ToArgb()).Append(',')
            }
        }
        [void]$builder.Append(']; }')
        [System.IO.File]::WriteAllText($OutputPath, $builder.ToString())
    } finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
} finally {
    [void][CursorInterop]::DestroyCursor($cursor)
}