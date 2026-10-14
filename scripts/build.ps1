[CmdletBinding()]
param([switch]$SkipTests)
. (Join-Path $PSScriptRoot 'common.ps1')
Push-Location $ProjectRoot
try {
    Set-LocalDotnet
    Assert-References
    $artifactRoot = Assert-ProjectPath (Join-Path $ProjectRoot 'artifacts')
    $run = Assert-ProjectPath (Join-Path $artifactRoot ('build-' + [Guid]::NewGuid().ToString('N')))
    $output = Join-Path $run 'plugin'
    Invoke-Dotnet @('build','src/ScopedImport/ScopedImport.csproj','-c','Release','-o',$output,'--disable-build-servers')
    $checker = Join-Path $run 'checker'
    Invoke-Dotnet @('build','tools/ArtifactCheck/ArtifactCheck.csproj','-c','Release','-o',$checker,'--disable-build-servers')
    [xml]$project = Get-Content -LiteralPath 'src/ScopedImport/ScopedImport.csproj' -Raw
    $version = [string]$project.Project.PropertyGroup.AssemblyVersion
    $licenseExpression = [string]$project.Project.PropertyGroup.PackageLicenseExpression
    $licensePath = Join-Path $ProjectRoot 'LICENSE'
    if ($licenseExpression -ne 'GPL-3.0-only' -or -not (Test-Path -LiteralPath $licensePath)) { throw 'Declared GPL-3.0-only LICENSE is required.' }
    $licenseText = Get-Content -LiteralPath $licensePath -Raw
    if ($licenseText -notmatch 'GNU GENERAL PUBLIC LICENSE' -or $licenseText -notmatch 'Version 3, 29 June 2007') { throw 'Unexpected GPLv3 license text.' }
    $pluginGuid = '347d2c09-78a1-4697-9c3d-e23566122144'
    if ((Get-Content -LiteralPath 'src/ScopedImport/Plugin.cs' -Raw) -notmatch [Regex]::Escape($pluginGuid)) { throw 'Plugin identity mismatch.' }
    $dll = Join-Path $output 'ScopedImport.dll'
    $inspectionText = & $Dotnet (Join-Path $checker 'ArtifactCheck.dll') $dll $version
    if ($LASTEXITCODE -ne 0) { throw 'Artifact validation failed.' }
    $inspection = $inspectionText | ConvertFrom-Json
    foreach ($entry in $Lock.jellyfin.references.PSObject.Properties) {
        & $Dotnet (Join-Path $checker 'ArtifactCheck.dll') (Join-Path $LocalRoot ('jellyfin/server/' + $entry.Name)) $entry.Value.assemblyVersion | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Reference ABI validation failed.' }
    }
    if (-not $SkipTests) { & (Join-Path $PSScriptRoot 'test.ps1') }
    $stage = Assert-ProjectPath (Join-Path $run ('ScopedImport_' + $version))
    New-Item -ItemType Directory -Path $stage | Out-Null
    Copy-Item -LiteralPath $dll -Destination (Join-Path $stage 'ScopedImport.dll')
    $meta = @{name='定向入库 Scoped Import';guid=$pluginGuid;version=$version;targetAbi=$Lock.jellyfin.abi;
        status='Active';autoUpdate=$false;assemblies=@('ScopedImport.dll');description='按路径追加缺失电影与剧集。'}
    $meta | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stage 'meta.json') -Encoding utf8
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archivePath = Join-Path $run ('ScopedImport-' + $version + '-jf' + $Lock.jellyfin.version + '.zip')
    $archive = [IO.Compression.ZipFile]::Open($archivePath,[IO.Compression.ZipArchiveMode]::Create)
    $entries = @{}
    $folder = 'ScopedImport_' + $version
    $entries[$folder + '/ScopedImport.dll'] = Join-Path $stage 'ScopedImport.dll'
    $entries[$folder + '/meta.json'] = Join-Path $stage 'meta.json'
    foreach ($name in @('README.md','CHANGELOG.md','THIRD_PARTY_NOTICES.md','LICENSE')) { $entries[$name] = Join-Path $ProjectRoot $name }
    try {
        foreach ($name in @($entries.Keys | Sort-Object)) {
            $entry = $archive.CreateEntry($name,[IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(2000,1,1,0,0,0,[TimeSpan]::Zero)
            $stream = $entry.Open()
            try { $bytes = [IO.File]::ReadAllBytes($entries[$name]); $stream.Write($bytes,0,$bytes.Length) } finally { $stream.Dispose() }
        }
        $entry = $archive.CreateEntry('SHA256SUMS.txt')
        $entry.LastWriteTime = [DateTimeOffset]::new(2000,1,1,0,0,0,[TimeSpan]::Zero)
        $writer = [IO.StreamWriter]::new($entry.Open(),[Text.UTF8Encoding]::new($false))
        try { $writer.WriteLine($inspection.sha256 + '  ' + $folder + '/ScopedImport.dll') } finally { $writer.Dispose() }
    } finally { $archive.Dispose() }
    $zip = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        $expected = @($entries.Keys) + @('SHA256SUMS.txt')
        if ($zip.Entries.Count -ne $expected.Count -or @($zip.Entries | Where-Object FullName -NotIn $expected).Count) { throw 'Package whitelist violation.' }
        $stream = $zip.GetEntry($folder + '/ScopedImport.dll').Open()
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
        finally { $stream.Dispose() }
        if ($hash -ne $inspection.sha256) { throw 'ZIP binary differs from this build.' }
    } finally { $zip.Dispose() }
    $sourceHashes = @{}
    foreach ($file in Get-ChildItem -LiteralPath 'src/ScopedImport' -File) { $sourceHashes[$file.Name] = (Get-FileHash -LiteralPath $file.FullName).Hash.ToLowerInvariant() }
    $manifest = @{version=$version;guid=$pluginGuid;jellyfinAbi=$Lock.jellyfin.abi;assembly=$inspection;
        dependencyLockSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'dependencies.lock.json')).Hash.ToLowerInvariant();
        sourceSha256=$sourceHashes;package=[IO.Path]::GetFileName($archivePath);packageSha256=(Get-FileHash -LiteralPath $archivePath).Hash.ToLowerInvariant();
        offlineTestsExecuted=(-not $SkipTests);integrationTestsExecuted=$false;licenseDecision=$licenseExpression;sourceProvenance='Historical contributions incomplete; no specific conflict identified';productionAccessed=$false}
    $manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $run 'build-manifest.json') -Encoding utf8
    Write-Host ('Verified candidate package: ' + $archivePath)
    Write-Host 'GPL-3.0-only declared; historical source attribution and isolated integration validation remain outstanding. No Release or deployment performed.'
} finally { Pop-Location }
