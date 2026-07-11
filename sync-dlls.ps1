# ============================================================================
#  sync-dlls.ps1
#
#  Builds the PinionCore.Remote projects and copies the resulting DLLs
#  into the PinionCore.NetSync.Package (Unity UPM package).
#
#  Notes:
#   - For the NetSync package, only files that ALREADY exist in the package
#     are overwritten. This guarantees we update the correct locations and
#     never introduce new untracked files (so Unity .meta GUIDs stay stable).
#   - .meta files are never touched.
#
#  Usage:   .\sync-dlls.ps1 [Release|Debug]   (default: Release)
# ============================================================================

param(
    [ValidateSet('Release', 'Debug')]
    [string]$Config = 'Release'
)

$ErrorActionPreference = 'Stop'

$Root = $PSScriptRoot
$Remote = Join-Path $Root 'PinionCore.Remote'
$Pkg = Join-Path $Root 'PinionCore.NetSync.Package'

$Tfm = 'netstandard2.1'
$TfmAnalyzer = 'netstandard2.0'

$Plugins = Join-Path $Pkg 'Runtime\Plugins'
$Analyzers = Join-Path $Pkg 'Analyzers'

# Overwrites <name>.dll/.pdb/.deps.json in $Dst, but ONLY for the extensions
# that already exist there (keeps the package layout intact).
function Sync-Dll {
    param([string]$Src, [string]$Dst, [string]$Name)

    foreach ($ext in 'dll', 'pdb', 'deps.json') {
        $dstFile = Join-Path $Dst "$Name.$ext"
        $srcFile = Join-Path $Src "$Name.$ext"
        if (Test-Path $dstFile) {
            if (Test-Path $srcFile) {
                Copy-Item $srcFile $dstFile -Force
                Write-Host "  [OK]   $Name.$ext"
            }
            else {
                Write-Host "  [WARN] source missing: $srcFile"
            }
        }
    }
}

Write-Host '============================================================'
Write-Host " Configuration : $Config"
Write-Host '============================================================'

# ---------------------------------------------------------------------------
#  1. Build the PinionCore.Remote projects
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host '=== [1/3] Building PinionCore.Remote projects ==='
$projects = @(
    'PinionCore.Network\PinionCore.Network.csproj'
    'PinionCore.Serialization\PinionCore.Serialization.csproj'
    'PinionCore.Utility\PinionCore.Utility\PinionCore.Utility.csproj'
    'PinionCore.Remote\PinionCore.Remote.csproj'
    'PinionCore.Remote.Client\PinionCore.Remote.Client.csproj'
    'PinionCore.Remote.Server\PinionCore.Remote.Server.csproj'
    'PinionCore.Remote.Ghost\PinionCore.Remote.Ghost.csproj'
    'PinionCore.Remote.Soul\PinionCore.Remote.Soul.csproj'
    'PinionCore.Remote.Standalone\PinionCore.Remote.Standalone.csproj'
    'PinionCore.Remote.Gateway\PinionCore.Remote.Gateway.csproj'
    'PinionCore.Remote.Gateway.Protocols\PinionCore.Remote.Gateway.Protocols.csproj'
    'PinionCore.Remote.Protocol.Identify\PinionCore.Remote.Protocol.Identify.csproj'
    'PinionCore.Remote.Tools.Protocol.Sources\PinionCore.Remote.Tools.Protocol.Sources.csproj'
)
foreach ($proj in $projects) {
    Write-Host "  building $proj"
    dotnet build (Join-Path $Remote $proj) -c $Config --nologo -v quiet
    if ($LASTEXITCODE -ne 0) {
        Write-Host ''
        Write-Host ' *** BUILD FAILED - aborting copy. ***'
        exit 1
    }
}

# ---------------------------------------------------------------------------
#  2. Copy Remote DLLs -> NetSync package Runtime\Plugins
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host '=== [2/3] Copying Remote DLLs -> Runtime\Plugins ==='

$remoteDlls = @(
    'PinionCore.Network'
    'PinionCore.Serialization'
    @{ Name = 'PinionCore.Utility'; Dir = 'PinionCore.Utility\PinionCore.Utility' }
    'PinionCore.Remote'
    'PinionCore.Remote.Client'
    'PinionCore.Remote.Server'
    'PinionCore.Remote.Ghost'
    'PinionCore.Remote.Soul'
    'PinionCore.Remote.Standalone'
    'PinionCore.Remote.Gateway'
    'PinionCore.Remote.Gateway.Protocols'
    'PinionCore.Remote.Protocol.Identify'
)
foreach ($entry in $remoteDlls) {
    if ($entry -is [hashtable]) { $name = $entry.Name; $dir = $entry.Dir }
    else { $name = $entry; $dir = $entry }
    Sync-Dll -Src (Join-Path $Remote "$dir\bin\$Config\$Tfm") -Dst $Plugins -Name $name
}

# ---------------------------------------------------------------------------
#  3. Copy the source-generator analyzer -> NetSync package Analyzers
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host '=== [3/3] Copying analyzer -> Analyzers ==='
Sync-Dll -Src (Join-Path $Remote "PinionCore.Remote.Tools.Protocol.Sources\bin\$Config\$TfmAnalyzer") -Dst $Analyzers -Name 'PinionCore.Remote.Tools.Protocol.Sources'

Write-Host ''
Write-Host '============================================================'
Write-Host ' Done.'
Write-Host '============================================================'
exit 0
