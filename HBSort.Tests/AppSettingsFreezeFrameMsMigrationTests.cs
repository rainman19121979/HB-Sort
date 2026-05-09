using System.Text.Json;
using HBSort.Core.Models;

namespace HBSort.Tests;

/// <summary>
/// UX-Iteration X.21 Teil 2: Verifikation dass alte settings.json mit dem
/// entfernten FreezeFrameMs-Feld weiterhin geladen werden koennen, ohne
/// Crash. System.Text.Json ignoriert unbekannte Properties beim
/// Deserialize per Default - dieser Test sichert das fuer den konkreten
/// Fall ab.
///
/// Grund fuer die Entfernung: das Setting wurde zur Laufzeit nirgendwo
/// mehr gelesen (Freeze-Verhalten in UX-1 FIX 4 entfernt). Nur der UI-
/// Slider blieb. Property + Slider sind in dieser Iteration weg, alte
/// settings.json muss aber lautlos weiter laden.
/// </summary>
public class AppSettingsFreezeFrameMsMigrationTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void Old_settings_json_with_FreezeFrameMs_loads_silently()
    {
        // Wie eine settings.json vor UX X.21: FreezeFrameMs noch drin.
        var oldJson = """
        {
            "SelectedCameraIndex": 0,
            "ScoreThresholdAuto": 0.85,
            "ScoreThresholdMin": 0.5,
            "ScanCooldownMs": 1000,
            "FreezeFrameMs": 1500,
            "SoundEnabled": false
        }
        """;

        // Nicht crashen
        var loaded = JsonSerializer.Deserialize<AppSettings>(oldJson, Options);

        Assert.NotNull(loaded);
        Assert.Equal(0, loaded!.SelectedCameraIndex);
        Assert.Equal(0.85, loaded.ScoreThresholdAuto);
        Assert.Equal(1000, loaded.ScanCooldownMs);
        Assert.False(loaded.SoundEnabled);
        // FreezeFrameMs ist nicht mehr in AppSettings - der Wert wird
        // ignoriert. Wenn jemand die Property doch wieder einfuehrt, faellt
        // dieser Test als kompilierender Test auf (bzw. der Round-Trip-Test
        // unten wuerde dann den alten Wert behalten).
    }

    [Fact]
    public void New_settings_json_does_not_serialize_FreezeFrameMs()
    {
        // Nach dem Refactor darf das Feld auch beim Schreiben nicht mehr
        // vorkommen.
        var fresh = new AppSettings();
        var json = JsonSerializer.Serialize(fresh, Options);

        Assert.DoesNotContain("FreezeFrameMs", json);
    }

    [Fact]
    public void Old_value_is_dropped_in_round_trip()
    {
        // Alte JSON laden -> sofort wieder serialisieren. Der alte
        // FreezeFrameMs-Wert darf nicht mehr im Output auftauchen.
        var oldJson = """{"FreezeFrameMs": 1500, "ScanCooldownMs": 1000}""";

        var loaded = JsonSerializer.Deserialize<AppSettings>(oldJson, Options);
        var roundTrip = JsonSerializer.Serialize(loaded!, Options);

        Assert.DoesNotContain("FreezeFrameMs", roundTrip);
        Assert.Contains("\"ScanCooldownMs\":1000", roundTrip);
    }

    /// <summary>
    /// UX X.29 Block G (v0.1.16): DefaultPartsCollected ueberlebt einen
    /// Round-Trip durch JSON-Serialisierung.
    /// </summary>
    [Fact]
    public void DefaultPartsCollected_round_trips_through_json()
    {
        var s = new AppSettings { DefaultPartsCollected = true };
        var json = JsonSerializer.Serialize(s, Options);
        Assert.Contains("\"DefaultPartsCollected\":true", json);

        var loaded = JsonSerializer.Deserialize<AppSettings>(json, Options);
        Assert.NotNull(loaded);
        Assert.True(loaded!.DefaultPartsCollected);
    }

    [Fact]
    public void DefaultPartsCollected_default_value_is_false()
    {
        var s = new AppSettings();
        Assert.False(s.DefaultPartsCollected);
    }

    [Fact]
    public void Old_settings_json_without_DefaultPartsCollected_loads_with_default_false()
    {
        // Alte settings.json vor v0.1.16 hatte das Feld nicht.
        var oldJson = """
        {
            "SelectedCameraIndex": 0,
            "ScanCooldownMs": 1000
        }
        """;
        var loaded = JsonSerializer.Deserialize<AppSettings>(oldJson, Options);
        Assert.NotNull(loaded);
        Assert.False(loaded!.DefaultPartsCollected);
    }
}
