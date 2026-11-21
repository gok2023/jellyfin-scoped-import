# Acquire locked references only. Never installs or starts Jellyfin.
[CmdletBinding()]
param([string]$SdkArchive, [string]$ServerArchive)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$project=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$local=Join-Path $project '.local'
$lock=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'dependencies-jf12.lock.json') -Raw | ConvertFrom-Json
$cache=Assert-ProjectPath (Join-Path $local 'downloads')
New-Item -ItemType Directory -Path $cache -Force | Out-Null
function Archive($Entry, [string]$Existing, [string]$Algorithm, [string]$Expected) {
    $file=$Existing
    if (!$file) {
        $uri=[Uri]$Entry.url
        if ($uri.Scheme -ne 'https' -or $uri.Host -notin @('repo.jellyfin.org','builds.dotnet.microsoft.com')) { throw 'Unapproved host' }
        $file=Join-Path $cache ([IO.Path]::GetFileName($uri.AbsolutePath))
        if (!(Test-Path -LiteralPath $file)) { Invoke-WebRequest -Uri $uri -OutFile $file -MaximumRedirection 0 }
    }
    if ((Get-FileHash -LiteralPath $file -Algorithm $Algorithm).Hash -ne $Expected) { throw 'Dependency archive hash mismatch' }
    return [IO.Path]::GetFullPath($file)
}
$sdk=Archive $lock.sdk $SdkArchive SHA512 $lock.sdk.sha512
$server=Archive $lock.jellyfin $ServerArchive SHA256 $lock.jellyfin.sha256
$sdkRoot=Assert-ProjectPath (Join-Path $local 'sdk12')
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (!(Test-Path -LiteralPath (Join-Path $sdkRoot 'dotnet.exe'))) {
    $zip=[IO.Compression.ZipFile]::OpenRead($sdk)
    try {
        foreach ($entry in $zip.Entries) {
            $target=[IO.Path]::GetFullPath((Join-Path $sdkRoot $entry.FullName))
            if (!$target.StartsWith($sdkRoot + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe archive path' }
        }
    } finally { $zip.Dispose() }
    Expand-Archive -LiteralPath $sdk -DestinationPath $sdkRoot
}
$zip=[IO.Compression.ZipFile]::OpenRead($server)
try {
    foreach ($group in @('references','testRuntimeReferences')) {
        $folder=if($group -eq 'references'){'server'}else{'test-runtime'}
        $directory=Assert-ProjectPath (Join-Path $local ('jellyfin12/' + $folder))
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        if ((Get-Item -LiteralPath $directory).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Redirected reference directory' }
        foreach ($entry in $lock.jellyfin.$group.PSObject.Properties) {
            $matches=@($zip.Entries | Where-Object Name -EQ $entry.Name)
            if ($matches.Count -ne 1) { throw 'Unexpected reference archive layout' }
            $target=Assert-ProjectPath (Join-Path $directory $entry.Name)
            [IO.Compression.ZipFileExtensions]::ExtractToFile($matches[0],$target,$true)
            if ((Get-FileHash -LiteralPath $target).Hash -ne $entry.Value.sha256) { throw 'Reference hash mismatch' }
        }
    }
} finally { $zip.Dispose() }
Write-Host 'Locked 12.4 compile references and SDK ready. No server started.'
