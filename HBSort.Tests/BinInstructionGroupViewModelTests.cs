using HBSort.ViewModels;

namespace HBSort.Tests;

/// <summary>
/// UX X.32 Block C (v0.1.19): Tests fuer das Sammel-Popup-VM.
/// Show/Dismiss-Lifecycle + Items-Verwaltung. Kein Auto-Dismiss-Timer.
/// </summary>
public class BinInstructionGroupViewModelTests
{
    private static BinInstructionItem MakeItem(string label, string bin, int qty)
        => new() { ItemLabel = label, BinLabel = bin, QuantityText = $"{qty} Stueck" };

    [Fact]
    public void IsVisible_defaults_to_false_and_items_empty()
    {
        var vm = new BinInstructionGroupViewModel();

        Assert.False(vm.IsVisible);
        Assert.Empty(vm.Items);
    }

    [Fact]
    public void Show_sets_visibility_and_items()
    {
        var vm = new BinInstructionGroupViewModel();
        var items = new[]
        {
            MakeItem("Brick 2x4 - Black", "Box 003", 2),
            MakeItem("Plate 1x1 - Red", "Box 005", 1)
        };

        vm.Show(items);

        Assert.True(vm.IsVisible);
        Assert.Equal(2, vm.Items.Count);
        Assert.Equal("Brick 2x4 - Black", vm.Items[0].ItemLabel);
    }

    [Fact]
    public void Show_with_empty_list_is_no_op()
    {
        var vm = new BinInstructionGroupViewModel();
        vm.Show(Array.Empty<BinInstructionItem>());

        Assert.False(vm.IsVisible);
        Assert.Empty(vm.Items);
    }

    [Fact]
    public void Show_replaces_previous_items()
    {
        var vm = new BinInstructionGroupViewModel();
        vm.Show(new[] { MakeItem("Old", "Box 1", 1) });
        vm.Show(new[]
        {
            MakeItem("New A", "Box 2", 2),
            MakeItem("New B", "Box 3", 3)
        });

        Assert.True(vm.IsVisible);
        Assert.Equal(2, vm.Items.Count);
        Assert.Equal("New A", vm.Items[0].ItemLabel);
    }

    [Fact]
    public void Dismiss_clears_visibility_and_items()
    {
        var vm = new BinInstructionGroupViewModel();
        vm.Show(new[]
        {
            MakeItem("X", "Box 1", 1),
            MakeItem("Y", "Box 2", 1)
        });

        vm.Dismiss();

        Assert.False(vm.IsVisible);
        Assert.Empty(vm.Items);
    }

    [Fact]
    public void Show_after_dismiss_works_again()
    {
        var vm = new BinInstructionGroupViewModel();
        vm.Show(new[] { MakeItem("First", "Box 1", 1) });
        vm.Dismiss();

        vm.Show(new[] { MakeItem("Second", "Box 2", 2) });

        Assert.True(vm.IsVisible);
        Assert.Single(vm.Items);
        Assert.Equal("Second", vm.Items[0].ItemLabel);
    }

    [Fact]
    public void Show_fires_property_changed_for_visibility()
    {
        var vm = new BinInstructionGroupViewModel();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.Show(new[] { MakeItem("X", "Box 1", 1) });

        Assert.Contains(nameof(BinInstructionGroupViewModel.IsVisible), changed);
    }

    [Fact]
    public void Dismiss_fires_property_changed_for_visibility()
    {
        var vm = new BinInstructionGroupViewModel();
        vm.Show(new[] { MakeItem("X", "Box 1", 1) });

        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.Dismiss();

        Assert.Contains(nameof(BinInstructionGroupViewModel.IsVisible), changed);
    }
}
