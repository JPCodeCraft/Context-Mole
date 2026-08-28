[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $SetupExecutable,

    [Parameter(Mandatory = $true)]
    [string] $InstallDirectory,

    [Parameter(Mandatory = $true)]
    [switch] $DisposableProfileConfirmed
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if (-not $IsWindows) {
    throw "The packaged uninstall smoke test is Windows-only."
}
if (-not $DisposableProfileConfirmed -or $env:GITHUB_ACTIONS -cne "true") {
    throw "This destructive smoke test may run only on a confirmed disposable GitHub Actions profile."
}

$setupPath = (Resolve-Path -LiteralPath $SetupExecutable -ErrorAction Stop).Path
$runnerTemp = [System.IO.Path]::TrimEndingDirectorySeparator(
    [System.IO.Path]::GetFullPath($env:RUNNER_TEMP))
$installPath = [System.IO.Path]::TrimEndingDirectorySeparator(
    [System.IO.Path]::GetFullPath($InstallDirectory))
$runnerPrefix = $runnerTemp + [System.IO.Path]::DirectorySeparatorChar
if (-not $installPath.StartsWith($runnerPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
    -not [System.IO.Path]::GetFileName($installPath).StartsWith(
        "ContextMole-package-smoke-", [System.StringComparison]::Ordinal)) {
    throw "The disposable install directory must be a ContextMole-package-smoke-* child of RUNNER_TEMP."
}
if (Test-Path -LiteralPath $installPath) {
    throw "The disposable install directory already exists: $installPath"
}

$localAppData = [System.IO.Path]::TrimEndingDirectorySeparator(
    [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData))
$dataDirectory = [System.IO.Path]::GetFullPath(
    [System.IO.Path]::Combine($localAppData, "ContextMole"))
if ([System.IO.Path]::GetDirectoryName($dataDirectory) -cne $localAppData) {
    throw "The canonical Context Mole data-directory calculation escaped LocalApplicationData."
}
if (Test-Path -LiteralPath $dataDirectory) {
    throw "The disposable runner already has Context Mole data; refusing to touch it: $dataDirectory"
}

$fixtureDirectory = [System.IO.Path]::Combine(
    $runnerTemp,
    "ContextMole-uninstall-source-fixture-$($env:GITHUB_RUN_ID)-$([Guid]::NewGuid().ToString('N'))")
[System.IO.Directory]::CreateDirectory($fixtureDirectory) | Out-Null
$sourceFixture = [System.IO.Path]::Combine($fixtureDirectory, "indexed-source.bin")
$sourceBytes = [byte[]] (0..255)
[System.IO.File]::WriteAllBytes($sourceFixture, $sourceBytes)
$sourceHash = [System.Convert]::ToHexString(
    [System.Security.Cryptography.SHA256]::HashData($sourceBytes))

function Invoke-SmokeProcess {
    param(
        [Parameter(Mandatory = $true)] [string] $FileName,
        [Parameter(Mandatory = $true)] [string[]] $Arguments,
        [Parameter(Mandatory = $true)] [int] $TimeoutSeconds,
        [string] $WorkingDirectory = $runnerTemp
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "Could not start smoke-test process: $FileName"
        }
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            try { $process.Kill($true) } catch { }
            throw "Smoke-test process timed out after $TimeoutSeconds seconds: $FileName"
        }
        if ($process.ExitCode -ne 0) {
            throw "Smoke-test process exited with code $($process.ExitCode): $FileName"
        }
    }
    finally {
        $process.Dispose()
    }
}

function Wait-ForMissingPath {
    param(
        [Parameter(Mandatory = $true)] [string] $Path,
        [Parameter(Mandatory = $true)] [int] $TimeoutSeconds
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if (-not (Test-Path -LiteralPath $Path)) { return }
        Start-Sleep -Milliseconds 250
    }
    throw "Timed out waiting for packaged cleanup of: $Path"
}

function Assert-SourceFixtureUntouched {
    if (-not (Test-Path -LiteralPath $sourceFixture -PathType Leaf)) {
        throw "The source fixture outside Context Mole data was removed."
    }
    $actualHash = [System.Convert]::ToHexString(
        [System.Security.Cryptography.SHA256]::HashData(
            [System.IO.File]::ReadAllBytes($sourceFixture)))
    if ($actualHash -cne $sourceHash) {
        throw "The source fixture outside Context Mole data was modified."
    }
}

# Install without launching the UI, then prove an ordinary Velopack uninstall keeps app data.
Invoke-SmokeProcess -FileName $setupPath -Arguments @(
    "--silent", "--installto", $installPath
) -TimeoutSeconds 180

$updateExecutable = [System.IO.Path]::Combine($installPath, "Update.exe")
$installedHelper = [System.IO.Path]::Combine(
    $installPath, "current", "uninstall-helper", "ContextMole.UninstallHelper.exe")
