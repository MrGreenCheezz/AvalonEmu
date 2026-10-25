<#
.SYNOPSIS
    Builds the server into its own folder (bin\run) and starts it with its console in this window.

.DESCRIPTION
    Building into bin\run means a running server never locks the normal build output, so `dotnet build` and the
    self-test keep working while it runs. The server keeps its database, log and backups in .\data of the repository.
    Type console / GM commands in this window, e.g. `.account create <name> <password>` and `.account level <name> Admin`
    (the web panel at http://127.0.0.1:8080/ needs an Admin account).

.PARAMETER GameDir
    The Albion client folder (the one with Albion-Online.exe). The server reads
    <GameDir>\Albion-Online_Data\StreamingAssets\GameData. Optional when -GameData is given or when
    config\common.jsonc (Paths:ClientGameData) already points at it (default: ..\game-local next to the repository).

.PARAMETER GameData
    The client's GameData folder itself (overrides -GameDir).

.PARAMETER NoBuild
    Start the last build in bin\run without building.

.PARAMETER ServerArgs
    Further arguments for the server, e.g. '--Log:Level=Debug' or '--Network:BindAddress=0.0.0.0'.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\start-server.ps1 -GameDir "C:\Games\Albion\game"
#>
[CmdletBinding()]
param(
    [string]$GameDir,
    [string]$GameData,
    [switch]$NoBuild,
    [string[]]$ServerArgs = @()
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

$args2 = @('-v')
if ($GameDir -and -not $GameData) { $GameData = Join-Path $GameDir 'Albion-Online_Data\StreamingAssets\GameData' }
if ($GameData) {
    if (-not (Test-Path -LiteralPath $GameData -PathType Container)) { throw "GameData folder not found: $GameData" }
    $GameData = (Resolve-Path -LiteralPath $GameData).ProviderPath
    $args2 += "--Paths:ClientGameData=$GameData"
}
$args2 += $ServerArgs

if (-not $NoBuild) {
    & dotnet build src/AlbionLocal.Server -nologo -v q -o bin/run
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}
$exe = Join-Path $RepoRoot 'bin\run\AlbionLocal.Server.exe'
if (Test-Path -LiteralPath $exe) { & $exe @args2 }
else { & dotnet (Join-Path $RepoRoot 'bin\run\AlbionLocal.Server.dll') @args2 }
