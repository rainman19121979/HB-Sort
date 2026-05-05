# HB-Sort

Windows-Desktop-App zum Sortieren von Klemmbaustein-Minifiguren und
Einzelteilen. HB-Sort erkennt Teile per USB-Webcam, ordnet sie wartenden
Figuren zu, verwaltet Lagerfaecher und unterstuetzt den Verkauf via
BrickStore- und BrickLink-Export.

> **Disclaimer:** LEGO® ist eine Marke der LEGO-Gruppe, die HB-Sort
> weder sponsert noch unterstuetzt oder anerkennt. HB-Sort ist ein
> unabhaengiges, kostenloses Werkzeug ohne offizielle Verbindung zur
> LEGO-Gruppe.

## Features

- **Webcam-Erkennung** ueber Brickognize-API: Minifiguren und Einzelteile
  inklusive Farbe.
- **Reverse-Match**: gescannte Einzelteile finden automatisch passende
  wartende Figuren; bei vollstaendigem Match wird die Figur als komplett
  markiert.
- **Lagerfaecher-Verwaltung** mit Smart-Storage-Suggestions: gleiches
  Teil + gleiche Farbe wird automatisch ins bestehende Fach gestapelt.
- **"Was kann ich bauen?"-Live-Vorschau** auf Basis deiner gelagerten
  Einzelteile.
- **BrickLink-Preise** (optional, eigener BL-Consumer-Account noetig)
  mit Cache + Stale-While-Revalidate, getrennten TTLs fuer Minifigs/Teile,
  Korrekturfaktoren fuer Verkaufsgebuehren.
- **BSX-Export** fuer BrickStore: komplette Figuren und Einzelteile in
  einer Datei, mit Lagerfach-Information im internen `<Remarks>`-Feld
  und User-Notizen im oeffentlichen `<Comments>`-Feld.
- **Wanted-List-Export** fuer BrickLink (XML pro Figur oder eine grosse
  Datei mit allen fehlenden Teilen).
- **Volle Tastatur-Bedienung** (Leertaste = scannen, Strg+S/L/H =
  Tab-Wechsel, F1 = Hilfe, etc.).
- **Integrierte Hilfe** (F1) mit Markdown-Inhalten.
- **Theming** folgt dem Windows-System-Theme (hell/dunkel).
- **Tray-Icon** mit Minimieren statt Schliessen.

## Voraussetzungen

- Windows 10 oder 11
- USB-Webcam
- BrickStore-Datenbank: muss beim ersten Start importiert werden ueber
  **Einstellungen → BrickLink → Catalog-Daten → "Von GitHub importieren"**.
  Holt die ~570 MB BrickStore-DB von rgriebl/brickstore-database; ohne
  diesen Schritt kennt HB-Sort die Teile-Stammdaten nicht.
- BrickLink-Consumer-Account (optional, nur fuer Live-Preise und
  Wanted-List-Export)
- Fuer den Source-Build: .NET 8 SDK (https://dotnet.microsoft.com/download)

## Installation

### Aus dem Source bauen

```powershell
git clone https://github.com/rainman19121979/HB-Sort.git
cd HB-Sort
dotnet build -c Release
dotnet run --project HBSort
```

### Self-contained Release

Die GitHub-Releases unter
https://github.com/rainman19121979/HB-Sort/releases bieten ein fertig
gepacktes Single-File-Build fuer Windows x64 (kein .NET-Install noetig).

## Konfiguration

Beim ersten Start die Tokens unter **Einstellungen → BrickLink** eintragen
(falls Live-Preise oder Wanted-List gewuenscht). Die Tokens werden via
Windows-DPAPI verschluesselt im AppData-Ordner abgelegt - sie sind
ausschliesslich auf demselben Windows-Benutzerprofil entschluesselbar.

Details zu jedem Bereich gibt es in der **integrierten Hilfe** (F1).

## Datenquellen & Drittanbieter

HB-Sort verlaesst sich auf drei externe Datenquellen. Sie sind klar
getrennt und alle bewusst gewaehlt - HB-Sort sammelt selbst keine
Daten zentral.

### Brickognize

- **Wofuer**: Webcam-Bild-basierte Teile- und Minifig-Erkennung (KI-Modell).
- **Link**: https://brickognize.com
- **Hinweis**: HB-Sort schickt nur das aufgenommene Webcam-Bild an die
  Brickognize-API zur Erkennung. Kein Account ist Pflicht fuer die
  Nutzung von HB-Sort - die API ist frei nutzbar mit fairem Rate-Limit.
  Mehr Infos auf brickognize.com.

### BrickStore-Datenbank (rgriebl/brickstore-database)

- **Wofuer**: Lokale Item-Stammdaten (Minifig-Subsets, Teile-Namen,
  Farben). HB-Sort nutzt die aufbereitete Daten-Pipeline von BrickStore
  von Robert Griebl, weil sie die offiziellen BrickLink-Daten in einem
  nutzbaren XML-Format zur Verfuegung stellt.
- **Link**: https://github.com/rgriebl/brickstore-database
- **Lizenz der Daten**: gemaess BrickStore-DB-Repo (GPL-3 / Daten-
  Lizenzhinweise dort).
- **Wichtig**: HB-Sort verteilt diese Daten NICHT mit. Sie werden beim
  ersten Setup direkt aus dem oben genannten Repo geladen.

### BrickLink-API

- **Wofuer**: Optional - aktuelle Preise (Sold/Stock-Guides) fuer
  gescannte Items.
- **Link**: https://www.bricklink.com/v3/api.page
- **Hinweis**: Erfordert einen eigenen BL-Consumer-Account beim User
  inklusive IP-Whitelist im BL-Profil. HB-Sort selbst sammelt keine
  BL-Daten zentral; alle Calls laufen direkt vom User-PC zur BL-API
  ueber dessen eigene Tokens.

## Tech-Stack

- C# 12 / .NET 8 LTS
- WPF + ModernWpfUI
- MVVM via CommunityToolkit.Mvvm
- EF Core 8 (SQLite) fuer Userdaten
- Microsoft.Data.Sqlite (raw ADO.NET) fuer den BL-Cache
- BricklinkSharp (BL-API, OAuth1)
- OpenCvSharp4 (Webcam)
- Markdig + Markdig.Wpf (Hilfe-Rendering)
- Serilog (Logging)
- Windows DPAPI (Token-Verschluesselung)

## Lizenz

HB-Sort steht unter der **GNU General Public License v3.0** - siehe
[LICENSE](LICENSE).

## Mitwirken

Issues und Pull Requests willkommen. Vor groesseren Aenderungen bitte
ein Issue eroeffnen, damit wir Richtung und Scope abstimmen koennen.

## Entwicklungs-Hinweis

Developed with AI assistance.
