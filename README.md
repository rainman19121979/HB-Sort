# LegoMinifig Sortierer

Windows-Desktop-App zum Identifizieren und Sortieren von losen
LEGO-Minifig-Teilen via USB-Webcam und Brickognize-API.

## Status
In Entwicklung.

## Voraussetzungen
- Windows 10/11
- .NET 8 SDK (https://dotnet.microsoft.com/download)
- USB-Webcam
- BrickLink-Account mit API-Tokens (in Settings -> BrickLink-API)
- Rebrickable Seed-Daten via tools\download-catalog-seed.ps1

## Build & Run

```powershell
dotnet build
dotnet run --project LegoMinifigSorter
```

## Phasen-Status
- Phase 1: WPF + Webcam
- Phase 1.5: Catalog-Import
- Phase 2: Brickognize-Integration
- Phase 2.5: BL-Bilder + persistenter Cache
- Phase R1-R3: BL-API + Cache + Stale-while-revalidate
- Phase 4: Lagerfach-Verwaltung
- Phase 5: Modus B - Einzelteil-Scan + Reverse-Match
- Phase X: Tabs + Lagerliste (BrickStore-Style) + Zerlegen-Wizard
- Phase 6: Komplettierungs-Workflow (geplant)
- Phase 5.5: Catalog-Builder (geplant)
- Phase 7: BSX-Export + Polish (geplant)
- Phase 8: BL-Preis-Tool-Anbindung (geplant)

## Tech-Stack
- C# 12 / .NET 8 LTS
- WPF + ModernWPF UI
- MVVM via CommunityToolkit.Mvvm
- EF Core 8 (SQLite)
- BricklinkSharp (BL-API, OAuth1)
- OpenCvSharp4 (Webcam)
- Serilog (Logging)
- DPAPI (Token-Verschluesselung)

## Lizenz
GPL-3.0 (geplant fuer Phase 7)