foreach ($required in @($updateExecutable, $installedHelper)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "The installed package is missing: $required"
    }
}

[System.IO.Directory]::CreateDirectory($dataDirectory) | Out-Null
$retainedData = [System.IO.Path]::Combine($dataDirectory, "ordinary-uninstall-retained.bin")
$retainedBytes = [byte[]] (255..0)
[System.IO.File]::WriteAllBytes($retainedData, $retainedBytes)
$retainedHash = [System.Convert]::ToHexString(
    [System.Security.Cryptography.SHA256]::HashData($retainedBytes))

Invoke-SmokeProcess -FileName $updateExecutable -Arguments @(
    "uninstall", "--silent"
) -TimeoutSeconds 180 -WorkingDirectory $installPath
Wait-ForMissingPath -Path $installPath -TimeoutSeconds 30

if (-not (Test-Path -LiteralPath $retainedData -PathType Leaf) -or
    [System.Convert]::ToHexString(
        [System.Security.Cryptography.SHA256]::HashData(
            [System.IO.File]::ReadAllBytes($retainedData))) -cne $retainedHash) {
    throw "Ordinary Velopack uninstall did not retain Context Mole application data byte-for-byte."
}
Assert-SourceFixtureUntouched

# Reinstall and invoke the packaged in-app helper contract. It deliberately launches the official
# uninstaller without silent mode, then removes only the canonical Context Mole data directory.
Invoke-SmokeProcess -FileName $setupPath -Arguments @(
    "--silent", "--installto", $installPath
) -TimeoutSeconds 180

$updateExecutable = [System.IO.Path]::Combine($installPath, "Update.exe")
$installedHelper = [System.IO.Path]::Combine(
    $installPath, "current", "uninstall-helper", "ContextMole.UninstallHelper.exe")
foreach ($required in @($updateExecutable, $installedHelper)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "The reinstalled package is missing: $required"
    }
}

$requestId = [Guid]::NewGuid()
$createdUtc = [DateTimeOffset]::UtcNow
$lifecycleDirectory = [System.IO.Path]::Combine($dataDirectory, ".lifecycle")
[System.IO.Directory]::CreateDirectory($lifecycleDirectory) | Out-Null
$marker = [ordered] @{
    requestId = $requestId.ToString("D")
    createdUtc = $createdUtc.ToString("O", [Globalization.CultureInfo]::InvariantCulture)
    expiresUtc = $createdUtc.AddMinutes(15).ToString("O", [Globalization.CultureInfo]::InvariantCulture)
}
$markerPath = [System.IO.Path]::Combine($lifecycleDirectory, "shutdown-request.json")
[System.IO.File]::WriteAllText(
    $markerPath,
    ($marker | ConvertTo-Json -Compress),
    [System.Text.UTF8Encoding]::new($false))

$helperDirectory = [System.IO.Path]::Combine(
    $runnerTemp,
    "ContextMole-uninstall-$([Guid]::NewGuid().ToString('N'))")
[System.IO.Directory]::CreateDirectory($helperDirectory) | Out-Null
$helperExecutable = [System.IO.Path]::Combine($helperDirectory, "ContextMole.UninstallHelper.exe")
[System.IO.File]::Copy($installedHelper, $helperExecutable, $false)

$parentProbe = Start-Process -FilePath $env:ComSpec -ArgumentList @(
    "/D", "/C", "ping -n 2 127.0.0.1 >nul"
) -PassThru -WindowStyle Hidden
try {
    $parentProcessId = $parentProbe.Id
    $parentStartTicks = $parentProbe.StartTime.ToUniversalTime().Ticks
    $parentProbe.WaitForExit()
}
finally {
    $parentProbe.Dispose()
}

Invoke-SmokeProcess -FileName $helperExecutable -Arguments @(
    "--parent-pid", $parentProcessId.ToString([Globalization.CultureInfo]::InvariantCulture),
    "--parent-start-ticks", $parentStartTicks.ToString([Globalization.CultureInfo]::InvariantCulture),
    "--update-exe", $updateExecutable,
    "--data-dir", $dataDirectory,
    "--request-id", $requestId.ToString("D"),
    "--delete-data", "true",
    "--timeout-seconds", "120",
    "--temporary-dir", $helperDirectory
) -TimeoutSeconds 240 -WorkingDirectory $helperDirectory

Wait-ForMissingPath -Path $installPath -TimeoutSeconds 30
Wait-ForMissingPath -Path $dataDirectory -TimeoutSeconds 30
Wait-ForMissingPath -Path $helperDirectory -TimeoutSeconds 30
Assert-SourceFixtureUntouched

Write-Host "Packaged Windows uninstall smoke passed: ordinary uninstall kept data; in-app Delete removed only canonical data."
