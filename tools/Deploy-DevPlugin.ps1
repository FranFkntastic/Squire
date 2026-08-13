[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $Target,
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',
    [string] $FranthropyRoot,
    [string] $CraftArchitectRoot,
    [switch] $SkipBuild
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\Squire\Squire.csproj"
$source = Join-Path $repoRoot "src\Squire\bin\$Configuration"
$targetPath = [System.IO.Path]::GetFullPath($Target)
if (-not $SkipBuild) {
    $arguments = @('build', $project, '-c', $Configuration)
    if (-not [string]::IsNullOrWhiteSpace($FranthropyRoot)) {
        $resolvedFranthropy = [System.IO.Path]::GetFullPath($FranthropyRoot)
        $arguments += "-p:FranthropyDalamudProject=$(Join-Path $resolvedFranthropy 'src\Franthropy.Dalamud\Franthropy.Dalamud.csproj')"
    }
    if (-not [string]::IsNullOrWhiteSpace($CraftArchitectRoot)) {
        $resolvedCraftArchitect = [System.IO.Path]::GetFullPath($CraftArchitectRoot)
        $arguments += "-p:CraftArchitectCoreProject=$(Join-Path $resolvedCraftArchitect 'src\FFXIV Craft Architect.Core\FFXIV Craft Architect.Core.csproj')"
    }
    dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Squire $Configuration build failed with exit code $LASTEXITCODE." }
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
$sourceHash = (Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash
$targetDll = Join-Path $targetPath 'Squire.dll'
$targetHash = (Get-FileHash -LiteralPath $targetDll -Algorithm SHA256).Hash
if ($sourceHash -ne $targetHash) { throw 'Squire target hash does not match the built artifact.' }
[pscustomobject]@{
    Product = 'Squire'
    Configuration = $Configuration
    Branch = (& git -C $repoRoot branch --show-current).Trim()
    Commit = (& git -C $repoRoot rev-parse HEAD).Trim()
    TargetDll = $targetDll
    SourceSha256 = $sourceHash
    TargetSha256 = $targetHash
} | ConvertTo-Json -Depth 4
