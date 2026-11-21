# Locked 12.4 build; never starts a host or imports media.
[CmdletBinding()]
param([string]$DotnetPath)
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if (!$DotnetPath) { $DotnetPath = Join-Path $project '.local/sdk12/dotnet.exe' }
$lock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'dependencies-jf12.lock.json') -Raw | ConvertFrom-Json
$refs = Join-Path $project '.local/jellyfin12/server'
foreach ($group in @('references','testRuntimeReferences')) {
    foreach ($entry in $lock.jellyfin.$group.PSObject.Properties) {
        $folder = if ($group -eq 'references') { 'server' } else { 'test-runtime' }
        $file = Join-Path $project ('.local/jellyfin12/' + $folder + '/' + $entry.Name)
        if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.Value.sha256) { throw "Locked reference mismatch: $($entry.Name)" }
    }
}
$run = Join-Path $project ('artifacts/build-jf12-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run | Out-Null
@{sdk=@{version=$lock.sdk.version;rollForward='disable';allowPrerelease=$false}} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'global.json') -Encoding utf8
Push-Location $run
try {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
    $env:DOTNET_ROOT=Split-Path -Parent $DotnetPath
    if ((& $DotnetPath --version) -ne $lock.sdk.version) { throw 'Unexpected SDK version' }
    function Invoke-Build([string[]]$Arguments) {
        & $DotnetPath @Arguments "-p:JellyfinVersion=12.4" "-p:JellyfinReferenceDir=$refs" --nologo
        if ($LASTEXITCODE -ne 0) { throw '12.4 build/check failed' }
    }
    $plugin=Join-Path $run 'plugin'
    Invoke-Build @('build',(Join-Path $project 'src/ScopedImport/ScopedImport.csproj'),'-c','Release','-o',$plugin)
    $checker=Join-Path $run 'checker'
    Invoke-Build @('build',(Join-Path $project 'tools/ArtifactCheck/ArtifactCheck.csproj'),'-c','Release','-o',$checker)
    $inspection = & $DotnetPath (Join-Path $checker 'ArtifactCheck.dll') (Join-Path $plugin 'ScopedImport.dll') '0.2.0.3' $lock.jellyfin.abi
    if ($LASTEXITCODE -ne 0) { throw 'Artifact ABI/resources check failed' }
    $tests=Join-Path $run 'tests'
    Invoke-Build @('build',(Join-Path $project 'tests/unit/PersistenceTests/PersistenceTests.csproj'),'-c','Release','-o',$tests)
    & $DotnetPath (Join-Path $tests 'PersistenceTests.dll')
    if ($LASTEXITCODE -ne 0) { throw 'Persistence safety check failed' }
    & node (Join-Path $project 'tests/ui/test-login.cjs')
    if ($LASTEXITCODE -ne 0) { throw 'Login UI check failed' }
    & node (Join-Path $project 'tests/ui/test-config-page.cjs')
    if ($LASTEXITCODE -ne 0) { throw 'Configuration UI check failed' }
    $stage=Join-Path $run 'ScopedImport_0.2.0.3'
    New-Item -ItemType Directory -Path $stage | Out-Null
    Copy-Item -LiteralPath (Join-Path $plugin 'ScopedImport.dll') -Destination $stage
    @{name='Scoped Import';guid='347d2c09-78a1-4697-9c3d-e23566122144';version='0.2.0.3';targetAbi=$lock.jellyfin.abi;status='Active';autoUpdate=$false;assemblies=@('ScopedImport.dll');description='按路径追加缺失电影与剧集，保留受限元数据刷新与写入保护。'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'meta.json') -Encoding utf8
    $package=Join-Path $run 'ScopedImport-0.2.0.3-jf12.4.zip'
    $sums=Join-Path $run 'SHA256SUMS.txt'
    (($inspection | ConvertFrom-Json).sha256 + '  ScopedImport_0.2.0.3/ScopedImport.dll') | Set-Content -LiteralPath $sums -Encoding utf8
    Compress-Archive -LiteralPath $stage,(Join-Path $project 'README.md'),(Join-Path $project 'CHANGELOG.md'),(Join-Path $project 'LICENSE'),(Join-Path $project 'THIRD_PARTY_NOTICES.md'),$sums -DestinationPath $package
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip=[IO.Compression.ZipFile]::OpenRead($package)
    try {
        $expected=@('ScopedImport_0.2.0.3/ScopedImport.dll','ScopedImport_0.2.0.3/meta.json','README.md','CHANGELOG.md','LICENSE','THIRD_PARTY_NOTICES.md','SHA256SUMS.txt')
        $files=@($zip.Entries | Where-Object { !$_.FullName.EndsWith('/') })
        if ($files.Count -ne 7 -or @($files | Where-Object FullName -NotIn $expected).Count) { throw 'Package whitelist violation' }
        $stream=$zip.GetEntry('ScopedImport_0.2.0.3/ScopedImport.dll').Open()
        try { $digest=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
        finally { $stream.Dispose() }
        if ($digest -ne ($inspection | ConvertFrom-Json).sha256) { throw 'Package DLL differs from inspected build' }
    } finally { $zip.Dispose() }
    $sourceHashes=@{}
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $project 'src/ScopedImport') -File) { $sourceHashes[$file.Name]=(Get-FileHash -LiteralPath $file.FullName).Hash.ToLowerInvariant() }
    @{version='0.2.0.3';jellyfinAbi=$lock.jellyfin.abi;assembly=($inspection|ConvertFrom-Json);sourceSha256=$sourceHashes;packageSha256=(Get-FileHash -LiteralPath $package).Hash.ToLowerInvariant();dependencyLockSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'dependencies-jf12.lock.json')).Hash.ToLowerInvariant();safetyTestsExecuted=$true;integrationTestsExecuted=$false;productionAccessed=$false} | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $run 'build-manifest.json') -Encoding utf8
    Write-Host "Verified 12.4 candidate: $run"
} finally { Pop-Location }
