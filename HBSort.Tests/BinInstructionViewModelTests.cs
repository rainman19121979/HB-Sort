using HBSort.ViewModels;

namespace HBSort.Tests;

/// <summary>
/// UX X.31 Block B (v0.1.18): Tests fuer das Anweisungs-Overlay-VM.
/// Bewusst klein gehalten - die UI-Animation/Timer wird nicht getestet.
/// </summary>
public class BinInstructionViewModelTests
{
    [Fact]
    public void IsVisible_defaults_to_false()
    {
        var vm = new BinInstructionViewModel();

        Assert.False(vm.IsVisible);
        Assert.Equal(string.Empty, vm.Label);
        Assert.Null(vm.ImageUrl);
    }

    [Fact]
    public void Show_sets_label_image_and_visibility()
    {
        var vm = new BinInstructionViewModel();

        vm.Show("Box 005", "C:/cache/arc007.png");

        Assert.True(vm.IsVisible);
        Assert.Equal("Box 005", vm.Label);
        Assert.Equal("C:/cache/arc007.png", vm.ImageUrl);
    }

    [Fact]
    public void Show_with_null_image_works()
    {
        var vm = new BinInstructionViewModel();

        vm.Show("Box 002", null);

        Assert.True(vm.IsVisible);
        Assert.Equal("Box 002", vm.Label);
        Assert.Null(vm.ImageUrl);
    }

    [Fact]
    public void Show_with_empty_label_is_no_op()
    {
        // Edge-Case: ohne Bin-Label macht das Overlay keinen Sinn ("Lege
        // diesen Eintrag in <leer>"). Bewusste No-Op damit ein leeres
        // Setup nicht versehentlich ein leeres Overlay zeigt.
        var vm = new BinInstructionViewModel();

        vm.Show("   ", "image.png");

        Assert.False(vm.IsVisible);
        Assert.Equal(string.Empty, vm.Label);
        Assert.Null(vm.ImageUrl);
    }

    [Fact]
    public void Dismiss_resets_visibility_but_keeps_last_label()
    {
        // Kein expliziter Reset von Label/ImageUrl - die Sichtbarkeit
        // entscheidet ueber das Overlay-Rendering, und der naechste Show
        // ueberschreibt die Felder ohnehin.
        var vm = new BinInstructionViewModel();
        vm.Show("Box 003", "img.png");

        vm.Dismiss();

        Assert.False(vm.IsVisible);
        Assert.Equal("Box 003", vm.Label);
        Assert.Equal("img.png", vm.ImageUrl);
    }

    [Fact]
    public void Show_after_dismiss_makes_overlay_visible_again()
    {
        var vm = new BinInstructionViewModel();
        vm.Show("Box A", "a.png");
        vm.Dismiss();
        Assert.False(vm.IsVisible);

        vm.Show("Box B", "b.png");

        Assert.True(vm.IsVisible);
        Assert.Equal("Box B", vm.Label);
        Assert.Equal("b.png", vm.ImageUrl);
    }

    [Fact]
    public void Show_fires_property_changed_for_visibility_and_label()
    {
        var vm = new BinInstructionViewModel();
        var changedProps = new List<string?>();
        vm.PropertyChanged += (_, e) => changedProps.Add(e.PropertyName);

        vm.Show("Box 99", "x.png");

        Assert.Contains(nameof(BinInstructionViewModel.IsVisible), changedProps);
        Assert.Contains(nameof(BinInstructionViewModel.Label), changedProps);
        Assert.Contains(nameof(BinInstructionViewModel.ImageUrl), changedProps);
    }
}
