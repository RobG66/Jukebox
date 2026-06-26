<#
.SYNOPSIS
    Downloads and verifies the native runtime libraries listed in
    natives.json, then extracts them into the lib/ folder.

.DESCRIPTION
    This script is the standard way to populate the lib/ folder with
    third-party native binaries (bass.dll, libmpv-2.dll, libprojectM.dll,
    glew32.dll, and their Linux .so equivalents). It:

      1. Reads natives.json (in the same directory as this script).
      2. Filters assets by the current platform (win-x64).
      3. For each asset:
         a. Skips it if the destination already contains files matching
            this asset's manifest version (idempotent — safe to re-run).
         b. Downloads the archive to a temporary location.
         c. Verifies the SHA-256 checksum against the manifest.
         d. Extracts the archive contents into lib/.
         e. Cleans up the temporary archive.
      4. Prints a summary of what was fetched vs. skipped vs. failed.

    No third-party binaries are committed to git history. The natives.json
    manifest pins the URL + SHA-256 of each binary, so the chain of trust
    is: this script -> URL in manifest -> checksum match -> lib/.

    To update a library, edit natives.json (bump URL + sha256) and re-run.

.PARAMETER Force
    Re-fetch all assets even if the destination appears up-to-date.

.PARAMETER Manifest
    Path to the natives.json manifest. Defaults to ./natives.json next
    to this script.

.EXAMPLE
    .\fetch-natives.ps1
    Downloads any missing native libraries into lib/.

.EXAMPLE
    .\fetch-natives.ps1 -Force
    Re-downloads all native libraries, overwriting what's in lib/.

.NOTES
    See THIRD_PARTY_LICENSES.md for the licensing of each fetched binary.
#>

[CmdletBinding()]
param(
    [switch]$Force,
    [string]$Manifest
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 3.0

# ── Resolve paths ────────────────────────────────────────────────────────
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Manifest) {
    $Manifest = Join-Path $scriptDir "natives.json"
}
if (-not (Test-Path $Manifest)) {
    Write-Host "ERROR: natives.json not found at: $Manifest" -ForegroundColor Red
    Write-Host "       fetch-natives.ps1 must live next to natives.json." -ForegroundColor Red
    exit 1
}

# Destination: lib/ next to the manifest.
$destRoot = Join-Path $scriptDir "lib"
if (-not (Test-Path $destRoot)) {
    New-Item -ItemType Directory -Force -Path $destRoot | Out-Null
}

# ── Detect platform ─────────────────────────────────────────────────────
# Currently only win-x64 and linux-x64 are supported. Add more cases here
# when the project gains macOS support.
$platform = if ($PSVersionTable.Platform -eq "Unix") {
    if ($IsMacOS) { "osx-arm64" } else { "linux-x64" }
} else {
    "win-x64"
}

Write-Host "fetch-natives" -ForegroundColor Green
Write-Host "=============" -ForegroundColor Green
Write-Host ""
Write-Host "  Manifest : $Manifest"
Write-Host "  Platform : $platform"
Write-Host "  Dest     : $destRoot"
Write-Host "  Force    : $Force"
Write-Host ""

