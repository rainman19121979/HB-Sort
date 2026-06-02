using HBSort.Core.Models;

namespace HBSort.Core.Services;

/// <summary>
/// Reine (UI-freie) Hilfslogik fuer die Spalten-Persistenz der Lagerliste.
///
/// Warum hier in HBSort.Core und nicht im View-Code-Behind?
/// - Die Merge-/Filter-Logik (welche gespeicherte Breite passt zu welcher
///   aktuellen Spalte) ist die eigentlich fehleranfaellige Stelle. Indem wir
///   sie von WPF entkoppeln, koennen wir sie mit xUnit testen, ohne ein
///   Fenster hosten zu muessen.
/// - Der View-Code-Behind ruft nur diese Methoden auf und uebersetzt das
///   Ergebnis in echte DataGrid-Operationen.
///
/// Identifikation der Spalten ausschliesslich ueber den Header-Text. Wenn
/// eine gespeicherte Spalte im aktuellen Layout nicht mehr existiert, wird
/// sie ignoriert - nie ein Crash.
/// </summary>
public static class InventoryGridLayoutHelper
{
    /// <summary>
    /// Untergrenze fuer eine gespeicherte Spaltenbreite. Schuetzt davor, dass
    /// eine versehentlich auf 0 (oder negativ) geschrumpfte Spalte nach dem
    /// Neustart unsichtbar bleibt.
    /// </summary>
    public const double MinColumnWidth = 20.0;

    /// <summary>
    /// Obergrenze, damit eine korrupte settings.json (extrem grosser Wert)
    /// nicht die ganze Tabelle aus dem sichtbaren Bereich schiebt.
    /// </summary>
    public const double MaxColumnWidth = 2000.0;

    /// <summary>
    /// Ermittelt fuer eine Spalte (per Header) die anzuwendende Breite aus dem
    /// gespeicherten Layout. Liefert null wenn fuer den Header nichts (Sinnvolles)
    /// gespeichert ist - dann soll die XAML-Default-Breite gelten.
    ///
    /// Robust gegen: unbekannter Header, NaN/Infinity, Werte ausserhalb
    /// [MinColumnWidth..MaxColumnWidth].
    /// </summary>
    public static double? ResolveWidth(InventoryGridLayout layout, string? header)
    {
        if (layout is null) return null;
        if (string.IsNullOrEmpty(header)) return null;
        if (!layout.ColumnWidths.TryGetValue(header, out var width)) return null;
        if (double.IsNaN(width) || double.IsInfinity(width)) return null;
        if (width < MinColumnWidth || width > MaxColumnWidth) return null;
        return width;
    }

    /// <summary>
    /// Prueft, ob fuer den uebergebenen Header eine aktive Sortierung gemerkt
    /// ist. Liefert true und setzt <paramref name="ascending"/>, wenn ja.
    /// Header-Vergleich case-sensitiv (Header sind im XAML fest verdrahtet).
    /// </summary>
    public static bool TryGetSort(InventoryGridLayout layout, string? header, out bool ascending)
    {
        ascending = true;
        if (layout is null) return false;
        if (string.IsNullOrEmpty(header)) return false;
        if (string.IsNullOrEmpty(layout.SortColumnHeader)) return false;
        if (!string.Equals(layout.SortColumnHeader, header, StringComparison.Ordinal)) return false;
        ascending = layout.SortAscending;
        return true;
    }
}
