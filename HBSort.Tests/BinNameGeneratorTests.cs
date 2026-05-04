using HBSort.Core.Services;

namespace HBSort.Tests;

/// <summary>
/// Tests fuer den Bulk-Bin-Name-Generator.
///
/// Audit M-12 (2026-05-04): mehrere strukturell aehnliche [Fact]-Tests
/// auf [Theory] mit [InlineData] konsolidiert. Die Test-Anzahl insgesamt
/// steigt leicht (Theory-Cases werden einzeln gezaehlt), die Lesbarkeit
/// und Erweiterbarkeit ist deutlich besser.
/// </summary>
public class BinNameGeneratorTests
{
    // ====================================================================
    // Generate(): erfolgreiche Sequenzen
    // ====================================================================

    [Theory]
    [InlineData("1", "3", 0,    new[] { "1", "2", "3" })]
    [InlineData("1", "5", 0,    new[] { "1", "2", "3", "4", "5" })]
    [InlineData("1", "3", 3,    new[] { "001", "002", "003" })]
    [InlineData("9", "11", 2,   new[] { "09", "10", "11" })]
    public void Generate_numbers(string start, string end, int padding, string[] expected)
    {
        var result = BinNameGenerator.Generate("", "", BinSequenceType.Numbers, start, end, padding);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("A", "C",     new[] { "A", "B", "C" })]
    [InlineData("X", "Z",     new[] { "X", "Y", "Z" })]
    [InlineData("Z", "AB",    new[] { "Z", "AA", "AB" })]
    [InlineData("AAA", "AAC", new[] { "AAA", "AAB", "AAC" })]
    [InlineData("A", "A",     new[] { "A" })]
    public void Generate_letters(string start, string end, string[] expected)
    {
        var result = BinNameGenerator.Generate("", "", BinSequenceType.Letters, start, end);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Box ", "",       BinSequenceType.Numbers, "1", "2", new[] { "Box 1", "Box 2" })]
    [InlineData("",     "-rot",   BinSequenceType.Numbers, "1", "2", new[] { "1-rot", "2-rot" })]
    [InlineData("Box ", "-rot",   BinSequenceType.Numbers, "1", "2", new[] { "Box 1-rot", "Box 2-rot" })]
    [InlineData("Bin-", "",       BinSequenceType.Letters, "A", "B", new[] { "Bin-A", "Bin-B" })]
    public void Generate_prefix_and_suffix_combine(string prefix, string suffix, BinSequenceType type, string start, string end, string[] expected)
    {
        var result = BinNameGenerator.Generate(prefix, suffix, type, start, end);
        Assert.Equal(expected, result);
    }

    // ====================================================================
    // Generate(): Fehler-Pfade
    // ====================================================================

    [Theory]
    [InlineData(BinSequenceType.Numbers, "10", "5")]   // Numbers: end < start
    [InlineData(BinSequenceType.Letters, "C", "A")]    // Letters: end < start
    public void Generate_throws_when_end_before_start(BinSequenceType type, string start, string end)
    {
        Assert.Throws<ArgumentException>(() =>
            BinNameGenerator.Generate("", "", type, start, end));
    }

    [Theory]
    [InlineData(BinSequenceType.Numbers, "abc", "10")]  // Numbers: nicht-numerisch
    [InlineData(BinSequenceType.Letters, "A1", "C")]    // Letters: Zahlen drin
    public void Generate_throws_when_invalid_input(BinSequenceType type, string start, string end)
    {
        Assert.Throws<ArgumentException>(() =>
            BinNameGenerator.Generate("", "", type, start, end));
    }

    // ====================================================================
    // IncrementAlpha
    // ====================================================================

    [Theory]
    [InlineData("",    "A")]
    [InlineData("A",   "B")]
    [InlineData("Y",   "Z")]
    [InlineData("Z",   "AA")]
    [InlineData("AZ",  "BA")]
    [InlineData("ZZ",  "AAA")]
    [InlineData("BZZ", "CAA")]
    public void IncrementAlpha(string input, string expected)
    {
        Assert.Equal(expected, BinNameGenerator.IncrementAlpha(input));
    }

    // ====================================================================
    // CountAlpha
    // ====================================================================

    [Theory]
    [InlineData("A", "A", 1)]
    [InlineData("A", "C", 3)]
    [InlineData("Z", "AB", 3)]
    [InlineData("C", "A", 0)]   // end < start -> 0
    public void CountAlpha(string start, string end, int expectedCount)
    {
        Assert.Equal(expectedCount, BinNameGenerator.CountAlpha(start, end));
    }

    // ====================================================================
    // CompareAlpha
    // ====================================================================

    [Theory]
    [InlineData("Z",  "AA",  -1)]   // kuerzer ist kleiner
    [InlineData("AA", "Z",    1)]
    [InlineData("AB", "AB",   0)]
    [InlineData("A",  "B",   -1)]
    public void CompareAlpha(string left, string right, int expectedSign)
    {
        var result = BinNameGenerator.CompareAlpha(left, right);
        Assert.Equal(expectedSign, Math.Sign(result));
    }
}
