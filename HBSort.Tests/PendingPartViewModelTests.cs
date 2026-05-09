using HBSort.Core.Models.Bricklink;
using HBSort.ViewModels;

namespace HBSort.Tests;

/// <summary>
/// UX X.29 Block G (v0.1.16): Tests fuer die Default-Auswahl-Vorbelegung
/// in PendingPartViewModel.FromSubset.
/// </summary>
public class PendingPartViewModelTests
{
    [Fact]
    public void FromSubset_default_collected_false_initializes_QuantityCollected_to_zero()
    {
        var subset = MakeSubset(quantity: 3);
        var pending = PendingPartViewModel.FromSubset(subset, color: null);
        Assert.Equal(0, pending.QuantityCollected);
    }

    [Fact]
    public void FromSubset_default_collected_true_initializes_QuantityCollected_to_quantity()
    {
        var subset = MakeSubset(quantity: 3);
        var pending = PendingPartViewModel.FromSubset(
            subset, color: null, defaultCollected: true);
        Assert.Equal(3, pending.QuantityCollected);
    }

    [Fact]
    public void FromSubset_quantity_one_with_defaultCollected_marks_as_complete()
    {
        var subset = MakeSubset(quantity: 1);
        var pending = PendingPartViewModel.FromSubset(
            subset, color: null, defaultCollected: true);
        Assert.Equal(1, pending.QuantityCollected);
        Assert.True(pending.IsCollected);
    }

    [Fact]
    public void FromSubset_default_collected_false_default_param()
    {
        // Standard-Aufruf ohne Parameter -> defaultCollected=false (Default)
        var subset = MakeSubset(quantity: 5);
        var pending = PendingPartViewModel.FromSubset(subset, color: null, itemName: "x");
        Assert.Equal(0, pending.QuantityCollected);
        Assert.False(pending.IsCollected);
    }

    private static BlSubset MakeSubset(int quantity = 1)
        => new()
        {
            ItemNo = "3001",
            ColorId = 11,
            Quantity = quantity,
            ItemType = "P"
        };
}
