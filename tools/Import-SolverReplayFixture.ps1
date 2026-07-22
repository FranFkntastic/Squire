[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePath,
    [string]$DestinationPath = (Join-Path $PSScriptRoot '..\tests\Squire.Tests\Fixtures\Squire\btn72-solver-replay-v1.json.gz')
)

$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath($SourcePath)
$destination = [IO.Path]::GetFullPath($DestinationPath)
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
    throw "Solver replay was not found: $source"
}

$json = [IO.File]::ReadAllText($source)
$document = $json | ConvertFrom-Json
if ($document.schemaVersion -ne 'marketmafioso-squire-min-btn-solver-replay/v1') {
    throw "Unexpected replay schema '$($document.schemaVersion)'."
}
if ($document.profile.classJobId -ne 17 -or $document.profile.characterLevel -ne 72 -or $document.requiredPositions.Count -ne 12) {
    throw 'The replay is not the rendered BTN 72 twelve-position acceptance request.'
}
foreach ($forbidden in @('Wei', 'Siren', 'Retainer', 'market:')) {
    if ($json.IndexOf($forbidden, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "The sanitized replay still contains forbidden live identity text '$forbidden'."
    }
}

$directory = Split-Path -Parent $destination
[IO.Directory]::CreateDirectory($directory) | Out-Null
$bytes = [Text.Encoding]::UTF8.GetBytes(($document | ConvertTo-Json -Depth 20 -Compress))
$file = [IO.File]::Create($destination)
try {
    $gzip = [IO.Compression.GZipStream]::new($file, [IO.Compression.CompressionMode]::Compress)
    try { $gzip.Write($bytes, 0, $bytes.Length) }
    finally { $gzip.Dispose() }
}
finally {
    $file.Dispose()
}

[pscustomobject]@{
    Destination = $destination
    Offers = $document.offers.Count
    UncompressedBytes = $bytes.Length
    CompressedBytes = (Get-Item -LiteralPath $destination).Length
    Sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
}
