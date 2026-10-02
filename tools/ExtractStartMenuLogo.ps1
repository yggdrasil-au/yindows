param(
    [Parameter(Mandatory = $true)]
    [string]$InputPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

Add-Type -AssemblyName System.Drawing

$size = 32
$sourceBitmap = [System.Drawing.Bitmap]::new($InputPath)

try {
    $scaledBitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)

    try {
        $graphics = [System.Drawing.Graphics]::FromImage($scaledBitmap)

        try {
            $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.DrawImage($sourceBitmap, 0, 0, $size, $size)

            [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($OutputPath)) | Out-Null
            $builder = [System.Text.StringBuilder]::new('namespace yggdrasilKernel.PreOs.Desktop; public static class StartMenuLogoAsset { public const int Width = ' + $size + '; public const int Height = ' + $size + '; public static readonly int[] Pixels = [')

            for ($row = 0; $row -lt $size; $row++) {
                for ($column = 0; $column -lt $size; $column++) {
                    [void]$builder.Append($scaledBitmap.GetPixel($column, $row).ToArgb()).Append(',')
                }
            }

            [void]$builder.Append(']; }')
            [System.IO.File]::WriteAllText($OutputPath, $builder.ToString())
        } finally {
            $graphics.Dispose()
        }
    } finally {
        $scaledBitmap.Dispose()
    }
} finally {
    $sourceBitmap.Dispose()
}