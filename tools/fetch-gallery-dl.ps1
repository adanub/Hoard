# Fetches the official standalone gallery-dl build for the current OS into tools/gallery-dl/
# (gallery-dl.exe on Windows, gallery-dl elsewhere — the names Hoard.Desktop resolves at runtime).
# Source: https://github.com/gdl-org/builds (the binary distribution channel linked from
# gallery-dl's own installation docs). Re-run periodically — Pinterest changes break old builds.
$ErrorActionPreference = "Stop"

# $IsMacOS/$IsLinux are undefined (falsy) on Windows PowerShell 5.1, so it falls through to Windows.
if ($IsMacOS)      { $assetName = "gallery-dl_macos";       $fileName = "gallery-dl" }
elseif ($IsLinux)  { $assetName = "gallery-dl_linux";       $fileName = "gallery-dl" }
else               { $assetName = "gallery-dl_windows.exe"; $fileName = "gallery-dl.exe" }

$dest = Join-Path (Join-Path $PSScriptRoot "gallery-dl") $fileName
New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null

$release = Invoke-RestMethod "https://api.github.com/repos/gdl-org/builds/releases/latest" -Headers @{ "User-Agent" = "hoard" }
$asset = $release.assets | Where-Object name -eq $assetName
Write-Host "Downloading gallery-dl $($release.tag_name) ($assetName, $([math]::Round($asset.size/1MB,1)) MB)…"
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $dest -UseBasicParsing

if (-not ($IsWindows -or $env:OS -eq "Windows_NT")) { chmod +x $dest }

Write-Host "Saved to $dest"
Write-Host "SHA-256: $((Get-FileHash $dest -Algorithm SHA256).Hash)"
Write-Host "Verify it runs:  & '$dest' --version"
