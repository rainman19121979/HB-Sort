using System.Text.Json;
using HBSort.Core.Models;
using HBSort.Core.Services;

namespace HBSort.Tests;

/// <summary>
/// Tests fuer die Spalten-Persistenz der Lagerliste (Temporaeres Inventar).
///
/// Schwerpunkte:
/// - Happy-Path: gespeicherte Breite + Sortierung werden korrekt aufgeloest.
/// - Robustheit gegen geaenderte Spalten-Layouts: unbekannter Header wird
///   ignoriert (kein Crash, liefert null/false).
/// - Defensive Clamps: NaN/Infinity/Out-of-Range-Breiten werden verworfen.
/// - JSON-Round-Trip: Layout bleibt erhalten; alte settings.json ohne das
///   Feld laedt mit Default (leeres Layout).
///
/// Die WPF-spezifische Anwendung (DataGridColumn.Width setzen, SortDirection,
/// SortDescriptions) lebt im View-Code-Behind und ist hier bewusst nicht
/// getestet - sie braeuchte ein gehostetes Fenster. Die fehleranfaellige
/// Merge-/Filter-Logik ist in den hier getesteten Helper ausgelagert.
/// </summary>
public class InventoryGridLayoutHelperTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // ----------------------------------------------------------------
    // ResolveWidth - Happy-Path
    // ----------------------------------------------------------------

    [Fact]
    public void ResolveWidth_returns_saved_width_for_known_header()
    {
        var layout = new InventoryGridLayout
        {
            ColumnWidths = { ["Farbe"] = 175.0 }
        };

        var result = InventoryGridLayoutHelper.ResolveWidth(layout, "Farbe");

        Assert.Equal(175.0, result);
    }

    [Fact]
    public void ResolveWidth_returns_null_for_unknown_header()
    {
        // Robustheit: Spalte aus alter settings.json existiert im neuen
        // Layout nicht mehr -> ignorieren statt crashen.
        var layout = new InventoryGridLayout
        {
            ColumnWidths = { ["AlteSpalteDieEsNichtMehrGibt"] = 120.0 }
        };

        var result = InventoryGridLayoutHelper.ResolveWidth(layout, "Farbe");

        Assert.Null(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ResolveWidth_returns_null_for_null_or_empty_header(string? header)
    {
        var layout = new InventoryGridLayout { ColumnWidths = { ["Farbe"] = 175.0 } };

        Assert.Null(InventoryGridLayoutHelper.ResolveWidth(layout, header));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0.0)]
    [InlineData(5.0)]            // unter MinColumnWidth (20)
    [InlineData(5000.0)]         // ueber MaxColumnWidth (2000)
    public void ResolveWidth_returns_null_for_invalid_width(double bad)
    {
        var layout = new InventoryGridLayout { ColumnWidths = { ["Anzahl"] = bad } };

        Assert.Null(InventoryGridLayoutHelper.ResolveWidth(layout, "Anzahl"));
    }

    [Theory]
    [InlineData(20.0)]           // exakt MinColumnWidth
    [InlineData(2000.0)]         // exakt MaxColumnWidth
    [InlineData(123.4)]
    public void ResolveWidth_accepts_width_within_bounds(double good)
    {
        var layout = new InventoryGridLayout { ColumnWidths = { ["Anzahl"] = good } };

        Assert.Equal(good, InventoryGridLayoutHelper.ResolveWidth(layout, "Anzahl"));
    }

    // ----------------------------------------------------------------
    // TryGetSort
    // ----------------------------------------------------------------

    [Fact]
    public void TryGetSort_matches_saved_sort_column_ascending()
    {
        var layout = new InventoryGridLayout
        {
            SortColumnHeader = "Beschreibung",
            SortAscending = true
        };

        var matched = InventoryGridLayoutHelper.TryGetSort(layout, "Beschreibung", out var ascending);

        Assert.True(matched);
        Assert.True(ascending);
    }

    [Fact]
    public void TryGetSort_matches_saved_sort_column_descending()
    {
        var layout = new InventoryGridLayout
        {
            SortColumnHeader = "Anzahl",
            SortAscending = false
        };

        var matched = InventoryGridLayoutHelper.TryGetSort(layout, "Anzahl", out var ascending);

        Assert.True(matched);
        Assert.False(ascending);
    }

    [Fact]
    public void TryGetSort_false_for_different_header()
    {
        var layout = new InventoryGridLayout { SortColumnHeader = "Anzahl", SortAscending = true };

        var matched = InventoryGridLayoutHelper.TryGetSort(layout, "Beschreibung", out var ascending);

        Assert.False(matched);
        Assert.True(ascending); // out-Default
    }

    [Fact]
    public void TryGetSort_false_when_no_sort_saved()
    {
        // Frisches Layout ohne aktive Sortierung.
        var layout = new InventoryGridLayout();

        var matched = InventoryGridLayoutHelper.TryGetSort(layout, "Beschreibung", out _);

        Assert.False(matched);
    }

    // ----------------------------------------------------------------
    // JSON-Round-Trip + Rueckwaerts-Kompatibilitaet
    // ----------------------------------------------------------------

    [Fact]
    public void Layout_round_trips_through_json()
    {
        var settings = new AppSettings();
        settings.InventoryGridLayout.ColumnWidths["Farbe"] = 200.0;
        settings.InventoryGridLayout.ColumnWidths["Anzahl"] = 80.0;
        settings.InventoryGridLayout.SortColumnHeader = "Beschreibung";
        settings.InventoryGridLayout.SortAscending = false;

        var json = JsonSerializer.Serialize(settings, Options);
        var loaded = JsonSerializer.Deserialize<AppSettings>(json, Options);

        Assert.NotNull(loaded);
        Assert.Equal(200.0, loaded!.InventoryGridLayout.ColumnWidths["Farbe"]);
        Assert.Equal(80.0, loaded.InventoryGridLayout.ColumnWidths["Anzahl"]);
        Assert.Equal("Beschreibung", loaded.InventoryGridLayout.SortColumnHeader);
        Assert.False(loaded.InventoryGridLayout.SortAscending);
    }

    [Fact]
    public void Old_settings_json_without_layout_loads_with_empty_default()
    {
        // settings.json aus einer Version vor diesem Feature: kein
        // InventoryGridLayout-Knoten. POCO-Default greift (leeres Layout).
        var oldJson = """
        {
            "SelectedCameraIndex": 0,
            "ShowTooltips": true
        }
        """;

        var loaded = JsonSerializer.Deserialize<AppSettings>(oldJson, Options);

        Assert.NotNull(loaded);
        Assert.NotNull(loaded!.InventoryGridLayout);
        Assert.Empty(loaded.InventoryGridLayout.ColumnWidths);
        Assert.Null(loaded.InventoryGridLayout.SortColumnHeader);
        Assert.True(loaded.InventoryGridLayout.SortAscending); // POCO-Default
    }

    [Fact]
    public void Default_layout_resolves_nothing()
    {
        // Ein frisches Layout (App-Erststart) darf keine Breite/Sortierung
        // erzwingen - die XAML-Defaults sollen gelten.
        var layout = new InventoryGridLayout();

        Assert.Null(InventoryGridLayoutHelper.ResolveWidth(layout, "Farbe"));
        Assert.False(InventoryGridLayoutHelper.TryGetSort(layout, "Farbe", out _));
    }
}
