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

    // ====================================================================
    // UX X.32 v0.1.19-beta.5 (User-Befund Baubar-Popup): HeaderText
    // ====================================================================

    [Fact]
    public void HeaderText_defaults_to_default_constant()
    {
        var vm = new BinInstructionGroupViewModel();
        Assert.Equal(BinInstructionGroupViewModel.DefaultHeaderText, vm.HeaderText);
    }

    [Fact]
    public void Show_without_headerText_keeps_default()
    {
        var vm = new BinInstructionGroupViewModel();
        vm.Show(new[] { MakeItem("X", "Box 1", 1) });
        Assert.Equal(BinInstructionGroupViewModel.DefaultHeaderText, vm.HeaderText);
    }

    [Fact]
    public void Show_with_custom_headerText_applies_it()
    {
        var vm = new BinInstructionGroupViewModel();
        var custom = "Nimm folgende Teile aus den Faechern und lege die fertige Figur in das Ziel-Fach:";
        vm.Show(new[] { MakeItem("X", "Box 1", 1) }, custom);
        Assert.Equal(custom, vm.HeaderText);
    }

    [Fact]
    public void Show_with_empty_headerText_falls_back_to_default()
    {
        var vm = new BinInstructionGroupViewModel();
        vm.Show(new[] { MakeItem("X", "Box 1", 1) }, "   ");
        Assert.Equal(BinInstructionGroupViewModel.DefaultHeaderText, vm.HeaderText);
    }

    [Fact]
    public void Dismiss_resets_HeaderText_to_default()
    {
        var vm = new BinInstructionGroupViewModel();
        vm.Show(new[] { MakeItem("X", "Box 1", 1) }, "Custom Header");
        Assert.Equal("Custom Header", vm.HeaderText);

        vm.Dismiss();

        Assert.Equal(BinInstructionGroupViewModel.DefaultHeaderText, vm.HeaderText);
    }

    [Fact]
    public void Show_after_dismiss_with_new_header_applies_new_header()
    {
        var vm = new BinInstructionGroupViewModel();
        vm.Show(new[] { MakeItem("X", "Box 1", 1) }, "Erster Header");
        vm.Dismiss();
        vm.Show(new[] { MakeItem("Y", "Box 2", 1) }, "Zweiter Header");

        Assert.Equal("Zweiter Header", vm.HeaderText);
    }

    // ====================================================================
    // UX X.32 v0.1.19-beta.6 (User-Befund Item-Trennung): IsTargetItem
    // ====================================================================

    [Fact]
    public void BinInstructionItem_IsTargetItem_defaults_to_false()
    {
        var item = MakeItem("X", "Box 1", 1);
        Assert.False(item.IsTargetItem);
    }

    [Fact]
    public void BinInstructionItem_IsTargetItem_can_be_set_via_init()
    {
        var item = new BinInstructionItem
        {
            ItemLabel = "Lege fertige Figur in Ziel-Fach",
            BinLabel = "Box 5",
            QuantityText = "1 Stueck",
            IsTargetItem = true
        };
        Assert.True(item.IsTargetItem);
    }

    [Fact]
    public void BinInstructionItem_ImageUrl_can_be_updated_after_creation()
    {
        // Item-Liste wird mit ImageUrl=null erzeugt. Im UI-Layer wird das
        // Bild dann async ueber den IPartImageProvider nachgeladen und
        // per Setter aktualisiert. ObservableProperty -> UI bekommt
        // PropertyChanged + Bild erscheint im Overlay.
        var item = new BinInstructionItem
        {
            ItemLabel = "Test", BinLabel = "Box 1", QuantityText = "1 Stueck"
        };
        Assert.Null(item.ImageUrl);

        var changedProps = new List<string?>();
        item.PropertyChanged += (_, e) => changedProps.Add(e.PropertyName);

        item.ImageUrl = "C:/cache/3001.png";

        Assert.Equal("C:/cache/3001.png", item.ImageUrl);
        Assert.Contains(nameof(BinInstructionItem.ImageUrl), changedProps);
    }
}
