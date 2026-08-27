param(
    [string]$PublishedExecutable,
    [string]$SetupExecutable
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$originalsDirectory = Join-Path $repoRoot "docs/branding/context-mole/originals"
$icoPath = Join-Path $repoRoot "src/App.UI/Assets/context-mole.ico"
$expectedOriginals = [ordered]@{
    "context-mole-01-app-icon.png" = @{ Hash = "D0E7F064F962583C4166F5EB36D7927062338D0959C188AB53815250A1AF63DB"; Transparent = $true }
    "context-mole-02-reference.png" = @{ Hash = "6B9493B4D765FEAB809EAC9BBF19D717FD7DA9B6814CB83BAD7D8D33DF572F7C"; Transparent = $true }
    "context-mole-03-reference.png" = @{ Hash = "95B401CF4739255071CE2D87BE6DBB3168F5EDF877FED356A254F054C55E30D8"; Transparent = $true }
    "context-mole-04-reference.png" = @{ Hash = "39419D7D75F5E78BF15F4C04E348919350907AD27D31677DFA4F9A603134FAAF"; Transparent = $true }
    "context-mole-05-reference.png" = @{ Hash = "A42FBB714A648F4868FE297A598344D024704C0391D714E871E74B7A0B5EC7F1"; Transparent = $true }
    "context-mole-06-reference.png" = @{ Hash = "AED61BEDA6444C5C74EBA3372DC79B79E864969AEFA43FEB273F52F5BFAEFB87"; Transparent = $false }
    "context-mole-07-reference.png" = @{ Hash = "24D2E602886767AE31FA19AC11A07D704BE214F249F03B820367EB6CB1E02E65"; Transparent = $true }
}
$expectedSizes = @(16, 24, 32, 48, 64, 128, 256)

try {
    Add-Type -AssemblyName System.Drawing.Common
}
catch {
    Add-Type -AssemblyName System.Drawing
}

function Assert-TransparentAndOpaquePixels {
    param(
        [Parameter(Mandatory)]
        [System.Drawing.Bitmap]$Bitmap,

        [Parameter(Mandatory)]
        [string]$Description
    )

    $minimumAlpha = 255
    $maximumAlpha = 0

    :alphaScan for ($y = 0; $y -lt $Bitmap.Height; $y++) {
        for ($x = 0; $x -lt $Bitmap.Width; $x++) {
            $alpha = $Bitmap.GetPixel($x, $y).A
            if ($alpha -lt $minimumAlpha) { $minimumAlpha = $alpha }
            if ($alpha -gt $maximumAlpha) { $maximumAlpha = $alpha }
            if ($minimumAlpha -eq 0 -and $maximumAlpha -eq 255) {
                break alphaScan
            }
        }
    }

    if ($minimumAlpha -ne 0 -or $maximumAlpha -ne 255) {
        throw "$Description must contain both fully transparent and fully opaque pixels."
    }
}

foreach ($entry in $expectedOriginals.GetEnumerator()) {
    $path = Join-Path $originalsDirectory $entry.Key
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing approved Context Mole original: $($entry.Key)"
    }

    $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash
    if ($actualHash -cne $entry.Value.Hash) {
        throw "The approved Context Mole original $($entry.Key) does not match its SHA-256."
    }

    $bitmap = [System.Drawing.Bitmap]::FromFile($path)
    try {
        if ($bitmap.RawFormat.Guid -ne [System.Drawing.Imaging.ImageFormat]::Png.Guid) {
            throw "The approved Context Mole original $($entry.Key) is not a PNG."
        }
        if ($bitmap.Width -ne 1254 -or $bitmap.Height -ne 1254) {
            throw "The approved Context Mole original $($entry.Key) is $($bitmap.Width)x$($bitmap.Height); expected 1254x1254."
        }

        if ($entry.Value.Transparent) {
            Assert-TransparentAndOpaquePixels -Bitmap $bitmap -Description "The approved Context Mole original $($entry.Key)"
        }
    }
    finally {
        $bitmap.Dispose()
    }
}

if (-not (Test-Path -LiteralPath $icoPath -PathType Leaf)) {
    throw "Missing generated Context Mole Windows icon: $icoPath"
}

$bytes = [System.IO.File]::ReadAllBytes($icoPath)
if ($bytes.Length -lt 6 -or [BitConverter]::ToUInt16($bytes, 0) -ne 0 -or [BitConverter]::ToUInt16($bytes, 2) -ne 1) {
    throw "The generated Context Mole asset is not a valid Windows icon."
}

