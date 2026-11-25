[CmdletBinding()]
param(
    [ValidateSet('Build', 'Test', 'Run')]
    [string]$Action = 'Build',
    [string]$UnityPath = $env:REALLIFE_UNITY_EDITOR,
    [switch]$KeepDiagnostics
)

$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$release = Join-Path $project 'Builds\Windows'
$releasePlayer = Join-Path $release 'RealLife.exe'

if ($Action -eq 'Run') {
    if (-not (Test-Path -LiteralPath $releasePlayer -PathType Leaf)) {
        throw 'Build the Windows player first with -Action Build.'
    }
    Start-Process -FilePath $releasePlayer -WorkingDirectory $release -WindowStyle Normal
    return
}

function Assert-NoLinks([string]$Path) {
    $entry = Get-Item -LiteralPath $Path -Force
    for ($ancestor = $entry; $null -ne $ancestor; $ancestor = $ancestor.Parent) {
        if ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Reparse point in target path: $($ancestor.FullName)"
        }
    }
    $link = Get-ChildItem -LiteralPath $Path -Recurse -Force |
        Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } | Select-Object -First 1
    if ($link) { throw "Reparse point in target directory: $($link.FullName)" }
}

function Invoke-CheckedProcess([string]$Exe, [string[]]$Arguments, [string]$Log, [int]$TimeoutSeconds) {
    $process = Start-Process -FilePath $Exe -ArgumentList $Arguments -PassThru -WindowStyle Hidden
    try {
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill()
            $process.WaitForExit()
            throw "Timed out after $TimeoutSeconds seconds. Log: $Log"
        }
        if ($process.ExitCode -ne 0) {
            if (Test-Path -LiteralPath $Log) { Get-Content -LiteralPath $Log -Tail 35 | Write-Host }
            throw "Process exited with code $($process.ExitCode). Log: $Log"
        }
    }
    finally { $process.Dispose() }
}

$versionLine = Get-Content -LiteralPath (Join-Path $project 'ProjectSettings\ProjectVersion.txt') |
    Select-String '^m_EditorVersion: (\S+)$'
