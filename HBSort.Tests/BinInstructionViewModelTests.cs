using HBSort.ViewModels;

namespace HBSort.Tests;

/// <summary>
/// Tests fuer das konsolidierte Anweisungs-Overlay-VM (v0.1.22-beta.1
/// Block F). VM hat Mode-Switch zwischen Single (Bin + Bild) und Group
/// (Items-Liste). Die UI-Animation/Timer wird nicht getestet - bewusst
/// schmal gehalten.
/// </summary>
public class BinInstructionViewModelTests
{
    [Fact]
    public void IsVisible_defaults_to_false()
    {
        var vm = new BinInstructionViewModel();

        Assert.False(vm.IsVisible);
        Assert.Equal(BinInstructionMode.Single, vm.Mode);
        Assert.Equal(string.Empty, vm.Label);
        Assert.Null(vm.ImageUrl);
        Assert.Empty(vm.Items);
        Assert.Equal(BinInstructionViewModel.DefaultHeaderText, vm.HeaderText);
    }

    [Fact]
    public void ShowSingle_sets_label_image_mode_and_visibility()
    {
        var vm = new BinInstructionViewModel();

        vm.ShowSingle("Box 005", "C:/cache/arc007.png");

        Assert.True(vm.IsVisible);
        Assert.Equal(BinInstructionMode.Single, vm.Mode);
        Assert.Equal("Box 005", vm.Label);
        Assert.Equal("C:/cache/arc007.png", vm.ImageUrl);
    }

    [Fact]
    public void ShowSingle_with_null_image_works()
    {
        var vm = new BinInstructionViewModel();

        vm.ShowSingle("Box 002", null);

        Assert.True(vm.IsVisible);
        Assert.Equal("Box 002", vm.Label);
        Assert.Null(vm.ImageUrl);
    }

    [Fact]
    public void ShowSingle_with_empty_label_is_no_op()
    {
        // Edge-Case: ohne Bin-Label macht das Overlay keinen Sinn.
        var vm = new BinInstructionViewModel();

        vm.ShowSingle("   ", "image.png");

        Assert.False(vm.IsVisible);
        Assert.Equal(string.Empty, vm.Label);
        Assert.Null(vm.ImageUrl);
    }

    [Fact]
    public void Dismiss_resets_visibility_and_state()
    {
        // v0.1.22-beta.1 Block F: Dismiss raeumt jetzt alle Felder auf
        // (vorher blieb Label/ImageUrl erhalten). Konsistent zu Group-Mode
        // der seit jeher Items.Clear() macht.
        var vm = new BinInstructionViewModel();
        vm.ShowSingle("Box 003", "img.png");

        vm.Dismiss();

        Assert.False(vm.IsVisible);
        Assert.Equal(string.Empty, vm.Label);
        Assert.Null(vm.ImageUrl);
    }

    [Fact]
    public void ShowSingle_after_dismiss_makes_overlay_visible_again()
    {
        var vm = new BinInstructionViewModel();
        vm.ShowSingle("Box A", "a.png");
        vm.Dismiss();
        Assert.False(vm.IsVisible);

        vm.ShowSingle("Box B", "b.png");

        Assert.True(vm.IsVisible);
        Assert.Equal("Box B", vm.Label);
        Assert.Equal("b.png", vm.ImageUrl);
    }

    [Fact]
    public void ShowSingle_fires_property_changed_for_visibility_and_label()
    {
        var vm = new BinInstructionViewModel();
        var changedProps = new List<string?>();
        vm.PropertyChanged += (_, e) => changedProps.Add(e.PropertyName);

        vm.ShowSingle("Box 99", "x.png");

        Assert.Contains(nameof(BinInstructionViewModel.IsVisible), changedProps);
        Assert.Contains(nameof(BinInstructionViewModel.Label), changedProps);
        Assert.Contains(nameof(BinInstructionViewModel.ImageUrl), changedProps);
    }

    // ========================================================================
    // Group-Mode (vorher BinInstructionGroupViewModel - in F konsolidiert)
    // ========================================================================

