<#
.SYNOPSIS
    Removes BepInEx and the client mods that tools\install-client-mods.ps1 added to an Albion client folder.

.DESCRIPTION
    Removes exactly what the installer adds: the folders BepInEx\ (with the plugins, their configs, the generated
    interop assemblies and the log) and dotnet\, and the files winhttp.dll, doorstop_config.ini, .doorstop_version and
    changelog.txt. No other file of the client folder is touched. Asks for confirmation unless -Force is given.

    The client then starts exactly as before the installation.

.PARAMETER GameDir
    The Albion client folder that contains Albion-Online.exe.

.PARAMETER Force
    Do not ask for confirmation.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\uninstall-client-mods.ps1 -GameDir "C:\Games\Albion\game"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$GameDir,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $GameDir -PathType Container)) { throw "Folder not found: $GameDir" }
$GameDir = (Resolve-Path -LiteralPath $GameDir).ProviderPath
if (-not (Test-Path -LiteralPath (Join-Path $GameDir 'Albion-Online.exe') -PathType Leaf)) {
    throw "Albion-Online.exe not found in $GameDir. -GameDir must be the client folder that contains Albion-Online.exe."
}

$items = @()
$bep = Join-Path $GameDir 'BepInEx'
# BepInEx\ only when it really is BepInEx (its core assembly, or the installer's list of files).
if ((Test-Path -LiteralPath (Join-Path $bep 'core\BepInEx.Core.dll')) -or (Test-Path -LiteralPath (Join-Path $bep 'albionlocal-installed.txt'))) {
    $items += $bep
} elseif (Test-Path -LiteralPath $bep) {
    Write-Host "Skipping $bep : it does not look like a BepInEx installation." -ForegroundColor Yellow
}
$dotnet = Join-Path $GameDir 'dotnet'
if (Test-Path -LiteralPath $dotnet) { $items += $dotnet }
# With the installer's list (BepInEx\albionlocal-installed.txt), a root file it did not add (it was there before) is kept.
$listed = $null
$manifest = Join-Path $bep 'albionlocal-installed.txt'
if (Test-Path -LiteralPath $manifest) { $listed = @(Get-Content -LiteralPath $manifest | ForEach-Object { $_.Trim().ToLowerInvariant() }) }
foreach ($name in 'winhttp.dll', 'doorstop_config.ini', '.doorstop_version') {
    $p = Join-Path $GameDir $name
    if (-not (Test-Path -LiteralPath $p)) { continue }
    if ($listed -and ($listed -notcontains $name.ToLowerInvariant())) { Write-Host "Keeping $p : it was there before the installation." -ForegroundColor Yellow; continue }
    $items += $p
}
# changelog.txt is BepInEx's (the client ships none); remove it only when it is BepInEx's changelog.
$changelog = Join-Path $GameDir 'changelog.txt'
if (Test-Path -LiteralPath $changelog) {
    $head = (Get-Content -LiteralPath $changelog -TotalCount 5) -join "`n"
    if ($head -match 'v6\.0\.0|BepInEx') { $items += $changelog }
    else { Write-Host "Keeping $changelog : it is not BepInEx's changelog." -ForegroundColor Yellow }
}

if ($items.Count -eq 0) { Write-Host "Nothing to remove in $GameDir."; exit 0 }

Write-Host 'These items will be deleted:'
$items | ForEach-Object { Write-Host "  $_" }
Write-Host '(BepInEx\ includes the mods'' configs in BepInEx\config.)'
if (-not $Force) {
    $answer = Read-Host 'Delete them? [y/N]'
    if ($answer -notmatch '^(y|yes)$') { Write-Host 'Cancelled.'; exit 1 }
}
foreach ($i in $items) { Remove-Item -LiteralPath $i -Recurse -Force }
Write-Host 'Removed. The client starts as before the installation.' -ForegroundColor Green
