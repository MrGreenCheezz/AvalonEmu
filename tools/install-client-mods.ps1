<#
.SYNOPSIS
    Installs BepInEx 6 (bleeding edge #788) and the prebuilt client mods (client-mods/dist) into an Albion client folder.

.DESCRIPTION
    For use with the LOCAL server of this repository only. Never start a client with BepInEx against the official
    servers. Installing a plugin loader into a game client is your own decision and responsibility.

    What it does:
      1. Checks that -GameDir is a client folder (it must contain Albion-Online.exe) and prints the warnings.
      2. Downloads BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788+5b766a3.zip from builds.bepinex.dev (its official build
         server) and verifies its SHA-256; a mismatch aborts the installation.
      3. Extracts it into -GameDir WITHOUT overwriting any existing file. Files that this script installed on an earlier
         run (listed in BepInEx\albionlocal-installed.txt) are replaced; every other existing file is left alone.
      4. Copies client-mods\dist\*.dll into <GameDir>\BepInEx\plugins.

    It only ADDS files. It never edits, patches or deletes an existing game file, and it does not touch, disable or
    bypass any anti-cheat. If the client folder is a copy whose files are NTFS hard links to another install, adding
    files is safe, but never edit an existing file in place: the original install would change too.

    Added to the client folder: winhttp.dll (Doorstop loader), doorstop_config.ini, .doorstop_version, changelog.txt,
    BepInEx\ and dotnet\. Remove them with tools\uninstall-client-mods.ps1. To disable the mods without uninstalling,
    rename or delete winhttp.dll.

.PARAMETER GameDir
    The Albion client folder that contains Albion-Online.exe.

.PARAMETER SkipBepInEx
    Do not download or extract BepInEx (it is already installed); only copy the plugin DLLs.

.PARAMETER ZipPath
    Use an already downloaded BepInEx zip instead of downloading it. Its SHA-256 is verified all the same.

.PARAMETER Force
    Do not ask for confirmation.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\install-client-mods.ps1 -GameDir "C:\Games\Albion\game"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$GameDir,
    [switch]$SkipBepInEx,
    [string]$ZipPath,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$BepInExUrl    = 'https://builds.bepinex.dev/projects/bepinex_be/788/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788%2B5b766a3.zip'
$BepInExSha256 = 'f4cc496bd098a0df4164b81e3737297707f13a47c2478dba2f60eefab784817a'
$ClientBuild   = '1.32.030.345319'
$ManifestName  = 'albionlocal-installed.txt'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$DistDir  = Join-Path $RepoRoot 'client-mods\dist'

function Write-Warn([string]$Text) { Write-Host $Text -ForegroundColor Yellow }

# Number of names (hard links) of a file; 1 for an ordinary file, 0 when it cannot be determined.
function Get-LinkCount([string]$Path) {
    try {
        $out = & fsutil.exe hardlink list $Path 2>$null
        if ($LASTEXITCODE -ne 0) { return 0 }
        return @($out | Where-Object { $_ -and $_.Trim() }).Count
    } catch { return 0 }
}

# --- 1. Check the folder ----------------------------------------------------------------------------------------
if (-not (Test-Path -LiteralPath $GameDir -PathType Container)) { throw "Folder not found: $GameDir" }
$GameDir = (Resolve-Path -LiteralPath $GameDir).ProviderPath
$exe = Join-Path $GameDir 'Albion-Online.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "Albion-Online.exe not found in $GameDir. -GameDir must be the client folder that contains Albion-Online.exe."
}
if (-not (Test-Path -LiteralPath (Join-Path $GameDir 'GameAssembly.dll'))) {
    Write-Warn 'GameAssembly.dll not found: this does not look like the IL2CPP Windows client the mods are built for.'
}

Write-Host ''
Write-Host 'Albion local server - client mods installer' -ForegroundColor Cyan
Write-Host "Client folder: $GameDir"
$versionFile = Join-Path $GameDir 'version.txt'
if (Test-Path -LiteralPath $versionFile) {
    $ver = (Get-Content -LiteralPath $versionFile -TotalCount 1)
    Write-Host "Client version: $ver"
    if ($ver -notmatch [regex]::Escape($ClientBuild)) {
        Write-Warn "The mods were built for client build $ClientBuild; other builds may fail to hook (the mods log it)."
    }
}
Write-Host ''
Write-Warn 'WARNING'
Write-Warn ' - Use this client with the LOCAL server only. Never start a client with BepInEx against the official servers.'
Write-Warn ' - Installing a plugin loader into a game client may break the terms of service that apply to you.'
Write-Warn '   It is your own decision and your own responsibility.'
Write-Warn ' - This script only adds files. It does not modify, patch or bypass anything, including any anti-cheat.'

$beMarkers = @('BattlEye', 'Albion-Online_BE.exe') | Where-Object { Test-Path -LiteralPath (Join-Path $GameDir $_) }
if ($beMarkers) {
    Write-Warn " - This folder contains anti-cheat launcher files ($($beMarkers -join ', ')). BepInEx and an anti-cheat do"
    Write-Warn '   not mix: use a separate client copy that only ever connects to the local server. Nothing here modifies,'
    Write-Warn '   disables or bypasses the anti-cheat.'
}

$links = Get-LinkCount $exe
if ($links -gt 1) {
    Write-Warn " - Albion-Online.exe has $links names: the files of this folder are NTFS hard links to another install."
    Write-Warn '   Adding files (what this script does) is safe. Never edit an existing file of this folder in place:'
    Write-Warn '   the other install would change too.'
}
Write-Host ''

