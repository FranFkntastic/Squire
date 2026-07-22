[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $Target,
    [switch] $SkipBuild
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\Squire\Squire.csproj"
$source = Join-Path $repoRoot "src\Squire\bin\Release"
$targetPath = [System.IO.Path]::GetFullPath($Target)
if (-not $SkipBuild) {
    dotnet build $project -c Release
    if ($LASTEXITCODE -ne 0) { throw "Squire Release build failed with exit code $LASTEXITCODE." }
}
$assembly = Join-Path $source "Squire.dll"
$manifest = Join-Path $source "Squire.json"
if (-not (Test-Path -LiteralPath $assembly) -or -not (Test-Path -LiteralPath $manifest)) {
    throw "Release output is incomplete at '$source'."
}
$targetParent = Split-Path -Parent $targetPath
if (-not (Test-Path -LiteralPath $targetParent)) {
    throw "Deployment target parent does not exist: '$targetParent'."
}
if (-not (Test-Path -LiteralPath $targetPath)) {
    New-Item -ItemType Directory -Path $targetPath | Out-Null
}
Get-ChildItem -LiteralPath $source -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $targetPath $_.Name) -Force
}
Write-Host "Deployed Squire to '$targetPath'."
