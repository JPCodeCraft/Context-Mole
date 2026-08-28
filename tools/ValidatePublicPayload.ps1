[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PublishedDirectory
)

$ErrorActionPreference = "Stop"

$resolvedDirectory = Resolve-Path -LiteralPath $PublishedDirectory -ErrorAction Stop
$payloadRoot = $resolvedDirectory.Path

$applicationExecutables = @(
    Get-ChildItem -LiteralPath $payloadRoot -Recurse -File -Filter "ContextMole.App.UI.exe"
)

if ($applicationExecutables.Count -ne 1) {
    throw "Expected exactly one ContextMole.App.UI.exe in '$payloadRoot', but found $($applicationExecutables.Count)."
}

$applicationRoot = $applicationExecutables[0].DirectoryName
$mcpRoot = Join-Path $applicationRoot "mcp-server"
$brokerRoot = Join-Path $mcpRoot "broker"
$uninstallHelperRoot = Join-Path $applicationRoot "uninstall-helper"
$requiredFiles = @(
    (Join-Path $applicationRoot "LICENSE"),
    (Join-Path $applicationRoot "THIRD-PARTY-NOTICES.md"),
    (Join-Path $applicationRoot "THIRD-PARTY-LICENSES\SharpCompress.txt"),
    (Join-Path $mcpRoot "ContextMole.Mcp.exe"),
    (Join-Path $brokerRoot "ContextMole.Broker.exe"),
    (Join-Path $mcpRoot "LICENSE"),
    (Join-Path $mcpRoot "THIRD-PARTY-NOTICES.md"),
    (Join-Path $mcpRoot "THIRD-PARTY-LICENSES\SharpCompress.txt"),
    (Join-Path $uninstallHelperRoot "ContextMole.UninstallHelper.exe")
)

foreach ($requiredFile in $requiredFiles) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Published payload is missing required file: $requiredFile"
    }
}

$uninstallHelperFiles = @(Get-ChildItem -LiteralPath $uninstallHelperRoot -File)
if ($uninstallHelperFiles.Count -ne 1 -or $uninstallHelperFiles[0].Name -cne "ContextMole.UninstallHelper.exe") {
    throw "The Windows uninstall helper must be packaged as exactly one self-contained executable."
}

$allUninstallHelpers = @(Get-ChildItem -LiteralPath $payloadRoot -Recurse -File -Filter "ContextMole.UninstallHelper.exe")
$expectedUninstallHelper = [System.IO.Path]::GetFullPath(
    (Join-Path $uninstallHelperRoot "ContextMole.UninstallHelper.exe"))
if ($allUninstallHelpers.Count -ne 1 -or
    [System.IO.Path]::GetFullPath($allUninstallHelpers[0].FullName) -cne $expectedUninstallHelper) {
    throw "Expected exactly one Windows uninstall helper at uninstall-helper\ContextMole.UninstallHelper.exe."
}

$allBrokers = @(Get-ChildItem -LiteralPath $payloadRoot -Recurse -File -Filter "ContextMole.Broker.exe")
$expectedBroker = [System.IO.Path]::GetFullPath((Join-Path $brokerRoot "ContextMole.Broker.exe"))
if ($allBrokers.Count -ne 1 -or
    [System.IO.Path]::GetFullPath($allBrokers[0].FullName) -cne $expectedBroker) {
    throw "Expected exactly one shared broker at mcp-server\broker\ContextMole.Broker.exe."
}

$issues = [System.Collections.Generic.List[string]]::new()
$payloadFiles = @(Get-ChildItem -LiteralPath $payloadRoot -Recurse -File)

foreach ($file in $payloadFiles) {
    $name = $file.Name.ToLowerInvariant()
    $isSensitive =
        $name -match '^\.env(?:\.|$)' -or
        $name -eq 'secrets.json' -or
        $name -match '\.(?:pem|key|p12|pfx|dmp|dump)$' -or
        $name -match '\.(?:db|sqlite|sqlite3)(?:-.+)?$'

    if ($isSensitive) {
        $relativePath = [System.IO.Path]::GetRelativePath($payloadRoot, $file.FullName)
        $issues.Add("Sensitive file included in the payload: $relativePath")
    }

    if ($file.Extension -ieq '.pdb') {
        $relativePath = [System.IO.Path]::GetRelativePath($payloadRoot, $file.FullName)
        $issues.Add("External symbol file included in the payload: $relativePath")
    }
}

$documentCount = 0
$managedAssemblies = @(
    Get-ChildItem -LiteralPath $applicationRoot -Recurse -File -Filter "ContextMole*.dll"
)

foreach ($assembly in $managedAssemblies) {
    $stream = [System.IO.File]::OpenRead($assembly.FullName)
    $peReader = $null
    try {
        $peReader = [System.Reflection.PortableExecutable.PEReader]::new($stream)
        foreach ($entry in $peReader.ReadDebugDirectory()) {
            if ($entry.Type -ne [System.Reflection.PortableExecutable.DebugDirectoryEntryType]::EmbeddedPortablePdb) {
                continue
            }

            $provider = $peReader.ReadEmbeddedPortablePdbDebugDirectoryData($entry)
            try {
                $reader = $provider.GetMetadataReader()
                foreach ($handle in $reader.Documents) {
                    $documentName = $reader.GetString($reader.GetDocument($handle).Name)
                    $normalizedName = $documentName.Replace('\', '/')
                    $documentCount++

                    if ($normalizedName.StartsWith('/_/', [System.StringComparison]::Ordinal)) {
                        continue
                    }

                    if ([System.IO.Path]::IsPathRooted($documentName) -or $normalizedName -match '^[A-Za-z]:/') {
                        $relativeAssembly = [System.IO.Path]::GetRelativePath($payloadRoot, $assembly.FullName)
                        $issues.Add("Absolute source path embedded in: $relativeAssembly")
                        break
                    }
                }
            }
            finally {
                $provider.Dispose()
            }
        }
    }
    finally {
        if ($null -ne $peReader) {
            $peReader.Dispose()
        }
        $stream.Dispose()
    }
}

if ($documentCount -eq 0) {
    $issues.Add("No embedded source documents were available for the privacy check.")
}

$textExtensions = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($extension in @('.config', '.json', '.md', '.nuspec', '.toml', '.txt', '.xml', '.yaml', '.yml')) {
    $null = $textExtensions.Add($extension)
}

foreach ($file in $payloadFiles) {
    if (-not $textExtensions.Contains($file.Extension)) {
        continue
    }

    $contents = [System.IO.File]::ReadAllText($file.FullName)
    if ($contents -match '(?i)(?:[A-Z]:[\\/]+Users[\\/]+|/Users/[^/]+/|/home/[^/]+/)') {
        $relativePath = [System.IO.Path]::GetRelativePath($payloadRoot, $file.FullName)
        $issues.Add("Local user path included in text file: $relativePath")
    }
}

if ($issues.Count -gt 0) {
    throw ($issues -join [Environment]::NewLine)
}

Write-Host "Public payload verified: required notices are present and no sensitive files or local user paths were found."
