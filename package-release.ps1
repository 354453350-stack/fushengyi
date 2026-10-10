param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$GitHubOwner,

    [Parameter(Mandatory = $true)]
    [string]$GitHubRepository
)

$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw 'Version must use MAJOR.MINOR.PATCH, such as 1.0.0.'
}
foreach ($name in @($GitHubOwner, $GitHubRepository)) {
    if ($name -notmatch '^[A-Za-z0-9_.-]+$') {
        throw "Invalid GitHub owner or repository name: $name"
    }
}

$projectRoot = $PSScriptRoot
$compilerCandidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$compiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $compiler) { throw 'The built-in .NET Framework C# compiler was not found.' }

$distRoot = Join-Path $projectRoot 'dist'
$releaseRoot = Join-Path $distRoot "release-v$Version"
$stage = Join-Path $releaseRoot 'package'
if (Test-Path -LiteralPath $releaseRoot) {
    throw "Output already exists: $releaseRoot. Move it aside before rebuilding this version."
}

New-Item -ItemType Directory -Path (Join-Path $stage 'images') -Force | Out-Null
$exePath = Join-Path $stage '浮生弈.exe'
$resourceArgument = '/resource:' + (Join-Path $projectRoot 'index.html') + ',Page.html'
$compilerArgs = @(
    '/nologo',
    '/target:winexe',
    '/optimize+',
    "/out:$exePath",
    '/reference:System.Windows.Forms.dll',
    '/reference:System.Web.Extensions.dll',
    '/reference:System.IO.Compression.dll',
    '/reference:System.IO.Compression.FileSystem.dll',
    $resourceArgument,
    (Join-Path $projectRoot 'Launcher.cs')
)
& $compiler @compilerArgs
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $exePath)) {
    throw 'Failed to compile 浮生弈.exe.'
}

Get-ChildItem -LiteralPath (Join-Path $projectRoot 'images') -File | Copy-Item -Destination (Join-Path $stage 'images') -Force
$imageCount = @(Get-ChildItem -LiteralPath (Join-Path $stage 'images') -File | Where-Object { $_.Extension -match '^\.(png|jpe?g|gif|webp|bmp)$' }).Count
if ($imageCount -eq 0) {
    throw 'No supported images were found in the images folder.'
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $stage
Set-Content -LiteralPath (Join-Path $stage 'version.txt') -Value $Version -Encoding utf8NoBOM
$updateConfig = [ordered]@{
    owner = $GitHubOwner
    repository = $GitHubRepository
    assetName = 'fushengyi-win-x64.zip'
}
$updateConfig | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'update-config.json') -Encoding utf8NoBOM

$archivePath = Join-Path $releaseRoot 'fushengyi-win-x64.zip'
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archivePath -CompressionLevel Optimal
$archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath ($archivePath + '.sha256') -Value "$archiveHash  fushengyi-win-x64.zip" -Encoding ascii

Write-Output "Release package: $archivePath"
Write-Output "SHA-256: $archiveHash"
