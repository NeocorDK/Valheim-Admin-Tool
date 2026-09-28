<#
    Builds a release:
      dist\ValheimAdmin-<version>-win-x64.zip   agent (self-contained exe + panel) and the mod, for server owners
      dist\neocor-ValheimAdmin-<version>.zip    Thunderstore package of the mod, for r2modman / Thunderstore

    Usage:  powershell -ExecutionPolicy Bypass -File package.ps1 [-ValheimPath "G:\Steam\steamapps\common\Valheim"] [-SkipTests]
#>
param(
    [string]$ValheimPath = 'G:\Steam\steamapps\common\Valheim',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$thunderstore = Join-Path $root 'package\thunderstore'

function Invoke-Checked([string]$what, [scriptblock]$block) {
    Write-Host "== $what"
    & $block
    if ($LASTEXITCODE -ne 0) { throw "$what failed ($LASTEXITCODE)" }
}

# Versions must agree: BepInPlugin, Thunderstore manifest, agent assembly.
$manifest = Get-Content (Join-Path $thunderstore 'manifest.json') -Raw | ConvertFrom-Json
$version = $manifest.version_number
$pluginSrc = Get-Content (Join-Path $root 'ValheimAdmin.Plugin\BepInExPlugin.cs') -Raw
if ($pluginSrc -notmatch 'pluginVersion\s*=\s*"([^"]+)"' -or $Matches[1] -ne $version) {
    throw "Version mismatch: manifest.json says $version, BepInExPlugin.cs says $($Matches[1])."
}
$agentProj = Get-Content (Join-Path $root 'ValheimAdmin.Agent\ValheimAdmin.Agent.csproj') -Raw
if ($agentProj -notmatch '<Version>([^<]+)</Version>' -or $Matches[1] -ne $version) {
    throw "Version mismatch: manifest.json says $version, ValheimAdmin.Agent.csproj says $($Matches[1])."
}

if (-not $SkipTests) {
    Invoke-Checked 'tests' { dotnet test (Join-Path $root 'ValheimAdmin.Agent.Tests') -c Release --nologo -v q }
}

Invoke-Checked 'plugin' { dotnet build (Join-Path $root 'ValheimAdmin.Plugin') -c Release --nologo -v q "-p:ValheimPath=$ValheimPath" }
$pluginDll = Join-Path $dist 'plugin\ValheimAdmin.dll'

$agentOut = Join-Path $dist 'agent'
if (Test-Path $agentOut) { Remove-Item -Recurse -Force $agentOut }
Invoke-Checked 'agent' { dotnet publish (Join-Path $root 'ValheimAdmin.Agent') -c Release -r win-x64 --nologo -v q -o $agentOut }
Get-ChildItem $agentOut -Filter '*.pdb' | Remove-Item
Remove-Item (Join-Path $agentOut 'web.config') -ErrorAction SilentlyContinue   # IIS only
Get-ChildItem (Join-Path $agentOut 'wwwroot') -Filter '*.gz' -ErrorAction SilentlyContinue | Remove-Item

# Zip with forward slashes: Compress-Archive on Windows PowerShell 5.1 writes backslashes,
# which Node-based mod managers mis-extract.
function New-Zip([string]$zip, [hashtable]$entries) {
    if (Test-Path $zip) { Remove-Item $zip }
    $fs = [System.IO.File]::Open($zip, [System.IO.FileMode]::CreateNew)
    $ar = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $entries.Keys) {
            $source = $entries[$name]
            $files = if (Test-Path $source -PathType Container) {
                Get-ChildItem $source -Recurse -File | ForEach-Object { @{ Path = $_.FullName; Name = $name + '/' + $_.FullName.Substring((Resolve-Path $source).Path.Length + 1).Replace('\', '/') } }
            } else { @(@{ Path = $source; Name = $name }) }
            foreach ($f in $files) {
                $entry = $ar.CreateEntry($f.Name, [System.IO.Compression.CompressionLevel]::Optimal)
                $stream = $entry.Open()
                $bytes = [System.IO.File]::ReadAllBytes($f.Path)
                $stream.Write($bytes, 0, $bytes.Length)
                $stream.Dispose()
            }
        }
    } finally { $ar.Dispose(); $fs.Dispose() }
    Write-Host "Packaged: $zip"
}

New-Zip (Join-Path $dist "neocor-ValheimAdmin-$version.zip") @{
    'manifest.json'          = Join-Path $thunderstore 'manifest.json'
    'icon.png'               = Join-Path $thunderstore 'icon.png'
    'README.md'              = Join-Path $thunderstore 'README.md'
    'plugins/ValheimAdmin.dll' = $pluginDll
}

New-Zip (Join-Path $dist "ValheimAdmin-$version-win-x64.zip") @{
    'ValheimAdmin/agent'                    = $agentOut
    'ValheimAdmin/mod/ValheimAdmin.dll'     = $pluginDll
    'ValheimAdmin/README.md'                = Join-Path $root 'README.md'
    'ValheimAdmin/README.ru.md'             = Join-Path $root 'README.ru.md'
    'ValheimAdmin/LICENSE'                  = Join-Path $root 'LICENSE'
}
