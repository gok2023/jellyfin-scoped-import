# Offline only. Integration scripts require a separately verified disposable host.
[CmdletBinding()]
param()
. (Join-Path $PSScriptRoot 'common.ps1')
Push-Location $ProjectRoot
try {
    Set-LocalDotnet
    Assert-References
    Assert-TestReferences
    & node tests/ui/test-login.cjs
    if ($LASTEXITCODE -ne 0) { throw 'Login UI tests failed.' }
    & node tests/ui/test-config-page.cjs
    if ($LASTEXITCODE -ne 0) { throw 'Configuration UI tests failed.' }
    Invoke-Dotnet @('run','--project','tests/unit/NamingTests/NamingTests.csproj','-c','Release','--no-launch-profile')
    & node tests/ui/test-import.cjs
    if ($LASTEXITCODE -ne 0) { throw 'Import UI tests failed.' }
    & node tests/ui/test-operation-recovery.cjs
    if ($LASTEXITCODE -ne 0) { throw 'Operation recovery UI tests failed.' }
    Invoke-Dotnet @('run','--project','tests/unit/ImportTests/ImportTests.csproj','-c','Release','--no-launch-profile')
    Invoke-Dotnet @('build','tests/integration/MetadataFixtures/MetadataFixtures.csproj','-c','Release','--disable-build-servers')
    & python -B -m unittest discover -s tests/unit -v
    if ($LASTEXITCODE -ne 0) { throw 'Integration safety tests failed.' }
} finally { Pop-Location }
