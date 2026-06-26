#!/usr/bin/env bash
#
# fetch-natives.sh — bash equivalent of fetch-natives.ps1
#
# Downloads and verifies the native runtime libraries listed in
# natives.json, then extracts them into the lib/ folder.
#
# This script is the standard way to populate the lib/ folder with
# third-party native binaries (bass.dll, libmpv-2.dll, libprojectM.dll,
# glew32.dll, and their Linux .so equivalents). It:
#
#   1. Reads natives.json (in the same directory as this script).
#   2. Filters assets by the current platform (linux-x64 or osx-arm64).
#   3. For each asset:
#      a. Skips it if the destination already contains files matching
#         this asset's manifest version (idempotent — safe to re-run).
#      b. Downloads the archive to a temporary location.
#      c. Verifies the SHA-256 checksum against the manifest.
#      d. Extracts the archive contents into lib/.
#      e. Cleans up the temporary archive.
#   4. Prints a summary of what was fetched vs. skipped vs. failed.
#
# No third-party binaries are committed to git history. The natives.json
# manifest pins the URL + SHA-256 of each binary, so the chain of trust
# is: this script -> URL in manifest -> checksum match -> lib/.
#
# To update a library, edit natives.json (bump URL + sha256) and re-run.
#
# Usage:
#   ./fetch-natives.sh              # download any missing libraries
#   ./fetch-natives.sh --force      # re-download all libraries
#   ./fetch-natives.sh --manifest /path/to/natives.json
#
# Requires: curl, sha256sum, tar, unzip (for zip archives on Linux),
#           python3 (used to parse JSON — available on most systems).
#
# See THIRD_PARTY_LICENSES.md for the licensing of each fetched binary.
#
set -euo pipefail

# ── Resolve paths ────────────────────────────────────────────────────────
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
manifest="${manifest:-$script_dir/natives.json}"
force=false

# Parse args
while [[ $# -gt 0 ]]; do
    case "$1" in
        --force)
            force=true
            shift
            ;;
        --manifest)
            manifest="$2"
            shift 2
            ;;
        --help|-h)
            sed -n '2,30p' "${BASH_SOURCE[0]}"
            exit 0
            ;;
        *)
            echo "ERROR: Unknown argument: $1" >&2
            exit 1
            ;;
    esac
done

if [[ ! -f "$manifest" ]]; then
    echo "ERROR: natives.json not found at: $manifest" >&2
    echo "       fetch-natives.sh must live next to natives.json." >&2
    exit 1
fi

# Destination: lib/ next to the manifest.
dest_root="$script_dir/lib"
mkdir -p "$dest_root"

# ── Detect platform ─────────────────────────────────────────────────────
# Currently only win-x64 and linux-x64 are supported. Add more cases here
# when the project gains macOS support.
os="$(uname -s)"
arch="$(uname -m)"
if [[ "$os" == "Linux" ]]; then
    platform="linux-x64"
elif [[ "$os" == "Darwin" ]]; then
    if [[ "$arch" == "arm64" ]]; then
        platform="osx-arm64"
    else
        platform="osx-x64"
    fi
else
    echo "ERROR: Unsupported OS: $os" >&2
    exit 1
fi

echo "fetch-natives"
echo "============="
echo ""
echo "  Manifest : $manifest"
echo "  Platform : $platform"
echo "  Dest     : $dest_root"
echo "  Force    : $force"
echo ""

# ── Parse manifest with python3 ─────────────────────────────────────────
# JSON parsing in pure bash is fragile; python3 is available on every
# Linux distro and macOS (with Xcode command line tools). We use a small
# python script to extract the fields we need.
if ! command -v python3 >/dev/null 2>&1; then
    echo "ERROR: python3 is required to parse natives.json but was not found." >&2
    exit 1
fi

# Emit one line per matching asset, tab-separated:
# name<TAB>url<TAB>sha256<TAB>archive_type<TAB>extract_to
read_assets() {
    python3 - "$manifest" "$platform" <<'PY'
import json, sys
manifest_path, platform = sys.argv[1], sys.argv[2]
with open(manifest_path) as f:
    data = json.load(f)
for a in data.get("assets", []):
    if a.get("platform") == platform:
        print("\t".join([
            a.get("name", ""),
            a.get("url", ""),
            a.get("sha256", ""),
            a.get("archive_type", ""),
            a.get("extract_to", "lib/"),
        ]))
PY
}

manifest_version=$(python3 - "$manifest" <<'PY'
import json, sys
with open(sys.argv[1]) as f:
    data = json.load(f)
print(data.get("version", "unknown"))
PY
)

assets_output="$(read_assets)"
if [[ -z "$assets_output" ]]; then
    echo "ERROR: No assets for platform '$platform' in $manifest" >&2
    exit 1
fi

# Count assets
asset_count=$(echo "$assets_output" | wc -l | tr -d ' ')
echo "Found $asset_count asset(s) for platform '$platform':"
echo "$assets_output" | while IFS=$'\t' read -r name _ _ _ _; do
    echo "  - $name"
done
echo ""

# Marker file for idempotency
marker_file="$dest_root/.fetched-$platform-$manifest_version"

