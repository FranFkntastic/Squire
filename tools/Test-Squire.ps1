[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$IncludePerformance
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$testProject = Join-Path $repoRoot 'tests\Squire.Tests\Squire.Tests.csproj'

& dotnet test $testProject `
    --configuration $Configuration `
    --filter 'Category!=Performance' `
    --logger 'console;verbosity=minimal'
if ($LASTEXITCODE -ne 0) {
    throw "Squire functional tests failed with exit code $LASTEXITCODE."
}

if ($IncludePerformance) {
    & dotnet test $testProject `
        --configuration Release `
        --filter 'Category=Performance' `
        --maxcpucount:1 `
        --logger 'console;verbosity=detailed'
    if ($LASTEXITCODE -ne 0) {
        throw "Squire performance gate failed with exit code $LASTEXITCODE."
    }
}
