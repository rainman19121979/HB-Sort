using HBSort.Core.Models;
using HBSort.Core.Services;
using HBSort.ViewModels;

namespace HBSort.Tests;

/// <summary>
/// UX X.25: Tests fuer die Multi-Mode-Logik in DismantlePartItemViewModel.
/// Pure VM-Logik ohne DI/DbContext - die DB-/Persistence-Pfade liegen in
/// MinifigPersistenceServiceTests.
/// </summary>
public class DismantlePartItemViewModelTests
{
    private static DismantlePartItemViewModel MakeItem(int id = 42)
        => new(new TrackedMinifigPart
        {
            Id = id,
            PartNumber = "3001",
            ColorId = 11,
            PartName = "Brick 2x4",
            ColorName = "Black",
            QuantityNeeded = 1,
            QuantityCollected = 0
        });

    [Fact]
    public void Default_mode_is_PutInBin()
    {
        var item = MakeItem();
        Assert.Equal(DismantlePartMode.PutInBin, item.Mode);
        Assert.True(item.IsPutInBinMode);
        Assert.False(item.IsAssignToWaitingMode);
    }

    [Fact]
    public void Default_no_matches_means_HasMatches_false()
    {
        var item = MakeItem();
        Assert.False(item.HasMatches);
        Assert.False(item.HasSingleMatch);
        Assert.False(item.HasMultipleMatches);
        Assert.Empty(item.WaitingMatches);
    }

    [Fact]
    public void SetMatches_with_one_match_populates_collection_and_selects_first()
    {
        var item = MakeItem();
        var match = new WaitingMinifigMatch(
            TrackedMinifigPartId: 7,
            TrackedMinifigId: 100,
            BlMinifigId: "cty0685",
            MinifigName: "Officer",
            MinifigImageUrl: null,
            StorageBinLabel: "Box 003",
            StorageBinId: 3,
            QuantityNeeded: 1,
            QuantityCollected: 0,
            IsAlternate: false);

        item.SetMatches(new[] { match });

        Assert.Single(item.WaitingMatches);
        Assert.True(item.HasMatches);
        Assert.True(item.HasSingleMatch);
        Assert.False(item.HasMultipleMatches);
        Assert.Equal(match, item.SelectedMatch);
    }

    [Fact]
    public void SetMatches_with_multiple_matches_sets_HasMultipleMatches_true()
    {
        var item = MakeItem();
        var matches = new[]
        {
            new WaitingMinifigMatch(1, 100, "fig1", "FigA", null, "Box 001", 1, 1, 0, false),
            new WaitingMinifigMatch(2, 200, "fig2", "FigB", null, "Box 002", 2, 1, 0, false),
            new WaitingMinifigMatch(3, 300, "fig3", "FigC", null, "Box 003", 3, 1, 0, false)
        };

        item.SetMatches(matches);

        Assert.Equal(3, item.WaitingMatches.Count);
        Assert.True(item.HasMatches);
        Assert.False(item.HasSingleMatch);
        Assert.True(item.HasMultipleMatches);
        Assert.Equal(matches[0], item.SelectedMatch); // erster ist Default
    }

    [Fact]
    public void SetMatches_replaces_existing_matches()
    {
        var item = MakeItem();
        var first = new WaitingMinifigMatch(1, 100, "fig1", "FigA", null, "Box 001", 1, 1, 0, false);
        var second = new WaitingMinifigMatch(2, 200, "fig2", "FigB", null, "Box 002", 2, 1, 0, false);

        item.SetMatches(new[] { first });
        item.SetMatches(new[] { second });

        Assert.Single(item.WaitingMatches);
        Assert.Equal(second, item.SelectedMatch);
    }

    [Fact]
    public void SingleMatchDisplay_formats_with_progress_and_bin()
    {
        var item = MakeItem();
        item.SetMatches(new[]
        {
            new WaitingMinifigMatch(7, 100, "cty0685", "Officer", null, "Box 003", 3, 7, 6, false)
        });

        // QuantityCollected+1 weil das Display zeigt was nach dem Assign kommen wird (6+1=7).
        Assert.Equal("Officer (7/7) [Box 003]", item.SingleMatchDisplay);
    }

    [Fact]
    public void SingleMatchDisplay_works_without_bin_label()
    {
        var item = MakeItem();
        item.SetMatches(new[]
        {
            new WaitingMinifigMatch(7, 100, "cty0685", "Officer", null, null, 0, 1, 0, false)
        });

        Assert.Equal("Officer (1/1)", item.SingleMatchDisplay);
    }

    [Fact]
    public void SingleMatchDisplay_empty_when_no_matches()
    {
        var item = MakeItem();
        Assert.Equal(string.Empty, item.SingleMatchDisplay);
    }

    [Fact]
    public void Mode_toggle_updates_computed_properties()
    {
        var item = MakeItem();

        item.Mode = DismantlePartMode.AssignToWaiting;
        Assert.False(item.IsPutInBinMode);
        Assert.True(item.IsAssignToWaitingMode);

        item.Mode = DismantlePartMode.PutInBin;
        Assert.True(item.IsPutInBinMode);
        Assert.False(item.IsAssignToWaitingMode);
    }

    [Fact]
    public void ModeGroupName_is_unique_per_item_id()
    {
        var a = MakeItem(id: 5);
        var b = MakeItem(id: 12);
        Assert.Equal("PartMode_5", a.ModeGroupName);
        Assert.Equal("PartMode_12", b.ModeGroupName);
        Assert.NotEqual(a.ModeGroupName, b.ModeGroupName);
    }
}
