Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$LocalRoot = Join-Path $ProjectRoot '.local'
$Lock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'dependencies.lock.json') -Raw | ConvertFrom-Json
function Assert-ProjectPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($ProjectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Path outside project rejected.' }
    for ($part = $full; $part; $part = [IO.Path]::GetDirectoryName($part)) {
        if (Test-Path -LiteralPath $part) {
            if ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse point rejected.' }
        }
    }
    return $full
}
function Assert-Digest([string]$Path, [string]$Algorithm, [string]$Expected) {
    if ((Get-FileHash -LiteralPath $Path -Algorithm $Algorithm).Hash -ne $Expected) { throw ('Dependency hash mismatch: ' + [IO.Path]::GetFileName($Path)) }
}
function Set-LocalDotnet {
    $script:Dotnet = Assert-ProjectPath (Join-Path $LocalRoot 'sdk/dotnet.exe')
    if (-not (Test-Path -LiteralPath $Dotnet)) { throw 'Run scripts/bootstrap.ps1 first.' }
    $env:DOTNET_ROOT = Split-Path -Parent $Dotnet
    $env:DOTNET_CLI_HOME = Assert-ProjectPath (Join-Path $LocalRoot 'dotnet-home')
    $env:NUGET_PACKAGES = Assert-ProjectPath (Join-Path $LocalRoot 'nuget')
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'
    if ((& $Dotnet --version) -ne $Lock.sdk.version) { throw 'Wrong .NET SDK version.' }
}
function Assert-References {
    foreach ($entry in $Lock.jellyfin.references.PSObject.Properties) {
        $path = Assert-ProjectPath (Join-Path $LocalRoot ('jellyfin/server/' + $entry.Name))
        Assert-Digest $path SHA256 $entry.Value.sha256
    }
}
function Invoke-Dotnet([string[]]$Arguments) {
    & $Dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw ('dotnet failed: ' + ($Arguments -join ' ')) }
}
