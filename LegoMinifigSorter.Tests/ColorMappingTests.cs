using LegoMinifigSorter.Core.Services;

namespace LegoMinifigSorter.Tests;

/// <summary>
/// Sanity-Checks fuer die generierte ColorMapping-Tabelle.
/// Pruefen wir die wichtigsten "Heavy-Hitter"-Mappings (Standardfarben),
/// damit wir bei einer Re-Generation nicht versehentlich schlechtere
/// Daten einchecken.
/// </summary>
public class ColorMappingTests
{
    /// <summary>
    /// Tabellarische bekannte Mappings (RB-ID, erwartete BL-ID, BL-Name).
    /// Das sind die zentralen Standardfarben, die JEDE LEGO-Sammlung trifft.
    /// </summary>
    [Theory]
    [InlineData(71, 86, "Light Bluish Gray")]
    [InlineData(0,  11, "Black")]
    [InlineData(15, 1,  "White")]
    [InlineData(14, 3,  "Yellow")]
    [InlineData(4,  5,  "Red")]
    [InlineData(1,  7,  "Blue")]
    public void TryGetBricklinkId_returns_expected_mapping(int rebrickableId, int expectedBlId, string expectedName)
    {
        var found = ColorMapping.TryGetBricklinkId(rebrickableId, out var blId);

        Assert.True(found, $"Erwartetes Mapping RB={rebrickableId} fehlt in ColorMapping.");
        Assert.Equal(expectedBlId, blId);

        var name = ColorMapping.GetBricklinkColorName(rebrickableId);
        Assert.Equal(expectedName, name);
    }

    [Fact]
    public void TryGetBricklinkId_returns_false_for_unmapped_color()
    {
        // Modulex Light Bluish Gray (RB 1014) hat in BrickLink keine Entsprechung.
        var found = ColorMapping.TryGetBricklinkId(1014, out var blId);

        Assert.False(found);
        Assert.Equal(0, blId);

        var name = ColorMapping.GetBricklinkColorName(1014);
        Assert.Null(name);
    }

    [Fact]
    public void TryGetBricklinkId_returns_false_for_unknown_rebrickable_id()
    {
        // RB-ID die schlicht nicht in der Tabelle steht (z.B. 99999)
        var found = ColorMapping.TryGetBricklinkId(99999, out var blId);

        Assert.False(found);
        Assert.Equal(0, blId);
    }

    [Fact]
    public void RebrickableToBricklink_dictionary_has_expected_size()
    {
        // Header der Datei sagt: 271 RB-Farben, 178 mit BL-Mapping.
        // Wenn die Tabelle stark schrumpft oder waechst, sollen wir das mitkriegen.
        Assert.Equal(271, ColorMapping.RebrickableToBricklink.Count);

        var withMapping = ColorMapping.RebrickableToBricklink.Values.Count(v => v.HasValue);
        Assert.Equal(178, withMapping);
    }

    [Fact]
    public void BricklinkColorNames_contains_entries_for_all_mapped_ids()
    {
        // Jede BL-ID, die im Mapping vorkommt, sollte auch in der Namens-Tabelle stehen.
        // Wenn das schief geht, wuerde die UI "BL:42" ohne Namen anzeigen.
        var orphanIds = ColorMapping.RebrickableToBricklink.Values
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .Where(id => !ColorMapping.BricklinkColorNames.ContainsKey(id))
            .Distinct()
            .ToList();

        Assert.Empty(orphanIds);
    }

    [Theory]
    [InlineData(71, 86)]   // Light Bluish Gray
    [InlineData(72, 85)]   // Dark Bluish Gray (sollte ebenso ein Mapping haben)
    public void Light_and_dark_bluish_gray_both_mapped(int rebrickableId, int expectedBlId)
    {
        // Klassisches User-Problem: User scannt Teil und kriegt Light Bluish Gray
        // angezeigt obwohl Dark Bluish Gray korrekt waere. Beide muessen sicher
        // mappen koennen, damit die Korrektur in beide Richtungen klappt.
        var found = ColorMapping.TryGetBricklinkId(rebrickableId, out var blId);
        Assert.True(found);
        Assert.Equal(expectedBlId, blId);
    }
}