    [Fact]
    public void ShowGroup_populates_items_and_visibility()
    {
        var vm = new BinInstructionViewModel();
        var items = new[]
        {
            new BinInstructionItem { ItemLabel = "Helm",    QuantityText = "1x", BinLabel = "Box 005" },
            new BinInstructionItem { ItemLabel = "Torso",   QuantityText = "1x", BinLabel = "Box 007" },
        };

        vm.ShowGroup(items);

        Assert.True(vm.IsVisible);
        Assert.Equal(BinInstructionMode.Group, vm.Mode);
        Assert.Equal(2, vm.Items.Count);
        Assert.Equal(BinInstructionViewModel.DefaultHeaderText, vm.HeaderText);
    }

    [Fact]
    public void ShowGroup_with_custom_header_uses_it()
    {
        var vm = new BinInstructionViewModel();
        var items = new[] { new BinInstructionItem { ItemLabel = "A", QuantityText = "1x", BinLabel = "Box A" } };

        vm.ShowGroup(items, "Nimm folgende Teile heraus:");

        Assert.Equal("Nimm folgende Teile heraus:", vm.HeaderText);
    }

    [Fact]
    public void ShowGroup_with_empty_list_is_no_op()
    {
        var vm = new BinInstructionViewModel();

        vm.ShowGroup(System.Array.Empty<BinInstructionItem>());

        Assert.False(vm.IsVisible);
        Assert.Empty(vm.Items);
    }

    [Fact]
    public void ShowGroup_replaces_previous_items()
    {
        var vm = new BinInstructionViewModel();
        vm.ShowGroup(new[]
        {
            new BinInstructionItem { ItemLabel = "Alt",     QuantityText = "1x", BinLabel = "Box 1" },
        });

        vm.ShowGroup(new[]
        {
            new BinInstructionItem { ItemLabel = "Neu",     QuantityText = "1x", BinLabel = "Box 2" },
            new BinInstructionItem { ItemLabel = "Auch neu",QuantityText = "1x", BinLabel = "Box 3" },
        });

        Assert.Equal(2, vm.Items.Count);
        Assert.DoesNotContain(vm.Items, i => i.ItemLabel == "Alt");
    }

    [Fact]
    public void Dismiss_after_group_resets_header_and_items()
    {
        var vm = new BinInstructionViewModel();
        vm.ShowGroup(new[]
        {
            new BinInstructionItem { ItemLabel = "X", QuantityText = "1x", BinLabel = "Box X" },
        }, "Custom Header");

        vm.Dismiss();

        Assert.False(vm.IsVisible);
        Assert.Empty(vm.Items);
        Assert.Equal(BinInstructionViewModel.DefaultHeaderText, vm.HeaderText);
    }

    [Fact]
    public void Switching_from_single_to_group_clears_single_state()
    {
        var vm = new BinInstructionViewModel();
        vm.ShowSingle("Box 005", "img.png");

        vm.ShowGroup(new[]
        {
            new BinInstructionItem { ItemLabel = "Teil", QuantityText = "1x", BinLabel = "Box 999" },
        });

        Assert.Equal(BinInstructionMode.Group, vm.Mode);
        Assert.Equal(string.Empty, vm.Label);
        Assert.Null(vm.ImageUrl);
    }

    [Fact]
    public void BinInstructionItem_IsTargetItem_defaults_to_false()
    {
        var item = new BinInstructionItem();
        Assert.False(item.IsTargetItem);
    }

    [Fact]
    public void BinInstructionItem_ImageUrl_can_be_updated_after_creation()
    {
        var item = new BinInstructionItem
        {
            ItemLabel = "Helm", QuantityText = "1x", BinLabel = "Box 1"
        };
        var changedProps = new List<string?>();
        item.PropertyChanged += (_, e) => changedProps.Add(e.PropertyName);

        item.ImageUrl = "C:/cached/img.png";

        Assert.Equal("C:/cached/img.png", item.ImageUrl);
        Assert.Contains(nameof(BinInstructionItem.ImageUrl), changedProps);
    }
}
