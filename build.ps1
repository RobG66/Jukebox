Set-Location $PSScriptRoot

Write-Host "Building Jukebox - Windows x64 & Linux x64" -ForegroundColor Green
Write-Host "=====================================================" -ForegroundColor Green
Write-Host ""

# Clean previous builds
Write-Host "Cleaning previous builds..." -ForegroundColor Yellow
dotnet clean "Jukebox.csproj" --configuration Release
foreach ($dir in @("./publish/win-x64", "./publish/win-x64-lite", "./publish/linux-x64")) {
    if (Test-Path $dir) {
        Remove-Item -Recurse -Force $dir
    }
}
Write-Host ""

# --- Windows x64 ---
Write-Host "Building Windows x64..." -ForegroundColor Cyan
Write-Host "-----------------------" -ForegroundColor Cyan

dotnet publish "Jukebox.csproj" `
    --configuration Release `
    --runtime win-x64 `
    --self-contained `
    --output "./publish/win-x64" `
    -p:PublishSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false

$winSuccess = ($LASTEXITCODE -eq 0)
Write-Host ""

# --- Windows x64 (framework-dependent, requires .NET 10 installed) ---
Write-Host "Building Windows x64 (framework-dependent)..." -ForegroundColor Cyan
Write-Host "----------------------------------------------" -ForegroundColor Cyan

dotnet publish "Jukebox.csproj" `
    --configuration Release `
    --runtime win-x64 `
    --self-contained false `
    --output "./publish/win-x64-lite" `
    -p:PublishSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false

$winLiteSuccess = ($LASTEXITCODE -eq 0)
Write-Host ""

# --- Linux x64 ---
Write-Host "Building Linux x64..." -ForegroundColor Cyan
Write-Host "---------------------" -ForegroundColor Cyan
Write-Host ""

dotnet publish "Jukebox.csproj" `
    --configuration Release `
    --runtime linux-x64 `
    --self-contained `
    --output "./publish/linux-x64" `
    -p:PublishSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false

$linuxSuccess = ($LASTEXITCODE -eq 0)
Write-Host ""

# --- Summary ---
Write-Host "=====================================================" -ForegroundColor Green
Write-Host "Build Summary" -ForegroundColor Green
Write-Host "=====================================================" -ForegroundColor Green

if ($winSuccess) {
    Write-Host "  Windows x64 : OK -> ./publish/win-x64/Jukebox.exe" -ForegroundColor Green
    $exePath = "./publish/win-x64/Jukebox.exe"
    if (Test-Path $exePath) {
        $size = (Get-Item $exePath).Length / 1MB
        Write-Host ("  Size        : {0:N2} MB" -f $size) -ForegroundColor Cyan
    }
} else {
    Write-Host "  Windows x64 : FAILED" -ForegroundColor Red
}

Write-Host ""

if ($winLiteSuccess) {
    Write-Host "  Win x64 Lite: OK -> ./publish/win-x64-lite/Jukebox.exe" -ForegroundColor Green
    $exePath = "./publish/win-x64-lite/Jukebox.exe"
    if (Test-Path $exePath) {
        $size = (Get-Item $exePath).Length / 1MB
        Write-Host ("  Size        : {0:N2} MB" -f $size) -ForegroundColor Cyan
    }
    Write-Host "  NOTE        : Requires .NET 10 runtime on target system." -ForegroundColor Yellow
} else {
    Write-Host "  Win x64 Lite: FAILED" -ForegroundColor Red
}

Write-Host ""

if ($linuxSuccess) {
    Write-Host "  Linux x64   : OK -> ./publish/linux-x64/Jukebox" -ForegroundColor Green
    $binPath = "./publish/linux-x64/Jukebox"
    if (Test-Path $binPath) {
        $size = (Get-Item $binPath).Length / 1MB
        Write-Host ("  Size        : {0:N2} MB" -f $size) -ForegroundColor Cyan
    }
} else {
    Write-Host "  Linux x64   : FAILED" -ForegroundColor Red
}

Write-Host ""

if ($winSuccess -and $winLiteSuccess -and $linuxSuccess) {
    exit 0
} else {
    exit 1
}