if (-not $Force) {
    $answer = Read-Host 'Continue? [y/N]'
    if ($answer -notmatch '^(y|yes)$') { Write-Host 'Cancelled.'; exit 1 }
}

$dlls = @(Get-ChildItem -LiteralPath $DistDir -Filter '*.dll' -File -ErrorAction SilentlyContinue)
if ($dlls.Count -eq 0) { throw "No prebuilt plugins in $DistDir" }

$bepDir       = Join-Path $GameDir 'BepInEx'
$manifestPath = Join-Path $bepDir $ManifestName
$previous = @{}
if (Test-Path -LiteralPath $manifestPath) {
    foreach ($line in Get-Content -LiteralPath $manifestPath) { if ($line.Trim()) { $previous[$line.Trim().ToLowerInvariant()] = $true } }
}
$installed = New-Object System.Collections.Generic.List[string]
foreach ($k in $previous.Keys) { $installed.Add($k) }

# --- 2./3. BepInEx ------------------------------------------------------------------------------------------------
if (-not $SkipBepInEx) {
    $work = Join-Path ([IO.Path]::GetTempPath()) ('albionlocal-bepinex-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $work | Out-Null
    try {
        if ($ZipPath) {
            $zip = (Resolve-Path -LiteralPath $ZipPath).ProviderPath
            Write-Host "Using $zip"
        } else {
            $zip = Join-Path $work 'BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788+5b766a3.zip'
            Write-Host "Downloading $BepInExUrl"
            [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
            $ProgressPreference = 'SilentlyContinue'
            Invoke-WebRequest -Uri $BepInExUrl -OutFile $zip -UseBasicParsing
        }
        $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -ne $BepInExSha256) {
            throw "SHA-256 mismatch for the BepInEx zip: expected $BepInExSha256, got $hash. Nothing was installed."
        }
        Write-Host 'SHA-256 verified.' -ForegroundColor Green

        $stage = Join-Path $work 'x'
        Expand-Archive -LiteralPath $zip -DestinationPath $stage
        $added = 0; $replaced = 0; $skipped = New-Object System.Collections.Generic.List[string]
        foreach ($f in Get-ChildItem -LiteralPath $stage -Recurse -File -Force) {
            $rel = $f.FullName.Substring($stage.Length).TrimStart('\', '/')
            $key = $rel.ToLowerInvariant()
            $dest = Join-Path $GameDir $rel
            if (Test-Path -LiteralPath $dest) {
                if (-not $previous.ContainsKey($key)) { $skipped.Add($rel); continue }
                # Our own file from an earlier run: replace it (remove, then copy - never written in place).
                Remove-Item -LiteralPath $dest -Force
                $replaced++
            } else { $added++ }
            $parent = Split-Path -Parent $dest
            if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
            Copy-Item -LiteralPath $f.FullName -Destination $dest
            if (-not $previous.ContainsKey($key)) { $installed.Add($key); $previous[$key] = $true }
        }
        Write-Host "BepInEx: $added file(s) added, $replaced replaced (own files of an earlier run), $($skipped.Count) existing file(s) left untouched."
        foreach ($s in ($skipped | Select-Object -First 10)) { Write-Host "  kept existing: $s" }
        if ($skipped.Count -gt 10) { Write-Host "  ... and $($skipped.Count - 10) more" }
    } finally {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
} elseif (-not (Test-Path -LiteralPath (Join-Path $bepDir 'core\BepInEx.Core.dll'))) {
    Write-Warn 'BepInEx does not seem to be installed (BepInEx\core\BepInEx.Core.dll missing); the plugins will not load without it.'
}

# --- 4. Plugins ---------------------------------------------------------------------------------------------------
$plugins = Join-Path $bepDir 'plugins'
if (-not (Test-Path -LiteralPath $plugins)) { New-Item -ItemType Directory -Path $plugins -Force | Out-Null }
foreach ($d in $dlls) {
    $dest = Join-Path $plugins $d.Name
    if (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest -Force }   # replace, never write in place
    Copy-Item -LiteralPath $d.FullName -Destination $dest
    $key = ('BepInEx\plugins\' + $d.Name).ToLowerInvariant()
    if (-not $previous.ContainsKey($key)) { $installed.Add($key); $previous[$key] = $true }
    Write-Host "Plugin installed: BepInEx\plugins\$($d.Name)"
}

if (-not (Test-Path -LiteralPath $bepDir)) { New-Item -ItemType Directory -Path $bepDir -Force | Out-Null }
Set-Content -LiteralPath $manifestPath -Value ($installed | Sort-Object -Unique) -Encoding ASCII

Write-Host ''
Write-Host 'Done. Next steps:' -ForegroundColor Green
Write-Host ' 1. Start the local server (tools\start-server.ps1) and then this client.'
Write-Host ' 2. The FIRST launch takes several minutes: BepInEx downloads the Unity base libraries and generates the interop'
Write-Host '    assemblies (BepInEx\interop). Later launches are fast.'
Write-Host " 3. Log: $(Join-Path $bepDir 'LogOutput.log') (lines starting with CameraMod:, GraphicsMod:, ActionBarsMod:)."
Write-Host " 4. Configs appear after the first run in $(Join-Path $bepDir 'config') (albionlocal.*.cfg)."
Write-Host ' Disable: rename winhttp.dll. Uninstall: tools\uninstall-client-mods.ps1 -GameDir "..."'
