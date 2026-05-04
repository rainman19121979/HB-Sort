using System.Text.Json;
using HBSort.Core.Models;

namespace HBSort.Tests;

/// <summary>
/// Audit W-8: Verifikation dass alte settings.json mit dem entfernten
/// CacheDays-Feld weiterhin geladen werden koennen, ohne Crash.
/// System.Text.Json ignoriert unbekannte Properties beim Deserialize per
/// Default - dieser Test sichert das fuer den konkreten Fall ab.
/// </summary>
public class PriceSettingsCacheDaysMigrationTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void Old_settings_json_with_CacheDays_loads_silently_into_new_PriceSettings()
    {
        // Eine alte settings.json wie sie vor Audit W-8 ausgesehen haette:
        // CacheDays gesetzt, BlPriceCacheTtlMinifigDays/PartDays noch nicht.
        var oldJson = """
        {
            "Provider": "BricklinkApi",
            "Currency": "EUR",
            "CacheDays": 7,
            "BlPriceCacheTtlMinifigDays": 30,
            "BlPriceCacheTtlPartDays": 60
        }
        """;

        var loaded = JsonSerializer.Deserialize<PriceSettings>(oldJson, Options);

        Assert.NotNull(loaded);
        Assert.Equal("BricklinkApi", loaded.Provider);
        Assert.Equal(30, loaded.BlPriceCacheTtlMinifigDays);
        Assert.Equal(60, loaded.BlPriceCacheTtlPartDays);
        // CacheDays gibt's in der Klasse nicht mehr - der Wert wird ignoriert,
        // aber kein Crash. Das ist der entscheidende Punkt.
    }

    [Fact]
    public void Defaults_for_new_PriceSettings_are_90_per_TTL()
    {
        var fresh = new PriceSettings();
        Assert.Equal(90, fresh.BlPriceCacheTtlMinifigDays);
        Assert.Equal(90, fresh.BlPriceCacheTtlPartDays);
    }

    [Fact]
    public void New_settings_json_does_not_serialize_removed_CacheDays_field()
    {
        // Wenn man eine frische PriceSettings serialisiert, darf in der JSON
        // kein "CacheDays"-Key mehr auftauchen.
        var fresh = new PriceSettings();
        var json = JsonSerializer.Serialize(fresh);
        Assert.DoesNotContain("CacheDays\":", json);
        Assert.Contains("BlPriceCacheTtlMinifigDays", json);
    }
}
