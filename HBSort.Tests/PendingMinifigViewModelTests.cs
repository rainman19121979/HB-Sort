using HBSort.ViewModels;

namespace HBSort.Tests;

/// <summary>
/// UX X.31 Block B Bug-Fix v2 (v0.1.18): Tests fuer die WillBeComplete-
/// Change-Notification-Kette. Beweist dass das ScanViewModel ueber
/// PendingMinifigViewModel.PropertyChanged auf "alle/keine Teile gesammelt"
/// reagieren kann - der Live-Bin-Re-Suggest haengt daran.
/// </summary>
public class PendingMinifigViewModelTests
{
    private static PendingPartViewModel MakePart(int quantity, int collected = 0)
    {
        return new PendingPartViewModel
        {
            BricklinkPartNo = $"part{quantity}",
            BricklinkColorId = 0,
            PartName = "Test",
            ColorName = "Black",
            Quantity = quantity,
            QuantityCollected = collected
        };
    }

    [Fact]
    public void WillBeComplete_is_false_when_no_parts_collected()
    {
        var vm = new PendingMinifigViewModel { NumParts = 2 };
        vm.Parts.Add(MakePart(1, 0));
        vm.Parts.Add(MakePart(2, 0));

        Assert.False(vm.WillBeComplete);
    }

    [Fact]
    public void WillBeComplete_is_true_when_all_parts_pre_collected()
    {
        // Setting "alles vorab abgehakt": jeder Part wird mit
        // QuantityCollected=Quantity erzeugt.
        var vm = new PendingMinifigViewModel { NumParts = 2 };
        vm.Parts.Add(MakePart(1, 1));
        vm.Parts.Add(MakePart(2, 2));

        Assert.True(vm.WillBeComplete);
    }

    [Fact]
    public void WillBeComplete_flips_to_true_when_user_marks_last_part()
    {
        // Setting "nichts vorab abgehakt" -> User klickt nacheinander an.
        var vm = new PendingMinifigViewModel { NumParts = 2 };
        var p1 = MakePart(1, 0);
        var p2 = MakePart(1, 0);
        vm.Parts.Add(p1);
        vm.Parts.Add(p2);
        Assert.False(vm.WillBeComplete);

        var changes = new List<string?>();
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        p1.IsCollected = true;
        Assert.False(vm.WillBeComplete); // erst eine markiert
        p2.IsCollected = true;
        Assert.True(vm.WillBeComplete);  // beide markiert -> Complete

        // PropertyChanged fuer WillBeComplete ist mind. einmal gefeuert worden.
        Assert.Contains(nameof(PendingMinifigViewModel.WillBeComplete), changes);
    }

    [Fact]
    public void WillBeComplete_flips_to_false_when_user_unmarks_one_part()
    {
        // Setting "alles vorab abgehakt" -> User entfernt einen Haken.
        var vm = new PendingMinifigViewModel { NumParts = 2 };
        var p1 = MakePart(1, 1);
        var p2 = MakePart(1, 1);
        vm.Parts.Add(p1);
        vm.Parts.Add(p2);
        Assert.True(vm.WillBeComplete);

        var changes = new List<string?>();
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        p1.IsCollected = false;

        Assert.False(vm.WillBeComplete);
        Assert.Contains(nameof(PendingMinifigViewModel.WillBeComplete), changes);
    }

    [Fact]
    public void WillBeComplete_re_toggles_cleanly_on_repeated_changes()
    {
        // Re-Toggle (markieren / entfernen / markieren) wechselt jedes Mal sauber.
        var vm = new PendingMinifigViewModel { NumParts = 1 };
        var p = MakePart(1, 0);
        vm.Parts.Add(p);

        Assert.False(vm.WillBeComplete);
        p.IsCollected = true;  Assert.True(vm.WillBeComplete);
        p.IsCollected = false; Assert.False(vm.WillBeComplete);
        p.IsCollected = true;  Assert.True(vm.WillBeComplete);
        p.IsCollected = false; Assert.False(vm.WillBeComplete);
    }

    [Fact]
    public void WillBeComplete_with_quantity_greater_one_only_completes_at_full_quantity()
    {
        // Quantity=3 - QuantityCollected muss 3 erreichen, nicht 1 oder 2.
        var vm = new PendingMinifigViewModel { NumParts = 1 };
        var p = MakePart(3, 0);
        vm.Parts.Add(p);

        p.QuantityCollected = 1; Assert.False(vm.WillBeComplete);
        p.QuantityCollected = 2; Assert.False(vm.WillBeComplete);
        p.QuantityCollected = 3; Assert.True(vm.WillBeComplete);
        p.QuantityCollected = 2; Assert.False(vm.WillBeComplete);
    }

    // ====================================================================
    // UX X.32 Block B (v0.1.19): HasAnyCollected (steuert "Direkt zerlegen")
    // ====================================================================

    [Fact]
    public void HasAnyCollected_is_false_when_no_parts()
    {
        var vm = new PendingMinifigViewModel { NumParts = 0 };
        Assert.False(vm.HasAnyCollected);
    }

    [Fact]
    public void HasAnyCollected_is_false_when_all_parts_uncollected()
    {
        var vm = new PendingMinifigViewModel { NumParts = 2 };
        vm.Parts.Add(MakePart(1, 0));
        vm.Parts.Add(MakePart(1, 0));
        Assert.False(vm.HasAnyCollected);
    }

    [Fact]
    public void HasAnyCollected_becomes_true_on_first_quantity_increase()
    {
        // Quantity=3 + Collected 0->1 aendert NICHT IsCollected, aber
        // HasAnyCollected geht false->true. Das beweist dass OnPartChanged
        // auch QuantityCollected (nicht nur IsCollected) trackt.
        var vm = new PendingMinifigViewModel { NumParts = 2 };
        var p1 = MakePart(3, 0);
        var p2 = MakePart(2, 0);
        vm.Parts.Add(p1);
        vm.Parts.Add(p2);
        Assert.False(vm.HasAnyCollected);

        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        p1.QuantityCollected = 1;

        Assert.True(vm.HasAnyCollected);
        Assert.Contains(nameof(PendingMinifigViewModel.HasAnyCollected), changed);
    }

    [Fact]
    public void HasAnyCollected_drops_back_to_false_when_last_collected_unset()
    {
        var vm = new PendingMinifigViewModel { NumParts = 1 };
        var p = MakePart(2, 1);
        vm.Parts.Add(p);
        Assert.True(vm.HasAnyCollected);

        p.QuantityCollected = 0;

        Assert.False(vm.HasAnyCollected);
    }

    [Fact]
    public void HasAnyCollected_remains_true_when_one_of_many_collected()
    {
        var vm = new PendingMinifigViewModel { NumParts = 3 };
        vm.Parts.Add(MakePart(1, 1));
        vm.Parts.Add(MakePart(1, 0));
        vm.Parts.Add(MakePart(1, 0));
        Assert.True(vm.HasAnyCollected);
    }
}
