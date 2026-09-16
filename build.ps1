param(
    [switch]$Debug
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root "src\Dahu\Dahu.csproj"
$dist = Join-Path $root "dist"

$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
$hasSdk = $false
if ($dotnet) { $hasSdk = (& $dotnet --list-sdks 2>$null | Where-Object { $_ -match '^10\.' }).Count -gt 0 }
if (-not $hasSdk) {
    $local = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
    if (Test-Path $local) { $dotnet = $local; $hasSdk = (& $dotnet --list-sdks 2>$null | Where-Object { $_ -match '^10\.' }).Count -gt 0 }
}
if (-not $hasSdk) {
    Write-Host "SDK .NET 10 absent : installation locale dans $env:USERPROFILE\.dotnet (sans droits admin)..." -ForegroundColor Yellow
    $script = Join-Path $env:TEMP "dotnet-install.ps1"
    Invoke-WebRequest -Uri "https://dot.net/v1/dotnet-install.ps1" -OutFile $script
    & $script -Channel 10.0 -InstallDir (Join-Path $env:USERPROFILE ".dotnet") -NoPath
    $dotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
}
Write-Host "dotnet : $dotnet" -ForegroundColor Cyan

if ($Debug) {
    & $dotnet build $proj -c Debug
    exit $LASTEXITCODE
}

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
& $dotnet publish $proj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $dist
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Get-ChildItem $dist -Exclude "Dahu.exe" | Remove-Item -Recurse -Force
$exe = Get-Item (Join-Path $dist "Dahu.exe")
Write-Host ("OK : {0} ({1:N1} Mo)" -f $exe.FullName, ($exe.Length / 1MB)) -ForegroundColor Green
