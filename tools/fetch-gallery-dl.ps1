# Fetches the official standalone gallery-dl Windows build into tools/gallery-dl/gallery-dl.exe.
# Source: https://github.com/gdl-org/builds (the binary distribution channel linked from
# gallery-dl's own installation docs). Re-run periodically — Pinterest changes break old builds.
$ErrorActionPreference = "Stop"
$dest = Join-Path $PSScriptRoot "gallery-dl\gallery-dl.exe"
New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null

$release = Invoke-RestMethod "https://api.github.com/repos/gdl-org/builds/releases/latest" -Headers @{ "User-Agent" = "hoard" }
$asset = $release.assets | Where-Object name -eq "gallery-dl_windows.exe"
Write-Host "Downloading gallery-dl $($release.tag_name) ($([math]::Round($asset.size/1MB,1)) MB)…"
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $dest -UseBasicParsing

Write-Host "Saved to $dest"
Write-Host "SHA-256: $((Get-FileHash $dest -Algorithm SHA256).Hash)"
Write-Host "Verify it runs:  & '$dest' --version"
