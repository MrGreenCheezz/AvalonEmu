<#
.SYNOPSIS
    Builds the client mods from source (client-mods\) and copies them into <GameDir>\BepInEx\plugins.

.DESCRIPTION
    The mods compile against the BepInEx core assemblies (<GameDir>\BepInEx\core) and the interop assemblies that
    BepInEx generates from the client on its first launch (<GameDir>\BepInEx\interop). So: install BepInEx first
    (tools\install-client-mods.ps1), start the client once and wait until it reaches the login screen, then run this.
    Needs the .NET SDK (the projects target net6.0).

    Only the plugin DLLs in BepInEx\plugins are replaced (removed, then copied); no game file is touched.

.PARAMETER GameDir
    The Albion client folder that contains Albion-Online.exe and BepInEx.

.PARAMETER Mods
    Which mods to build (default: CameraMod, GraphicsMod, ActionBarsMod).

.PARAMETER NoCopy
    Only build (output in client-mods\<Mod>\bin\Release\net6.0).

.PARAMETER UpdateDist
    Also copy the built DLLs into client-mods\dist (for maintainers).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\build-client-mods.ps1 -GameDir "C:\Games\Albion\game"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$GameDir,
    [string[]]$Mods = @('CameraMod', 'GraphicsMod', 'ActionBarsMod'),
    [switch]$NoCopy,
    [switch]$UpdateDist
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$ModsDir  = Join-Path $RepoRoot 'client-mods'

if (-not (Test-Path -LiteralPath $GameDir -PathType Container)) { throw "Folder not found: $GameDir" }
$GameDir = (Resolve-Path -LiteralPath $GameDir).ProviderPath
if (-not (Test-Path -LiteralPath (Join-Path $GameDir 'BepInEx\core\BepInEx.Core.dll'))) {
    throw "BepInEx is not installed in $GameDir (BepInEx\core\BepInEx.Core.dll missing). Run tools\install-client-mods.ps1 first."
}
if (-not (Test-Path -LiteralPath (Join-Path $GameDir 'BepInEx\interop\Assembly-CSharp.dll'))) {
    throw "BepInEx\interop\Assembly-CSharp.dll missing: start the client once with BepInEx (the first launch generates the interop assemblies), then run this again."
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'The .NET SDK (dotnet) is not on PATH.' }

$plugins = Join-Path $GameDir 'BepInEx\plugins'
foreach ($m in $Mods) {
    $proj = Join-Path $ModsDir "$m\$m.csproj"
    if (-not (Test-Path -LiteralPath $proj)) { throw "Project not found: $proj" }
    Write-Host "Building $m ..." -ForegroundColor Cyan
    & dotnet build $proj -c Release -nologo -v q "-p:GameDir=$GameDir"
    if ($LASTEXITCODE -ne 0) { throw "Build of $m failed." }
    $dll = Join-Path $ModsDir "$m\bin\Release\net6.0\$m.dll"
    if ($UpdateDist) {
        $dist = Join-Path $ModsDir 'dist'
        if (-not (Test-Path -LiteralPath $dist)) { New-Item -ItemType Directory -Path $dist | Out-Null }
        Copy-Item -LiteralPath $dll -Destination (Join-Path $dist "$m.dll") -Force
    }
    if (-not $NoCopy) {
        if (-not (Test-Path -LiteralPath $plugins)) { New-Item -ItemType Directory -Path $plugins -Force | Out-Null }
        $dest = Join-Path $plugins "$m.dll"
        if (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest -Force }   # replace, never write in place
        Copy-Item -LiteralPath $dll -Destination $dest
        Write-Host "  -> $dest"
    }
}
Write-Host 'Done. Restart the client to load the new build.' -ForegroundColor Green
