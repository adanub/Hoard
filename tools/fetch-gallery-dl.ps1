# Fetches the official standalone gallery-dl build for the current OS into tools/gallery-dl/
# (gallery-dl.exe on Windows, gallery-dl elsewhere — the names Hoard.Desktop resolves at runtime).
# Source: https://github.com/gdl-org/builds (the binary distribution channel linked from
# gallery-dl's own installation docs).
#
# You don't normally need to run this: building Hoard.Desktop fetches the binary when it's missing
# (see the FetchGalleryDl target in src/Hoard.Desktop/Hoard.Desktop.csproj). Run it to FORCE a refresh —
# Pinterest changes break old gallery-dl builds, and the build step won't replace a file that's already
# there — or to prepare a publish (the release workflow calls this before packaging).
#
# -Tag pins the download to one gdl-org/builds release (e.g. -Tag 2026.08.11) instead of "latest".
# The release workflow resolves latest ONCE per run and passes it here, so the Windows and macOS legs
# of one release can't straddle an upstream release and ship two different builds. Omitted = latest.
param([string]$Tag)

$ErrorActionPreference = "Stop"

# $IsMacOS/$IsLinux are undefined (falsy) on Windows PowerShell 5.1, so it falls through to Windows.
if ($IsMacOS)      { $assetName = "gallery-dl_macos";        $fileName = "gallery-dl" }
elseif ($IsLinux)  { $assetName = "gallery-dl_linux";        $fileName = "gallery-dl" }
else               { $assetName = "gallery-dl_windows.exe";  $fileName = "gallery-dl.exe" }

$dest = Join-Path (Join-Path $PSScriptRoot "gallery-dl") $fileName
New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null

# GitHub's /releases/latest/download/ redirect always points at the newest release's asset, so this needs
# no API call — the unauthenticated API is capped at 60 requests/hour and fails the whole fetch when a
# shared runner has spent them. The MSBuild target uses this same URL.
$url = if ($Tag) { "https://github.com/gdl-org/builds/releases/download/$Tag/$assetName" }
       else      { "https://github.com/gdl-org/builds/releases/latest/download/$assetName" }
Write-Host "Downloading $assetName from $url …"

# Download to a temp name and rename only on success: an interrupted fetch must not leave a truncated
# binary sitting at the real path, where the build's "does it exist" check would keep it forever and the
# only symptom would be gallery-dl crashing on someone's first import.
$staged = "$dest.tmp"
Invoke-WebRequest -Uri $url -OutFile $staged -UseBasicParsing
Move-Item -Path $staged -Destination $dest -Force

if (-not ($IsWindows -or $env:OS -eq "Windows_NT")) { chmod +x $dest }

Write-Host "Saved to $dest ($([math]::Round((Get-Item $dest).Length / 1MB, 1)) MB)"
Write-Host "SHA-256: $((Get-FileHash $dest -Algorithm SHA256).Hash)"
Write-Host "Verify it runs:  & '$dest' --version"
