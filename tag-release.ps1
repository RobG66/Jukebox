Set-Location $PSScriptRoot

# Resolve gh CLI — not always in VS terminal PATH
$gh = Get-Command gh -ErrorAction SilentlyContinue |
      Select-Object -ExpandProperty Source
if (-not $gh) {
    $gh = "C:\Program Files\GitHub CLI\gh.exe"
    if (-not (Test-Path $gh)) { $gh = $null }
}

# --- Read version from csproj ---
$csproj  = "Jukebox.csproj"
$xml     = [xml](Get-Content $csproj)
$version = $xml.Project.PropertyGroup |
    ForEach-Object { $_.Version } |
    Where-Object   { $_ } |
    Select-Object  -First 1

if (-not $version) {
    Write-Host "ERROR: Could not read <Version> from $csproj" -ForegroundColor Red
    exit 1
}

$version = $version -replace '\+.*$', ''
$tag     = "v$version"

Write-Host "Version : $version" -ForegroundColor Cyan
Write-Host "Tag     : $tag"     -ForegroundColor Cyan
Write-Host ""

# --- Tag ---
$localTag  = git tag -l $tag
$remoteTag = git ls-remote --tags origin "refs/tags/$tag" 2>$null

if ($localTag -or $remoteTag) {
    Write-Host "Tag $tag already exists." -ForegroundColor Yellow
    $answer = Read-Host "Delete the existing tag and release and redo? (y/N)"
    if ($answer -notmatch '^[Yy]$') {
        Write-Host "Aborted." -ForegroundColor Gray
        exit 0
    }

    if ($localTag) {
        git tag -d $tag | Out-Null
        Write-Host "  Deleted local tag $tag" -ForegroundColor Gray
    }

    if ($remoteTag) {
        git push origin --delete $tag | Out-Null
        Write-Host "  Deleted remote tag $tag" -ForegroundColor Gray
    }

    if ($gh) {
        & $gh release view $tag 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) {
            & $gh release delete $tag --yes 2>$null
            Write-Host "  Deleted GitHub release $tag" -ForegroundColor Gray
        }
    }

    Write-Host ""
}

git tag $tag
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: git tag failed." -ForegroundColor Red
    exit 1
}

git push origin $tag
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: git push failed." -ForegroundColor Red
    git tag -d $tag
    exit 1
}

Write-Host "Tag pushed. GitHub Actions will create the draft release." -ForegroundColor Gray
Write-Host ""

# --- Build ---
& "$PSScriptRoot\build.ps1"
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Build failed - release artifacts not uploaded." -ForegroundColor Red
    exit 1
}

# --- Package ---
Write-Host "Packaging..." -ForegroundColor Yellow

$winZip     = "Jukebox-$tag-win-x64-with-net10.zip"
$winLiteZip = "Jukebox-$tag-win-x64-no-net10.zip"
$linuxTar   = "Jukebox-$tag-linux-x64.tar.gz"

Compress-Archive -Path "./publish/win-x64/*"      -DestinationPath $winZip     -Force
Compress-Archive -Path "./publish/win-x64-lite/*" -DestinationPath $winLiteZip -Force

$winAbsPath = (Resolve-Path "./publish/linux-x64").Path
$winTarPath = [System.IO.Path]::GetFullPath($linuxTar)   # <-- absolute path

$cmd = @"
chmod +x "`$(wslpath '$winAbsPath')/Jukebox" && tar -czf "`$(wslpath '$winTarPath')" -C "`$(wslpath '$winAbsPath')" .
"@
wsl bash -c $cmd

Write-Host "  $winZip" -ForegroundColor Green
Write-Host "  $winLiteZip (requires .NET 10)" -ForegroundColor Green
Write-Host "  $linuxTar" -ForegroundColor Green
Write-Host ""

# --- Upload ---
if ($gh) {
    Write-Host "Uploading to GitHub release $tag..." -ForegroundColor Yellow
    & $gh release upload $tag $winZip $winLiteZip $linuxTar --clobber
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Done. Publish the draft at:" -ForegroundColor Green
        Write-Host "  https://github.com/RobG66/Jukebox/releases" -ForegroundColor Cyan
    } else {
        Write-Host "Upload failed - attach the files manually on GitHub." -ForegroundColor Red
    }
} else {
    Write-Host "gh CLI not found - attach these files manually to the GitHub draft release:" -ForegroundColor Yellow
    Write-Host "  $winZip" -ForegroundColor Cyan
    Write-Host "  $winLiteZip" -ForegroundColor Cyan
    Write-Host "  $linuxTar" -ForegroundColor Cyan
}
