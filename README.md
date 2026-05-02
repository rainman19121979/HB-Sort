# HB-Sort

Sortier-Werkzeug fuer Klemmbausteine. Erkennt Minifiguren und Teile per
Kamera (Brickognize-API) und verwaltet Lagerfaecher.

## Features
- Live-Erkennung per USB-Kamera + Brickognize
- BrickLink-Catalog-Lookup mit lokalem Cache (~200.000 Items)
- Sortier-Workflow mit Lagerfaechern und automatischem Reverse-Match
- BrickStore-Daten-Import von GitHub (taeglich aktualisierte BL-Daten)

## Voraussetzungen
- Windows 10/11
- .NET 8 SDK (https://dotnet.microsoft.com/download)
- USB-Webcam
- BrickLink-Account mit API-Tokens (in Settings -> BrickLink-API)

## Build & Run

```powershell
dotnet build
dotnet run --project HBSort
```

## Markenrechte / Disclaimer

LEGO ist eine eingetragene Marke der LEGO Gruppe, die diese Software
weder sponsert noch unterstuetzt. HB-Sort ist ein unabhaengiges
Werkzeug ohne offizielle Verbindung zur LEGO Gruppe.

BrickLink-Catalog-Daten sind Eigentum von BrickLink (bricklink.com).
Diese Anwendung nutzt die Aufbereitung dieser Daten durch BrickStore
(brickstore.dev, GPL-3, github.com/rgriebl/brickstore-database).

Brickognize ist ein Service von brickognize.com.

## Status
Phase 5.5+ abgeschlossen, in aktiver Entwicklung.

## Tech-Stack
- C# 12 / .NET 8 LTS
- WPF + ModernWPF UI
- MVVM via CommunityToolkit.Mvvm
- EF Core 8 (SQLite)
- BricklinkSharp (BL-API, OAuth1)
- OpenCvSharp4 (Webcam)
- Serilog (Logging)
- DPAPI (Token-Verschluesselung)
