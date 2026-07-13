[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PiTarget
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PiTarget -notmatch '^[A-Za-z_][A-Za-z0-9._-]*@[A-Za-z0-9][A-Za-z0-9.-]*$') {
    throw "PiTarget must look like arenaadmin@192.168.1.50 or arenaadmin@lazertag-pi."
}

foreach ($command in 'dotnet', 'tar', 'scp', 'ssh') {
    if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
        throw "Required command '$command' was not found in PATH."
    }
}

$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $root 'LazerTagWebServer\LazerTagWebServer.csproj'
$outputRoot = Join-Path $root 'artifacts\vscode'
$publishDirectory = Join-Path $outputRoot 'publish'
$archive = Join-Path $outputRoot 'lazertag-linux-arm64.tar.gz'
$remoteArchive = '/tmp/lazertag-linux-arm64.tar.gz'

if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
    throw "Web server project not found: $project"
}

if (Test-Path -LiteralPath $publishDirectory) {
    $resolvedOutput = [System.IO.Path]::GetFullPath($publishDirectory)
    if (-not $resolvedOutput.StartsWith($outputRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to clean a publish directory outside the VS Code artifact directory.'
    }
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}

New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null

Write-Host 'Publishing the web server for Raspberry Pi (linux-arm64)...' -ForegroundColor Cyan
& dotnet publish $project `
    --configuration Release `
    --runtime linux-arm64 `
    --self-contained true `
    --output $publishDirectory
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

if (Test-Path -LiteralPath $archive) {
    Remove-Item -LiteralPath $archive -Force
}

Write-Host 'Creating the release archive...' -ForegroundColor Cyan
& tar --create --gzip --file $archive --directory $publishDirectory .
if ($LASTEXITCODE -ne 0) {
    throw "tar failed with exit code $LASTEXITCODE."
}

$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "Release SHA-256: $hash"

Write-Host "Uploading the release to $PiTarget..." -ForegroundColor Cyan
& scp $archive "${PiTarget}:${remoteArchive}"
if ($LASTEXITCODE -ne 0) {
    throw "scp failed with exit code $LASTEXITCODE."
}

Write-Host 'Activating the release with health-check and automatic rollback...' -ForegroundColor Cyan
& ssh -t $PiTarget "sudo /usr/local/sbin/lazertag-deploy '$remoteArchive'"
if ($LASTEXITCODE -ne 0) {
    throw "Remote deployment failed with exit code $LASTEXITCODE. The Pi deployer will retain or restore the previous healthy release."
}

Write-Host 'Deployment completed and the Pi health check passed.' -ForegroundColor Green