$version = $versionLine.Matches[0].Groups[1].Value
if (-not $UnityPath) {
    $UnityPath = @(
        "D:\Tools\Unity\$version\Editor\Unity.exe",
        "$env:ProgramFiles\Unity\Hub\Editor\$version\Editor\Unity.exe"
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}
if (-not $UnityPath -or -not (Test-Path -LiteralPath $UnityPath -PathType Leaf)) {
    throw "Install Unity $version for Windows x64 and pass -UnityPath with the path to Unity.exe."
}
$UnityPath = (Get-Item -LiteralPath $UnityPath).FullName
$installedVersion = (Get-Item -LiteralPath $UnityPath).VersionInfo.ProductVersion
if (($installedVersion -split '_')[0] -ne $version) { throw "Expected Unity $version, found $installedVersion." }

$scratchBase = 'D:\Temp\Codex\RealLife\REALLIFE-001'
if (-not (Test-Path -LiteralPath 'D:\Temp' -PathType Container)) { throw 'D:\Temp is unavailable.' }
$run = [Guid]::NewGuid().ToString('N').Substring(0, 12)
$scratch = Join-Path $scratchBase $run
if (Test-Path -LiteralPath $scratch) { throw "Scratch path already exists: $scratch" }
New-Item -ItemType Directory -Path $scratch | Out-Null
$markerPath = Join-Path $scratch '.codex-owned.json'
@{ project = 'RealLife'; task = 'REALLIFE-001'; run = $run; created_at = [DateTime]::UtcNow.ToString('o'); absolute_root = $scratch } |
    ConvertTo-Json | Set-Content -LiteralPath $markerPath -Encoding utf8
Write-Host "Scratch: $scratch"

$completed = $false
try {
    $testLog = Join-Path $scratch 'editmode.log'
    $results = Join-Path $scratch 'editmode.xml'
    $commonArgs = @('-batchmode', '-nographics', '-projectPath', "`"$project`"", '-buildTarget', 'Win64')
    Invoke-CheckedProcess $UnityPath ($commonArgs + @('-runTests', '-testPlatform', 'EditMode',
        '-assemblyNames', 'RealLife.Tests', '-testResults', "`"$results`"", '-logFile', "`"$testLog`"")) $testLog 900
    [xml]$report = Get-Content -Raw -LiteralPath $results
    $testRun = $report.'test-run'
    if ($testRun.result -ne 'Passed' -or [int]$testRun.passed -lt 1 -or [int]$testRun.failed -ne 0) {
        throw "Edit Mode failed or ran no tests. Report: $results"
    }
    Write-Host "Edit Mode: $($testRun.passed) passed."

    if ($Action -eq 'Build') {
        $candidate = Join-Path $scratch 'candidate'
        $candidatePlayer = Join-Path $candidate 'RealLife.exe'
        $buildLog = Join-Path $scratch 'build.log'
        New-Item -ItemType Directory -Path $candidate | Out-Null
        Invoke-CheckedProcess $UnityPath ($commonArgs + @('-quit', '-executeMethod', 'RealLife.Editor.WindowsBuild.Build',
            '-realLifeOutput', "`"$candidatePlayer`"", '-logFile', "`"$buildLog`"")) $buildLog 1800

        $burstSymbols = Join-Path $candidate 'RealLife_BurstDebugInformation_DoNotShip'
        if (Test-Path -LiteralPath $burstSymbols) {
            Assert-NoLinks $burstSymbols
            Move-Item -LiteralPath $burstSymbols -Destination (Join-Path $scratch 'burst-debug')
        }

        $smokeLog = Join-Path $scratch 'player.log'
        $smokeData = Join-Path $scratch 'smoke'
        $playerArgs = @('-realLifeSmoke', "`"$smokeData`"", '-logFile', "`"$smokeLog`"",
            '-screen-fullscreen', '0', '-screen-width', '1280', '-screen-height', '800')
        Invoke-CheckedProcess $candidatePlayer $playerArgs $smokeLog 90
        if (-not (Select-String -LiteralPath $smokeLog -Pattern '^RealLife Windows smoke passed\.$' -Quiet)) {
            throw "Player did not confirm the gameplay scenario. Log: $smokeLog"
        }
        Write-Host 'Windows Player smoke passed.'

        $buildMarker = '.reallife-build.json'
        @{ project = 'RealLife'; directory = $release; editor = $version } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $candidate $buildMarker) -Encoding utf8
        New-Item -ItemType Directory -Path (Split-Path -Parent $release) -Force | Out-Null
        Assert-NoLinks $candidate
        Assert-NoLinks (Split-Path -Parent $release)
        $previous = Join-Path $scratch 'previous'
        if (Test-Path -LiteralPath $release) {
            $owner = Get-Content -Raw -LiteralPath (Join-Path $release $buildMarker) | ConvertFrom-Json
            if ($owner.project -ne 'RealLife' -or $owner.directory -ne $release) {
                throw "The existing Windows build has no matching ownership marker: $release"
            }
            Move-Item -LiteralPath $release -Destination $previous
        }
        try { Move-Item -LiteralPath $candidate -Destination $release }
        catch {
            if (Test-Path -LiteralPath $previous) { Move-Item -LiteralPath $previous -Destination $release }
            throw
        }
        Write-Host "Windows Player: $releasePlayer"
    }
    $completed = $true
}
finally {
    if ($completed -and -not $KeepDiagnostics) {
        $marker = Get-Content -Raw -LiteralPath $markerPath | ConvertFrom-Json
        $resolved = (Resolve-Path -LiteralPath $scratch).Path
        if ($resolved -ne $scratch -or $marker.absolute_root -ne $resolved -or $marker.run -ne $run -or
            $marker.project -ne 'RealLife' -or $marker.task -ne 'REALLIFE-001') {
            throw "Scratch ownership could not be verified: $scratch"
        }
        Assert-NoLinks $resolved
        $bytes = 0L
        foreach ($file in (Get-ChildItem -LiteralPath $resolved -File -Recurse -Force)) {
            $handle = [IO.File]::Open($file.FullName, 'Open', 'Read', 'None')
            try { $bytes += $handle.Length } finally { $handle.Dispose() }
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
        Write-Host "Scratch removed ($bytes bytes): $resolved"
    }
    else { Write-Host "Diagnostics retained: $scratch" }
}