# ── Check for required tools ────────────────────────────────────────────
for tool in curl sha256sum tar; do
    if ! command -v "$tool" >/dev/null 2>&1; then
        echo "ERROR: Required tool '$tool' not found in PATH." >&2
        exit 1
    fi
done

# ── Download + verify + extract each asset ──────────────────────────────
fetched=()
skipped=()
failed=()

i=0
while IFS=$'\t' read -r name url expected_sha archive_type extract_to; do
    i=$((i + 1))
    if [[ -z "$extract_to" ]]; then extract_to="lib/"; fi
    extract_to_full="$script_dir/$extract_to"
    mkdir -p "$extract_to_full"

    echo "[$i/$asset_count] $name"

    # ── Idempotency check ──
    if [[ "$force" != "true" && -f "$marker_file" ]]; then
        if grep -qF "$url" "$marker_file" 2>/dev/null; then
            echo "  Skipped (already fetched for manifest version $manifest_version)"
            skipped+=("$name")
            continue
        fi
    fi

    # ── Validate manifest fields ──
    if [[ "$expected_sha" == "REPLACE_WITH_ACTUAL_SHA256_OF_THE_ZIP" ||
          "$expected_sha" == "REPLACE_WITH_ACTUAL_SHA256_OF_THE_TAR_GZ" ]]; then
        echo "  FAILED: Manifest still has a placeholder SHA-256 for this asset." >&2
        echo "          Edit $manifest and replace '$expected_sha' with the actual checksum." >&2
        failed+=("$name")
        continue
    fi

    # ── Download ──
    temp_file="$(mktemp)"
    case "$archive_type" in
        zip)   temp_file="${temp_file}.zip" ;;
        tar.gz) temp_file="${temp_file}.tar.gz" ;;
    esac
    echo "  Downloading: $url"
    if ! curl -fL --connect-timeout 30 --max-time 600 -o "$temp_file" "$url"; then
        echo "  FAILED: Download error (curl exited $?)"
        rm -f "$temp_file"
        failed+=("$name")
        continue
    fi

    # ── Verify SHA-256 ──
    echo "  Verifying SHA-256..."
    actual_sha="$(sha256sum "$temp_file" | awk '{print $1}' | tr 'A-Z' 'a-z')"
    if [[ "$actual_sha" != "${expected_sha,,}" ]]; then
        echo "  FAILED: SHA-256 mismatch!"
        echo "          Expected: $expected_sha"
        echo "          Actual:   $actual_sha"
        echo "          The downloaded file does not match the manifest. This could"
        echo "          mean the upstream file changed, the manifest is out of date,"
        echo "          or the download was tampered with."
        rm -f "$temp_file"
        failed+=("$name")
        continue
    fi
    echo "  SHA-256 verified: $actual_sha"

    # ── Extract ──
    echo "  Extracting to: $extract_to_full"
    case "$archive_type" in
        zip)
            if ! command -v unzip >/dev/null 2>&1; then
                echo "  FAILED: 'unzip' is required to extract zip archives but was not found." >&2
                rm -f "$temp_file"
                failed+=("$name")
                continue
            fi
            if ! unzip -o -q "$temp_file" -d "$extract_to_full"; then
                echo "  FAILED: unzip exited $?"
                rm -f "$temp_file"
                failed+=("$name")
                continue
            fi
            ;;
        tar.gz)
            if ! tar -xzf "$temp_file" -C "$extract_to_full"; then
                echo "  FAILED: tar exited $?"
                rm -f "$temp_file"
                failed+=("$name")
                continue
            fi
            ;;
        *)
            echo "  FAILED: Unknown archive_type: '$archive_type' (expected 'zip' or 'tar.gz')"
            rm -f "$temp_file"
            failed+=("$name")
            continue
            ;;
    esac

    # ── Cleanup temp ──
    rm -f "$temp_file"

    # ── Record in marker ──
    printf '%s\t%s\t%s\n' "$url" "$expected_sha" "$name" >> "$marker_file"

    echo "  OK"
    fetched+=("$name")
done <<< "$assets_output"

echo ""

# ── Summary ─────────────────────────────────────────────────────────────
echo "============================================================"
echo "Summary"
echo "============================================================"
echo ""
echo "  Fetched (${#fetched[@]}):"
if [[ ${#fetched[@]} -eq 0 ]]; then
    echo "    (none)"
else
    for n in "${fetched[@]}"; do echo "    - $n"; done
fi
echo ""
echo "  Skipped (${#skipped[@]}):"
if [[ ${#skipped[@]} -eq 0 ]]; then
    echo "    (none)"
else
    for n in "${skipped[@]}"; do echo "    - $n"; done
fi
echo ""
echo "  Failed (${#failed[@]}):"
if [[ ${#failed[@]} -eq 0 ]]; then
    echo "    (none)"
else
    for n in "${failed[@]}"; do echo "    - $n"; done
fi
echo ""

if [[ ${#failed[@]} -gt 0 ]]; then
    echo "Some assets failed to fetch. See messages above." >&2
    exit 1
fi

echo "Done. Native libraries are in: $dest_root"
echo ""
echo "Next steps:"
echo "  - Review THIRD_PARTY_LICENSES.md for licensing obligations."
echo "  - Run the build script (build.sh) to produce the drop-in zip."
echo ""

exit 0
