# =============================================================================
# download-catalog-seed.ps1
# =============================================================================
# Laedt die Rebrickable-Daily-Dumps (CSV.GZ) von cdn.rebrickable.com herunter,
# entpackt sie und legt sie als ZIP-Dateien in
#   LegoMinifigSorter.Core/Resources/CatalogSeed/
# ab. Die ZIPs werden zur Build-Zeit als Embedded Resources in die EXE gepackt.
#
# Aufruf:    .\tools\download-catalog-seed.ps1
# Aufruf mit Force-Refresh:  .\tools\download-catalog-seed.ps1 -Force
#
# Voraussetzungen:
#   - PowerShell 5.1 oder neuer
#   - Internetzugang zu https://cdn.rebrickable.com
#
# Dauer: ca. 30-60 Sekunden (je nach Internet)
# =============================================================================

[CmdletBinding()]
param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

# Liste aller Rebrickable-Daily-Dumps die wir brauchen.
# Die Datei "themes.csv.gz" wird mit eingepackt (kann fuer kuenftige Set-Themen
# nuetzlich sein), wird aber im Catalog-Schema aktuell nicht importiert.
$Files = @(
    'colors',
    'part_categories',
    'parts',
    'part_relationships',
    'minifigs',
    'sets',
    'themes',
    'inventories',
    'inventory_parts',
    'inventory_minifigs',
    'inventory_sets',
    'elements'
)

# Pfade ermitteln (relativ zum Script-Speicherort)
$ScriptDir   = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot    = Split-Path -Parent $ScriptDir
$TargetDir   = Join-Path $RepoRoot 'LegoMinifigSorter.Core\Resources\CatalogSeed'
$TempDir     = Join-Path $env:TEMP "lego-catalog-seed-$([guid]::NewGuid().ToString('N'))"

Write-Host "Repo-Wurzel:  $RepoRoot"
Write-Host "Ziel-Ordner:  $TargetDir"
Write-Host "Temp-Ordner:  $TempDir"

# Ziel-Ordner anlegen (falls noch nicht vorhanden)
New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null
New-Item -ItemType Directory -Path $TempDir   -Force | Out-Null

# TLS 1.2 erzwingen (auf alten PowerShell-Versionen sonst Default TLS 1.0)
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# Globaler Counter fuer den Fortschritt
$total = $Files.Count
$idx   = 0

foreach ($name in $Files) {
    $idx++
    $gzUrl   = "https://cdn.rebrickable.com/media/downloads/$name.csv.gz"
    $gzPath  = Join-Path $TempDir "$name.csv.gz"
    $csvPath = Join-Path $TempDir "$name.csv"
    $zipPath = Join-Path $TargetDir "$name.csv.zip"

    # Wenn ZIP bereits existiert und kein -Force, ueberspringen
    if (-not $Force -and (Test-Path $zipPath)) {
        Write-Host ("[{0}/{1}] Ueberspringe {2}.csv.zip (bereits vorhanden)" -f $idx, $total, $name) -ForegroundColor DarkGray
        continue
    }

    Write-Host ("[{0}/{1}] Lade {2}.csv.gz herunter..." -f $idx, $total, $name) -ForegroundColor Cyan

    try {
        # Download
        Invoke-WebRequest -Uri $gzUrl -OutFile $gzPath -UseBasicParsing

        # Entpacken (.gz -> .csv)
        $inStream  = [System.IO.File]::OpenRead($gzPath)
        $gzStream  = New-Object System.IO.Compression.GzipStream($inStream, [System.IO.Compression.CompressionMode]::Decompress)
        $outStream = [System.IO.File]::Create($csvPath)
        try {
            $gzStream.CopyTo($outStream)
        }
        finally {
            $outStream.Close()
            $gzStream.Close()
            $inStream.Close()
        }

        # Als ZIP neu packen (ein einzelnes CSV pro ZIP)
        if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
        Compress-Archive -Path $csvPath -DestinationPath $zipPath -CompressionLevel Optimal

        $sizeKb = [math]::Round((Get-Item $zipPath).Length / 1KB, 1)
        Write-Host ("    -> {0}.csv.zip ({1} KB)" -f $name, $sizeKb) -ForegroundColor Green
    }
    catch {
        Write-Host ("    FEHLER bei {0}: {1}" -f $name, $_.Exception.Message) -ForegroundColor Red
        throw
    }
}

# Temp-Ordner aufraeumen
Write-Host ""
Write-Host "Raeume Temp-Ordner auf..." -ForegroundColor DarkGray
Remove-Item -Recurse -Force $TempDir

Write-Host ""
Write-Host "Fertig. ZIPs liegen in: $TargetDir" -ForegroundColor Green
