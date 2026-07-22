[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$Target,
    [ValidateSet('Debug', 'Release')]
    [string]$TestConfiguration = 'Debug',
    [switch]$IncludePerformance
)

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Test-Squire.ps1') `
    -Configuration $TestConfiguration `
    -IncludePerformance:$IncludePerformance
& (Join-Path $PSScriptRoot 'Deploy-DevPlugin.ps1') -Target $Target