$count = [BitConverter]::ToUInt16($bytes, 4)
if ($count -ne $expectedSizes.Count -or $bytes.Length -lt 6 + (16 * $count)) {
    throw "The Context Mole Windows icon contains an invalid frame directory."
}

$actualSizes = [System.Collections.Generic.List[int]]::new()
for ($index = 0; $index -lt $count; $index++) {
    $directoryEntry = 6 + (16 * $index)
    $width = if ($bytes[$directoryEntry] -eq 0) { 256 } else { [int]$bytes[$directoryEntry] }
    $height = if ($bytes[$directoryEntry + 1] -eq 0) { 256 } else { [int]$bytes[$directoryEntry + 1] }
    if ($width -ne $height) {
        throw "Context Mole icon frame $index is not square."
    }
    $actualSizes.Add($width)

    $length = [BitConverter]::ToUInt32($bytes, $directoryEntry + 8)
    $offset = [BitConverter]::ToUInt32($bytes, $directoryEntry + 12)
    if ($length -eq 0 -or $offset -gt $bytes.Length -or $length -gt $bytes.Length - $offset) {
        throw "Context Mole icon frame ${width}px points outside the icon file."
    }

    $stream = [System.IO.MemoryStream]::new($bytes, [int]$offset, [int]$length, $false, $true)
    try {
        $bitmap = [System.Drawing.Bitmap]::FromStream($stream)
        try {
            if ($bitmap.Width -ne $width -or $bitmap.Height -ne $height) {
                throw "Context Mole icon frame ${width}px has inconsistent encoded dimensions."
            }

            Assert-TransparentAndOpaquePixels -Bitmap $bitmap -Description "Context Mole icon frame ${width}px"
        }
        finally {
            $bitmap.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

if (($actualSizes -join ",") -cne ($expectedSizes -join ",")) {
    throw "Unexpected Context Mole Windows icon frame sizes: $($actualSizes -join ', ')."
}

$iconConsumers = @(
    @{
        Path = "src/App.UI/ContextMole.App.UI.csproj"
        Reference = "<ApplicationIcon>Assets\context-mole.ico</ApplicationIcon>"
        Description = "executable project"
    },
    @{
        Path = "src/App.UI/ContextMole.App.UI.csproj"
        Reference = 'Link="Assets\context-mole-app-icon.png"'
        Description = "sidebar image resource"
    },
    @{
        Path = "src/App.UI/App.axaml.cs"
        Reference = "avares://ContextMole.App.UI/Assets/context-mole.ico"
        Description = "tray icon"
    },
    @{
        Path = "src/App.UI/Views/ConfirmWindow.cs"
        Reference = "avares://ContextMole.App.UI/Assets/context-mole.ico"
        Description = "confirmation window"
    },
    @{
        Path = "src/App.UI/Views/MainWindow.axaml"
        Reference = 'Icon="/Assets/context-mole.ico"'
        Description = "main window"
    },
    @{
        Path = "src/App.UI/Views/MainWindow.axaml"
        Reference = 'Source="/Assets/context-mole-app-icon.png"'
        Description = "sidebar brand image"
    },
    @{
        Path = "src/App.UI/Views/ModelSetupWindow.axaml"
        Reference = 'Icon="/Assets/context-mole.ico"'
        Description = "model setup window"
    },
    @{
        Path = "src/App.UI/Views/ProjectEditorWindow.axaml"
        Reference = 'Icon="/Assets/context-mole.ico"'
        Description = "project editor window"
    }
)

foreach ($consumer in $iconConsumers) {
    $path = Join-Path $repoRoot $consumer.Path
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing Context Mole icon consumer: $($consumer.Path)"
    }

    $contents = Get-Content -Raw -LiteralPath $path
    if ($contents.IndexOf($consumer.Reference, [StringComparison]::Ordinal) -lt 0) {
        throw "The $($consumer.Description) does not reference the Context Mole icon correctly."
    }
}

foreach ($executable in @($PublishedExecutable, $SetupExecutable)) {
    if ([string]::IsNullOrWhiteSpace($executable)) { continue }
    $resolved = (Resolve-Path -LiteralPath $executable).Path
    $associatedIcon = [System.Drawing.Icon]::ExtractAssociatedIcon($resolved)
    if ($null -eq $associatedIcon) {
        throw "No Windows icon is embedded in $resolved."
    }
    $associatedIcon.Dispose()
}

Write-Host "Context Mole branding verified: seven approved PNG originals, icon frames, and all eight application references are valid."
