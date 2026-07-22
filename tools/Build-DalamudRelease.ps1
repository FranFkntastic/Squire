[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",
    [string] $PackageUrl = "",
    [string] $OutputDirectory = "",
    [switch] $SkipBuild
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectDir = Join-Path $repoRoot "src\Squire"
$projectPath = Join-Path $projectDir "Squire.csproj"
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "dist"
}
$outputDirectoryFull = [System.IO.Path]::GetFullPath($OutputDirectory)
$repoRootFull = [System.IO.Path]::GetFullPath($repoRoot)
if (-not $outputDirectoryFull.StartsWith($repoRootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must be inside the repository: $repoRootFull"
}

$buildOutput = Join-Path $projectDir "bin\$Configuration"
$packageStaging = Join-Path $outputDirectoryFull "package"
$zipPath = Join-Path $outputDirectoryFull "latest.zip"
$repoJsonPath = Join-Path $outputDirectoryFull "repo.json"
if (-not $SkipBuild) {
    dotnet build $projectPath -c $Configuration -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$manifestPath = Join-Path $buildOutput "Squire.json"
if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "Expected manifest was not found: $manifestPath"
}
if (Test-Path -LiteralPath $outputDirectoryFull) {
    Remove-Item -LiteralPath $outputDirectoryFull -Recurse -Force
}
New-Item -ItemType Directory -Path $packageStaging | Out-Null

$packageFiles = @(
    "Squire.dll",
    "Squire.deps.json",
    "Squire.json",
    "ECommons.dll",
    "Franthropy.Dalamud.dll",
    "Franthropy.Filtering.dll",
    "System.Security.Cryptography.ProtectedData.dll"
)
foreach ($fileName in $packageFiles) {
    $sourcePath = Join-Path $buildOutput $fileName
    if (-not (Test-Path -LiteralPath $sourcePath)) {
        throw "Expected package file was not found: $sourcePath"
    }
    Copy-Item -LiteralPath $sourcePath -Destination $packageStaging
}
Compress-Archive -Path (Join-Path $packageStaging "*") -DestinationPath $zipPath -Force

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$releaseTag = "v$($manifest.AssemblyVersion)"
if ([string]::IsNullOrWhiteSpace($PackageUrl)) {
    $PackageUrl = "https://github.com/FranFkntastic/Squire/releases/download/$releaseTag/latest.zip"
}
$repoEntry = [ordered]@{
    Author = $manifest.Author
    Name = $manifest.Name
    InternalName = $manifest.InternalName
    AssemblyVersion = $manifest.AssemblyVersion
    TestingAssemblyVersion = $null
    Description = $manifest.Description
    ApplicableVersion = $manifest.ApplicableVersion
    RepoUrl = $manifest.RepoUrl
    DalamudApiLevel = $manifest.DalamudApiLevel
    Punchline = $manifest.Punchline
    Tags = $manifest.Tags
    CategoryTags = $manifest.CategoryTags
    IsHide = $false
    IsTestingExclusive = $false
    DownloadCount = 0
    DownloadLinkInstall = $PackageUrl
    DownloadLinkTesting = $PackageUrl
    DownloadLinkUpdate = $PackageUrl
    LastUpdate = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds().ToString()
}
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText($repoJsonPath, "[$($repoEntry | ConvertTo-Json -Depth 8)]", $utf8NoBom)
Write-Host "Built Squire package: $zipPath"
Write-Host "Built custom repository manifest: $repoJsonPath"

