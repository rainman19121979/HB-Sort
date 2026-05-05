using System.Text.Json;
using HBSort.Core.Models;

namespace HBSort.Tests;

/// <summary>
/// UX X.19 Teil 3b: Tests fuer die neuen horizontalen Splitter-Properties
/// in WindowState.
///
/// Schwerpunkte:
/// - Defaults sind 0.65 (entspricht dem alten 65/35-Layout).
/// - Alte settings.json (vor X.19) ohne diese Properties laedt sauber
///   und liefert Defaults zurueck.
/// - Werte werden im Round-Trip JSON beibehalten.
/// - settings.json mit unbekannten Zusatzfeldern wird tolerant geladen.
///
/// Die Clamp-Logik (NaN, &lt;0.05, &gt;0.95 -&gt; Default) lebt im SortingView-
/// Code-Behind, nicht im POCO. Tests dafuer waeren WPF-spezifisch und
/// braeuchten ein Window-Hosting; im aktuellen Test-Stil verzichten wir
/// darauf - die Logik ist klein und manuell verifizierbar.
/// </summary>
public class WindowStateSplitterRatiosTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void Defaults_are_0_65_for_all_three_columns()
    {
        var ws = new WindowState();
        Assert.Equal(0.65, ws.Column1HorizontalSplitterRatio);
        Assert.Equal(0.65, ws.Column2HorizontalSplitterRatio);
        Assert.Equal(0.65, ws.Column3HorizontalSplitterRatio);
    }

    [Fact]
    public void Old_settings_json_without_horizontal_ratios_loads_with_defaults()
    {
        // Alte settings.json wie sie vor UX X.19 ausgesehen haette: nur die
        // Spalten-Verhaeltnisse, keine horizontalen Splitter-Properties.
        var oldJson = """
        {
            "Width": 1920,
            "Height": 1080,
            "IsMaximized": true,
            "SplitterColumnRatio": 0.4,
            "SplitterColumnRatio2": 0.3
        }
        """;

        var loaded = JsonSerializer.Deserialize<WindowState>(oldJson, Options);

        Assert.NotNull(loaded);
        Assert.Equal(0.4, loaded!.SplitterColumnRatio);
        Assert.Equal(0.3, loaded.SplitterColumnRatio2);
        // Horizontale Werte sind nicht in der JSON -> POCO-Defaults greifen.
        Assert.Equal(0.65, loaded.Column1HorizontalSplitterRatio);
        Assert.Equal(0.65, loaded.Column2HorizontalSplitterRatio);
        Assert.Equal(0.65, loaded.Column3HorizontalSplitterRatio);
    }

    [Fact]
    public void New_settings_json_round_trip_preserves_horizontal_ratios()
    {
        var ws = new WindowState
        {
            Column1HorizontalSplitterRatio = 0.55,
            Column2HorizontalSplitterRatio = 0.7,
            Column3HorizontalSplitterRatio = 0.5
        };

        var json = JsonSerializer.Serialize(ws);
        var loaded = JsonSerializer.Deserialize<WindowState>(json, Options);

        Assert.NotNull(loaded);
        Assert.Equal(0.55, loaded!.Column1HorizontalSplitterRatio);
        Assert.Equal(0.7,  loaded.Column2HorizontalSplitterRatio);
        Assert.Equal(0.5,  loaded.Column3HorizontalSplitterRatio);
    }

    [Fact]
    public void Settings_with_unknown_extra_fields_load_silently()
    {
        // Robustheit gegen settings.json aus zukuenftigen Versionen, die
        // Felder enthalten die das aktuelle POCO nicht kennt.
        var futureJson = """
        {
            "Width": 1920,
            "Column1HorizontalSplitterRatio": 0.5,
            "FutureFlagXyz": true,
            "FutureNestedThing": { "key": 42 }
        }
        """;

        var loaded = JsonSerializer.Deserialize<WindowState>(futureJson, Options);

        Assert.NotNull(loaded);
        Assert.Equal(0.5, loaded!.Column1HorizontalSplitterRatio);
        Assert.Equal(0.65, loaded.Column2HorizontalSplitterRatio);
    }

    [Theory]
    [InlineData(-0.5)]
    [InlineData(0.0)]
    [InlineData(0.04)]      // unter MinRatio 0.05
    [InlineData(0.96)]      // ueber MaxRatio 0.95
    [InlineData(2.0)]
    public void Out_of_range_values_round_trip_through_JSON(double bad)
    {
        // Out-of-range-Werte werden vom POCO ohne Aenderung gespeichert/geladen.
        // Das Clamping passiert beim Anwenden im SortingView-Code-Behind
        // (ClampOrDefault), nicht hier. Dieser Test sichert nur dass JSON-
        // Round-Trip nicht crashed.
        var ws = new WindowState { Column1HorizontalSplitterRatio = bad };
        var json = JsonSerializer.Serialize(ws);
        var loaded = JsonSerializer.Deserialize<WindowState>(json, Options);
        Assert.NotNull(loaded);
        Assert.Equal(bad, loaded!.Column1HorizontalSplitterRatio);
    }

    // Hinweis zu NaN/Infinity: System.Text.Json wirft beim Serialize standardmaessig
    // fuer non-finite double-Werte. Wir koennen sie also nicht ueber JSON in eine
    // settings.json bekommen - nur bei manueller Korruption durch externes Editieren.
    // Der ClampOrDefault-Pfad im Code-Behind faengt sie trotzdem ab als Defensiv-
    // Schutz; ein UI-Test dafuer waere disproportional aufwendig.
}
