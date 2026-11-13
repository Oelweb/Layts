<#
  Creates a new layts release:
    1. sets the version in Layts.csproj
    2. builds the app (dotnet publish)
    3. packs the installer (Layts-win-Setup.exe) + update packages with Velopack
    4. uploads everything as a GitHub release (only with -Upload)

  Examples:
    .\release.ps1 -Version 1.2.0              # build locally only (folder .\releases)
    .\release.ps1 -Version 1.2.0 -Upload      # build and publish on GitHub

  Requirements: .NET 10 SDK, Velopack tool (dotnet tool install -g vpk),
  for -Upload a GitHub login in Git (Git Credential Manager).
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [switch]$Upload
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$repo = 'https://github.com/Oelweb/Layts'
Set-Location $root

# Find .NET (also works for per-user installs)
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = (Get-Command dotnet).Source }
$env:DOTNET_ROOT = Split-Path $dotnet
$vpk = Join-Path $env:USERPROFILE '.dotnet\tools\vpk.exe'
if (-not (Test-Path $vpk)) { throw 'vpk is missing. Install it with: dotnet tool install -g vpk' }

# 1. Set version
$proj = Join-Path $root 'Layts.csproj'
$xml = [System.IO.File]::ReadAllText($proj)
$xml = [regex]::Replace($xml, '<Version>.*?</Version>', "<Version>$Version</Version>")
[System.IO.File]::WriteAllText($proj, $xml, (New-Object System.Text.UTF8Encoding $false))
Write-Host "Version set to $Version." -ForegroundColor Cyan

# 2. Build (separate folder so a running Layts.exe doesn't get in the way)
$out = Join-Path $root 'build\app'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
& $dotnet publish -c Release -o $out
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

# Get the GitHub token from the Git login (never printed)
$token = $null
if ($Upload) {
    # Pass the request to Git as a file (LF only, no BOM) via cmd redirection -
    # Windows PowerShell 5 would otherwise prepend a byte order mark when piping.
    $req = Join-Path $env:TEMP "layts-cred-$PID.txt"
    [System.IO.File]::WriteAllText($req, "protocol=https`nhost=github.com`n`n", (New-Object System.Text.UTF8Encoding $false))
    try { $cred = cmd /c "git credential fill < `"$req`"" }
    finally { Remove-Item $req -Force -ErrorAction SilentlyContinue }
    $token = [string](($cred | Where-Object { $_ -like 'password=*' } | Select-Object -First 1) -replace '^password=', '')
    $token = $token.Trim()
    if (-not $token) { throw 'No GitHub login found.' }

    # Fetch the previous release so Velopack can create small delta updates
    & $vpk download github --repoUrl $repo --token $token --outputDir releases
}

# 3. Installer + update packages
& $vpk pack --packId Layts --packVersion $Version --packDir $out --mainExe Layts.exe `
    --packTitle layts --packAuthors Oelweb --icon Assets\layts.ico --outputDir releases
if ($LASTEXITCODE -ne 0) { throw 'Packing failed.' }

# 4. Upload
if ($Upload) {
    & $vpk upload github --repoUrl $repo --token $token --publish `
        --releaseName "layts v$Version" --tag "v$Version" --outputDir releases
    if ($LASTEXITCODE -ne 0) { throw 'Upload failed.' }
    Write-Host "Release v$Version is live: $repo/releases/tag/v$Version" -ForegroundColor Green
} else {
    Write-Host 'Done. Installer is in .\releases (not uploaded).' -ForegroundColor Green
}
