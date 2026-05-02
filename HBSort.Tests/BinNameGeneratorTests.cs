using HBSort.Core.Services;

namespace HBSort.Tests;

/// <summary>Tests fuer den Bulk-Bin-Name-Generator.</summary>
public class BinNameGeneratorTests
{
    [Fact]
    public void Numbers_without_padding()
    {
        var result = BinNameGenerator.Generate("", "", BinSequenceType.Numbers, "1", "3");
        Assert.Equal(new[] { "1", "2", "3" }, result);
    }

    [Fact]
    public void Numbers_with_padding_3()
    {
        var result = BinNameGenerator.Generate("", "", BinSequenceType.Numbers, "1", "10", padding: 3);
        Assert.Equal(10, result.Count);
        Assert.Equal("001", result[0]);
        Assert.Equal("010", result[9]);
    }

    [Fact]
    public void Letters_simple()
    {
        var result = BinNameGenerator.Generate("", "", BinSequenceType.Letters, "A", "C");
        Assert.Equal(new[] { "A", "B", "C" }, result);
    }

    [Fact]
    public void Letters_Z_to_AB()
    {
        var result = BinNameGenerator.Generate("", "", BinSequenceType.Letters, "Z", "AB");
        Assert.Equal(new[] { "Z", "AA", "AB" }, result);
    }

    [Fact]
    public void Letters_three_letters_AAA_to_AAC()
    {
        var result = BinNameGenerator.Generate("", "", BinSequenceType.Letters, "AAA", "AAC");
        Assert.Equal(new[] { "AAA", "AAB", "AAC" }, result);
    }

    [Fact]
    public void Prefix_and_suffix_combine_correctly()
    {
        var result = BinNameGenerator.Generate("Box ", "-rot", BinSequenceType.Numbers, "1", "2");
        Assert.Equal(new[] { "Box 1-rot", "Box 2-rot" }, result);
    }

    [Theory]
    [InlineData("A", "B")]
    [InlineData("Z", "AA")]
    [InlineData("AZ", "BA")]
    [InlineData("ZZ", "AAA")]
    [InlineData("BZZ", "CAA")]
    public void IncrementAlpha_carries_over(string input, string expected)
    {
        Assert.Equal(expected, BinNameGenerator.IncrementAlpha(input));
    }

    [Fact]
    public void IncrementAlpha_empty_returns_A()
    {
        Assert.Equal("A", BinNameGenerator.IncrementAlpha(""));
    }

    [Fact]
    public void CountAlpha_includes_both_endpoints()
    {
        Assert.Equal(3, BinNameGenerator.CountAlpha("A", "C"));
        Assert.Equal(3, BinNameGenerator.CountAlpha("Z", "AB"));
        Assert.Equal(1, BinNameGenerator.CountAlpha("A", "A"));
    }

    [Fact]
    public void CountAlpha_zero_when_end_before_start()
    {
        Assert.Equal(0, BinNameGenerator.CountAlpha("C", "A"));
    }

    [Fact]
    public void Generate_throws_when_end_before_start()
    {
        Assert.Throws<ArgumentException>(() =>
            BinNameGenerator.Generate("", "", BinSequenceType.Numbers, "10", "5"));
        Assert.Throws<ArgumentException>(() =>
            BinNameGenerator.Generate("", "", BinSequenceType.Letters, "C", "A"));
    }

    [Fact]
    public void Generate_throws_when_invalid_input()
    {
        Assert.Throws<ArgumentException>(() =>
            BinNameGenerator.Generate("", "", BinSequenceType.Numbers, "abc", "10"));
        Assert.Throws<ArgumentException>(() =>
            BinNameGenerator.Generate("", "", BinSequenceType.Letters, "A1", "C"));
    }

    [Fact]
    public void Compare_alpha_shorter_is_smaller()
    {
        Assert.True(BinNameGenerator.CompareAlpha("Z", "AA") < 0);
        Assert.True(BinNameGenerator.CompareAlpha("AA", "Z") > 0);
        Assert.Equal(0, BinNameGenerator.CompareAlpha("AB", "AB"));
    }
}
