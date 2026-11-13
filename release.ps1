<#
  Erstellt ein neues Release von layts:
    1. setzt die Version in Layts.csproj
    2. baut die App (dotnet publish)
    3. packt Installer (Layts-win-Setup.exe) + Update-Pakete mit Velopack
    4. lädt alles als GitHub-Release hoch (nur mit -Upload)

  Beispiele:
    .\release.ps1 -Version 1.2.0              # nur lokal bauen (Ordner .\releases)
    .\release.ps1 -Version 1.2.0 -Upload      # bauen und auf GitHub veröffentlichen

  Voraussetzungen: .NET 10 SDK, Velopack-Werkzeug (dotnet tool install -g vpk),
  bei -Upload eine GitHub-Anmeldung in Git (Git Credential Manager).
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [switch]$Upload
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$repo = 'https://github.com/Oelweb/Layts'
Set-Location $root

# .NET finden (auch bei Installation nur für den Benutzer)
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = (Get-Command dotnet).Source }
$env:DOTNET_ROOT = Split-Path $dotnet
$vpk = Join-Path $env:USERPROFILE '.dotnet\tools\vpk.exe'
if (-not (Test-Path $vpk)) { throw 'vpk fehlt. Installieren mit: dotnet tool install -g vpk' }

# 1. Version setzen
$proj = Join-Path $root 'Layts.csproj'
$xml = [System.IO.File]::ReadAllText($proj)
$xml = [regex]::Replace($xml, '<Version>.*?</Version>', "<Version>$Version</Version>")
[System.IO.File]::WriteAllText($proj, $xml, (New-Object System.Text.UTF8Encoding $false))
Write-Host "Version $Version gesetzt." -ForegroundColor Cyan

# 2. Bauen (eigener Ordner, damit eine laufende Layts.exe nicht stört)
$out = Join-Path $root 'build\app'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
& $dotnet publish -c Release -o $out
if ($LASTEXITCODE -ne 0) { throw 'Build fehlgeschlagen.' }

# GitHub-Token aus der Git-Anmeldung holen (wird nicht angezeigt)
$token = $null
if ($Upload) {
    # Anfrage als Datei (reine LF-Zeilen, ohne BOM) per cmd-Umleitung an Git geben –
    # Windows PowerShell 5 setzt beim Pipen sonst eine Kodierungsmarke davor.
    $req = Join-Path $env:TEMP "layts-cred-$PID.txt"
    [System.IO.File]::WriteAllText($req, "protocol=https`nhost=github.com`n`n", (New-Object System.Text.UTF8Encoding $false))
    try { $cred = cmd /c "git credential fill < `"$req`"" }
    finally { Remove-Item $req -Force -ErrorAction SilentlyContinue }
    $token = [string](($cred | Where-Object { $_ -like 'password=*' } | Select-Object -First 1) -replace '^password=', '')
    $token = $token.Trim()
    if (-not $token) { throw 'Keine GitHub-Anmeldung gefunden.' }

    # Vorheriges Release holen, damit Velopack kleine Delta-Updates erzeugen kann
    & $vpk download github --repoUrl $repo --token $token --outputDir releases
}

# 3. Installer + Update-Pakete
& $vpk pack --packId Layts --packVersion $Version --packDir $out --mainExe Layts.exe `
    --packTitle layts --packAuthors Oelweb --icon Assets\layts.ico --outputDir releases
if ($LASTEXITCODE -ne 0) { throw 'Packen fehlgeschlagen.' }

# 4. Hochladen
if ($Upload) {
    & $vpk upload github --repoUrl $repo --token $token --publish `
        --releaseName "layts v$Version" --tag "v$Version" --outputDir releases
    if ($LASTEXITCODE -ne 0) { throw 'Upload fehlgeschlagen.' }
    Write-Host "Release v$Version ist online: $repo/releases/tag/v$Version" -ForegroundColor Green
} else {
    Write-Host 'Fertig. Installer liegt in .\releases (nicht hochgeladen).' -ForegroundColor Green
}
