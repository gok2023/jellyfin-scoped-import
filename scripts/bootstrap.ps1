# Downloads only official, locked build dependencies. Never starts Jellyfin.
[CmdletBinding()]
param()
. (Join-Path $PSScriptRoot 'common.ps1')
$cache = Assert-ProjectPath (Join-Path $LocalRoot 'downloads')
New-Item -ItemType Directory -Path $cache -Force | Out-Null
function Get-LockedArchive($Entry, [string]$Algorithm, [string]$Digest) {
    $target = Assert-ProjectPath (Join-Path $cache $Entry.file)
    if (-not (Test-Path -LiteralPath $target)) {
        $uri = [Uri]$Entry.url
        if ($uri.Scheme -ne 'https' -or $uri.Host -notin @('repo.jellyfin.org','sgp1.mirror.jellyfin.org','builds.dotnet.microsoft.com')) { throw 'Unapproved dependency host.' }
        Invoke-WebRequest -Uri $uri -OutFile $target -MaximumRedirection 0 -TimeoutSec 300
    }
    Assert-Digest $target $Algorithm $Digest
    return $target
}
function Expand-SafeZip([string]$Archive, [string]$Destination) {
    $destination = Assert-ProjectPath $Destination
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        foreach ($entry in $zip.Entries) {
            $target = [IO.Path]::GetFullPath((Join-Path $destination $entry.FullName))
            if (-not $target.StartsWith($destination + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe archive entry.' }
            Assert-ProjectPath $target | Out-Null
        }
    } finally { $zip.Dispose() }
    Expand-Archive -LiteralPath $Archive -DestinationPath $destination -Force
}
$sdkZip = Get-LockedArchive $Lock.sdk SHA512 $Lock.sdk.sha512
$sdkRoot = Assert-ProjectPath (Join-Path $LocalRoot 'sdk')
if (-not (Test-Path -LiteralPath (Join-Path $sdkRoot 'dotnet.exe'))) { Expand-SafeZip $sdkZip $sdkRoot }
$serverZip = Get-LockedArchive $Lock.jellyfin SHA256 $Lock.jellyfin.sha256
Assert-Digest $serverZip MD5 $Lock.jellyfin.md5
$serverRoot = Assert-ProjectPath (Join-Path $LocalRoot 'jellyfin/server')
New-Item -ItemType Directory -Path $serverRoot -Force | Out-Null
# Only locked compile/test references are extracted; no server is started or installed.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($serverZip)
try {
    foreach ($reference in $Lock.jellyfin.references.PSObject.Properties) {
        $entries = @($zip.Entries | Where-Object Name -EQ $reference.Name)
        if ($entries.Count -ne 1) { throw 'Unexpected reference archive layout.' }
        $target = Assert-ProjectPath (Join-Path $serverRoot $reference.Name)
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entries[0],$target,$true)
    }
    foreach ($reference in $Lock.jellyfin.testRuntimeReferences.PSObject.Properties) {
        $entries = @($zip.Entries | Where-Object Name -EQ $reference.Name)
        if ($entries.Count -ne 1) { throw 'Unexpected test runtime archive layout.' }
        $target = Assert-ProjectPath (Join-Path $LocalRoot ('jellyfin/test-runtime/' + $reference.Name))
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entries[0],$target,$true)
    }
} finally { $zip.Dispose() }
Assert-References
Assert-TestReferences
Push-Location $ProjectRoot
try { Set-LocalDotnet; Write-Host 'Verified SDK, six Jellyfin compile references and one offline test runtime reference. No server was started.' }
finally { Pop-Location }
