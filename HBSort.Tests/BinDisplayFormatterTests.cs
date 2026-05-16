using HBSort.Core.Models;
using HBSort.Core.Services;
using HBSort.Helpers;

namespace HBSort.Tests;

/// <summary>
/// v0.1.24-beta.1 Phase 2b (Konzept Befund B2 / OPEN-7): Unit-Tests fuer
/// <see cref="BinDisplayFormatter.FormatBinDisplayText"/>. Reine Logik-Tests
/// ohne DB-Setup — alle Eintraege werden in-memory gebaut.
/// </summary>
public class BinDisplayFormatterTests
{
    private static StorageBinWithCounts MakeCounts(
        string label, int waiting = 0, int complete = 0, int floating = 0)
    {
        var bin = new StorageBin { Id = 1, Label = label };
        return new StorageBinWithCounts(bin, waiting, complete, floating);
    }

    [Fact]
    public void Empty_bin_returns_label_without_suffix()
    {
        var b = MakeCounts("Box 005");
        Assert.Equal("Box 005", BinDisplayFormatter.FormatBinDisplayText(b));
    }

    [Fact]
    public void Only_waiting_renders_singular_suffix()
    {
        var b = MakeCounts("Box 005", waiting: 2);
        Assert.Equal("Box 005 (2 wartend)", BinDisplayFormatter.FormatBinDisplayText(b));
    }

    [Fact]
    public void Only_complete_renders_singular_suffix()
    {
        var b = MakeCounts("Box 005", complete: 3);
        Assert.Equal("Box 005 (3 fertig)", BinDisplayFormatter.FormatBinDisplayText(b));
    }

    [Fact]
    public void Waiting_plus_complete_renders_two_parts()
    {
        // Reifungspfad — Konzept-Beispiel.
        var b = MakeCounts("Box 005", waiting: 2, complete: 3);
        Assert.Equal(
            "Box 005 (2 wartend, 3 fertig)",
            BinDisplayFormatter.FormatBinDisplayText(b));
    }

    [Fact]
    public void Only_floating_renders_singular_suffix()
    {
        var b = MakeCounts("Box 005", floating: 5);
        Assert.Equal(
            "Box 005 (5 Einzelteile)",
            BinDisplayFormatter.FormatBinDisplayText(b));
    }

    [Fact]
    public void Waiting_plus_floating_renders_two_parts_in_correct_order()
    {
        // Reifungs-Edge-Case (wartende Figur teilt Bin mit Einzelteilen).
        var b = MakeCounts("Box 005", waiting: 2, floating: 5);
        Assert.Equal(
            "Box 005 (2 wartend, 5 Einzelteile)",
            BinDisplayFormatter.FormatBinDisplayText(b));
    }
}