# ── Parse manifest ──────────────────────────────────────────────────────
$manifestText = Get-Content -Raw -Path $Manifest
$json = $null
try {
    # ConvertFrom-Json with -AsHashtable doesn't exist in older PowerShell,
    # so we use the default PSCustomObject. The _comment fields are simply
    # ignored on read.
    $json = $manifestText | ConvertFrom-Json
} catch {
    Write-Host "ERROR: Failed to parse $Manifest as JSON: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

if (-not $json.assets -or $json.assets.Count -eq 0) {
    Write-Host "ERROR: No assets found in $Manifest" -ForegroundColor Red
    exit 1
}

$manifestVersion = $json.version
if (-not $manifestVersion) {
    Write-Host "WARNING: Manifest has no 'version' field. Idempotency check disabled." -ForegroundColor Yellow
    $manifestVersion = "unknown"
}

# Filter to assets for this platform.
$assetsForPlatform = @($json.assets | Where-Object { $_.platform -eq $platform })
if ($assetsForPlatform.Count -eq 0) {
    Write-Host "ERROR: No assets for platform '$platform' in $Manifest" -ForegroundColor Red
    Write-Host "       Available platforms:" -ForegroundColor Red
    $json.assets | ForEach-Object { Write-Host "         $($_.platform)" -ForegroundColor Red }
    exit 1
}

Write-Host "Found $($assetsForPlatform.Count) asset(s) for platform '$platform':" -ForegroundColor Cyan
foreach ($a in $assetsForPlatform) {
    Write-Host "  - $($a.name)" -ForegroundColor Gray
}
Write-Host ""

# ── Marker file for idempotency ─────────────────────────────────────────
# We write a .fetched file in lib/ recording the manifest version that was
# last fetched successfully. If the manifest version hasn't changed and
# Force isn't set, we skip the corresponding asset.
$markerFile = Join-Path $destRoot ".fetched-$platform-$manifestVersion"

# ── Download + verify + extract each asset ──────────────────────────────
$summary = [ordered]@{
    Fetched = @()
    Skipped = @()
    Failed  = @()
}

foreach ($asset in $assetsForPlatform) {
    $name = $asset.name
    $url = $asset.url
    $expectedSha = $asset.sha256
    $archiveType = $asset.archive_type
    $extractTo = $asset.extract_to
    if (-not $extractTo) { $extractTo = "lib/" }
    $extractToFull = Join-Path $scriptDir $extractTo
    if (-not (Test-Path $extractToFull)) {
        New-Item -ItemType Directory -Force -Path $extractToFull | Out-Null
    }

    Write-Host "[$($assetsForPlatform.IndexOf($asset) + 1)/$($assetsForPlatform.Count)] $name" -ForegroundColor Cyan

    # ── Idempotency check ──
    if (-not $Force -and (Test-Path $markerFile)) {
        $markerContent = Get-Content -Raw -Path $markerFile
        if ($markerContent -match [regex]::Escape($url)) {
            Write-Host "  Skipped (already fetched for manifest version $manifestVersion)" -ForegroundColor Gray
            $summary.Skipped += $name
            continue
        }
    }

    # ── Validate manifest fields ──
    if ($expectedSha -eq "REPLACE_WITH_ACTUAL_SHA256_OF_THE_ZIP" -or
        $expectedSha -eq "REPLACE_WITH_ACTUAL_SHA256_OF_THE_TAR_GZ") {
        Write-Host "  FAILED: Manifest still has a placeholder SHA-256 for this asset." -ForegroundColor Red
        Write-Host "          Edit $Manifest and replace '$expectedSha' with the actual checksum." -ForegroundColor Red
        $summary.Failed += $name
        continue
    }

    # ── Download ──
    $tempFile = [System.IO.Path]::GetTempFileName()
    # Preserve the archive extension so Expand-Archive / tar can recognize it.
    if ($archiveType -eq "zip") {
        $tempFile = "$tempFile.zip"
    } elseif ($archiveType -eq "tar.gz") {
        $tempFile = "$tempFile.tar.gz"
    }
    Write-Host "  Downloading: $url" -ForegroundColor Gray
    try {
        # Use TLS 1.2 (GitHub requires it).
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        # Follow redirects (GitHub release assets redirect to a CDN).
        Invoke-WebRequest -Uri $url -OutFile $tempFile -UseBasicParsing
    } catch {
        Write-Host "  FAILED: Download error: $($_.Exception.Message)" -ForegroundColor Red
        if (Test-Path $tempFile) { Remove-Item $tempFile -Force }
        $summary.Failed += $name
        continue
    }

    # ── Verify SHA-256 ──
    Write-Host "  Verifying SHA-256..." -ForegroundColor Gray
    $actualSha = (Get-FileHash -Path $tempFile -Algorithm SHA256).Hash.ToLower()
    if ($actualSha -ne $expectedSha.ToLower()) {
        Write-Host "  FAILED: SHA-256 mismatch!" -ForegroundColor Red
        Write-Host "          Expected: $expectedSha" -ForegroundColor Red
        Write-Host "          Actual:   $actualSha" -ForegroundColor Red
        Write-Host "          The downloaded file does not match the manifest. This could" -ForegroundColor Red
        Write-Host "          mean the upstream file changed, the manifest is out of date," -ForegroundColor Red
        Write-Host "          or the download was tampered with." -ForegroundColor Red
        if (Test-Path $tempFile) { Remove-Item $tempFile -Force }
        $summary.Failed += $name
        continue
    }
    Write-Host "  SHA-256 verified: $actualSha" -ForegroundColor Gray

    # ── Extract ──
    Write-Host "  Extracting to: $extractToFull" -ForegroundColor Gray
    try {
        if ($archiveType -eq "zip") {
            Expand-Archive -Path $tempFile -DestinationPath $extractToFull -Force
        } elseif ($archiveType -eq "tar.gz") {
            # tar is available on Windows 10+ (bsdtar) and Linux/macOS.
            & tar -xzf $tempFile -C $extractToFull
            if ($LASTEXITCODE -ne 0) {
                throw "tar exited with code $LASTEXITCODE"
            }
        } else {
            throw "Unknown archive_type: '$archiveType' (expected 'zip' or 'tar.gz')"
        }
    } catch {
        Write-Host "  FAILED: Extraction error: $($_.Exception.Message)" -ForegroundColor Red
        if (Test-Path $tempFile) { Remove-Item $tempFile -Force }
        $summary.Failed += $name
        continue
    }

    # ── Cleanup temp ──
    Remove-Item $tempFile -Force

    # ── Record in marker ──
    "$url`n$expectedSha`n$name" | Out-File -FilePath $markerFile -Append -Encoding utf8

    Write-Host "  OK" -ForegroundColor Green
    $summary.Fetched += $name
}

Write-Host ""

# ── Summary ─────────────────────────────────────────────────────────────
Write-Host "============================================================" -ForegroundColor Green
Write-Host "Summary" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
Write-Host ""
Write-Host "  Fetched ($($summary.Fetched.Count)):" -ForegroundColor Green
if ($summary.Fetched.Count -eq 0) {
    Write-Host "    (none)" -ForegroundColor Gray
} else {
    $summary.Fetched | ForEach-Object { Write-Host "    - $_" -ForegroundColor Green }
}
Write-Host ""
Write-Host "  Skipped ($($summary.Skipped.Count)):" -ForegroundColor Gray
if ($summary.Skipped.Count -eq 0) {
    Write-Host "    (none)" -ForegroundColor Gray
} else {
    $summary.Skipped | ForEach-Object { Write-Host "    - $_" -ForegroundColor Gray }
}
Write-Host ""
Write-Host "  Failed ($($summary.Failed.Count)):" -ForegroundColor Red
if ($summary.Failed.Count -eq 0) {
    Write-Host "    (none)" -ForegroundColor Gray
} else {
    $summary.Failed | ForEach-Object { Write-Host "    - $_" -ForegroundColor Red }
}
Write-Host ""

if ($summary.Failed.Count -gt 0) {
    Write-Host "Some assets failed to fetch. See messages above." -ForegroundColor Red
    exit 1
}

Write-Host "Done. Native libraries are in: $destRoot" -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  - Review THIRD_PARTY_LICENSES.md for licensing obligations." -ForegroundColor Yellow
Write-Host "  - Run the build script (build.ps1) to produce the drop-in zip." -ForegroundColor Yellow
Write-Host ""

exit 0
