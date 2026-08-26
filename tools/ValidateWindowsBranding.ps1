param(
    [string]$PublishedExecutable,
    [string]$SetupExecutable
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$pngPath = Join-Path $repoRoot "docs/branding/mcp-index-search-icon-ai.png"
$icoPath = Join-Path $repoRoot "src/App.UI/Assets/mcp-index-search.ico"
$expectedHash = "CA7E1B09F1551F4D41951D54DAD3D03DF57DDDFAFB44A1FAF01F9F822A2AADC5"
$expectedSizes = @(16, 24, 32, 48, 64, 128, 256)

if ((Get-FileHash -Algorithm SHA256 -LiteralPath $pngPath).Hash -cne $expectedHash) {
    throw "The preserved RGBA source PNG does not match the approved SHA-256."
}

$bytes = [System.IO.File]::ReadAllBytes($icoPath)
if ([BitConverter]::ToUInt16($bytes, 0) -ne 0 -or [BitConverter]::ToUInt16($bytes, 2) -ne 1) {
    throw "The generated asset is not a valid Windows icon."
}

$count = [BitConverter]::ToUInt16($bytes, 4)
if ($count -ne $expectedSizes.Count) {
    throw "The Windows icon contains $count frames; expected $($expectedSizes.Count)."
}

Add-Type -AssemblyName System.Drawing.Common
$actualSizes = [System.Collections.Generic.List[int]]::new()
for ($index = 0; $index -lt $count; $index++) {
    $entry = 6 + (16 * $index)
    $width = if ($bytes[$entry] -eq 0) { 256 } else { [int]$bytes[$entry] }
    $height = if ($bytes[$entry + 1] -eq 0) { 256 } else { [int]$bytes[$entry + 1] }
    if ($width -ne $height) { throw "Icon frame $index is not square." }
    $actualSizes.Add($width)

    $length = [BitConverter]::ToUInt32($bytes, $entry + 8)
    $offset = [BitConverter]::ToUInt32($bytes, $entry + 12)
    $stream = [System.IO.MemoryStream]::new($bytes, [int]$offset, [int]$length, $false, $true)
    try {
        $bitmap = [System.Drawing.Bitmap]::FromStream($stream)
        try {
            $minimumAlpha = 255
            $maximumAlpha = 0
            for ($y = 0; $y -lt $bitmap.Height; $y++) {
                for ($x = 0; $x -lt $bitmap.Width; $x++) {
                    $alpha = $bitmap.GetPixel($x, $y).A
                    if ($alpha -lt $minimumAlpha) { $minimumAlpha = $alpha }
                    if ($alpha -gt $maximumAlpha) { $maximumAlpha = $alpha }
                }
            }
            if ($minimumAlpha -ne 0 -or $maximumAlpha -ne 255) {
                throw "Icon frame ${width}px does not preserve transparent and opaque pixels."
            }
        }
        finally {
            $bitmap.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

if ((Compare-Object $expectedSizes $actualSizes).Count -ne 0) {
    throw "Unexpected Windows icon frame sizes: $($actualSizes -join ', ')."
}

$projectFile = Get-Content -Raw -LiteralPath (Join-Path $repoRoot "src/App.UI/MCPIndexSearch.App.UI.csproj")
$appFile = Get-Content -Raw -LiteralPath (Join-Path $repoRoot "src/App.UI/App.axaml.cs")
$windowFile = Get-Content -Raw -LiteralPath (Join-Path $repoRoot "src/App.UI/Views/MainWindow.axaml")
if ($projectFile -notmatch [regex]::Escape("<ApplicationIcon>Assets\mcp-index-search.ico</ApplicationIcon>")) {
    throw "The executable project does not reference the MCPIndexSearch icon."
}
if ($appFile -notmatch [regex]::Escape("Assets/mcp-index-search.ico")) {
    throw "The tray icon does not reference the MCPIndexSearch icon."
}
if ($windowFile -notmatch [regex]::Escape('Icon="/Assets/mcp-index-search.ico"')) {
    throw "The main window does not reference the MCPIndexSearch icon."
}

foreach ($executable in @($PublishedExecutable, $SetupExecutable)) {
    if ([string]::IsNullOrWhiteSpace($executable)) { continue }
    $resolved = (Resolve-Path -LiteralPath $executable).Path
    $associatedIcon = [System.Drawing.Icon]::ExtractAssociatedIcon($resolved)
    if ($null -eq $associatedIcon) { throw "No Windows icon is embedded in $resolved." }
    $associatedIcon.Dispose()
}

Write-Host "Branding verified: source hash, RGBA transparency, icon frames, and application references are valid."
