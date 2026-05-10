using HBSort.Core.Models;
using HBSort.Core.Models.Bricklink;
using HBSort.Core.Services;
using HBSort.ViewModels;

namespace HBSort.Tests;

/// <summary>
/// UX X.32 v0.1.19-beta.4 Block D: Tests fuer den Auswahl-Dialog vor
/// "Diese Figur anlegen". Kern-Logik: Vor-Markierung Trigger + Reverse-
/// Match-Treffer, Filter-Liste fuer den Service.
/// </summary>
public class CollectMinifigSelectionViewModelTests
{
    [Fact]
    public async Task LoadAsync_marks_trigger_part_kept_and_disables_checkbox()
    {
        // Trigger-Teil "3001/11" liegt nicht im Pool, andere Teile auch nicht.
        // Trigger muss trotzdem markiert + Checkbox deaktiviert sein.
        var catalog = new StubCatalog();
        catalog.MinifigDetails = new BlItem
        {
            ItemType = "M", ItemNo = "fig1", Name = "Test Fig",
            DataCompleteness = DataCompleteness.Full, FetchedAt = DateTime.UtcNow
        };
        catalog.Subsets = new List<BlSubset>
        {
            new() { ItemNo = "3001", ColorId = 11, Quantity = 1 },
            new() { ItemNo = "3024", ColorId = 0, Quantity = 2 }
        };
        catalog.Colors = new List<BlColor>
        {
            new() { ColorId = 11, Name = "Black" },
            new() { ColorId = 0, Name = "(no color)" }
        };
        var partLookup = new StubPartLookup(); // keine Floating-Treffer
        var imageProvider = new StubImageProvider();

        var vm = new CollectMinifigSelectionViewModel(catalog, partLookup, imageProvider);
        await vm.LoadAsync("fig1", triggerPartNo: "3001", triggerColorId: 11);

        Assert.Equal(2, vm.Parts.Count);
        var trigger = vm.Parts.Single(p => p.PartNumber == "3001");
        Assert.True(trigger.IsTrigger);
        Assert.True(trigger.IsKept);
        Assert.False(trigger.IsCheckBoxEnabled);

        var other = vm.Parts.Single(p => p.PartNumber == "3024");
        Assert.False(other.IsTrigger);
        Assert.False(other.IsKept); // nicht im Pool, nicht vor-markiert
        Assert.True(other.IsCheckBoxEnabled);
    }

    [Fact]
    public async Task LoadAsync_pre_marks_parts_found_in_floating_pool()
    {
        var catalog = new StubCatalog();
        catalog.MinifigDetails = new BlItem
        {
            ItemType = "M", ItemNo = "fig2", Name = "Pool Fig",
            DataCompleteness = DataCompleteness.Full, FetchedAt = DateTime.UtcNow
        };
        catalog.Subsets = new List<BlSubset>
        {
            new() { ItemNo = "TRIG", ColorId = 5, Quantity = 1 },
            new() { ItemNo = "INPOOL", ColorId = 7, Quantity = 1 },
            new() { ItemNo = "MISSING", ColorId = 9, Quantity = 1 }
        };
        catalog.Colors = new List<BlColor>();
        var partLookup = new StubPartLookup();
        // Reverse-Match-Treffer: INPOOL/7 liegt in "Box 003".
        partLookup.LocationsByKey[("INPOOL", 7)] = new List<FloatingPartLocation>
        {
            new(StorageBinId: 42, StorageBinLabel: "Box 003", TotalQuantity: 1)
        };
        var imageProvider = new StubImageProvider();

        var vm = new CollectMinifigSelectionViewModel(catalog, partLookup, imageProvider);
        await vm.LoadAsync("fig2", triggerPartNo: "TRIG", triggerColorId: 5);

        var trigger = vm.Parts.Single(p => p.PartNumber == "TRIG");
        Assert.True(trigger.IsKept);

        var pool = vm.Parts.Single(p => p.PartNumber == "INPOOL");
        Assert.True(pool.IsKept);
        Assert.True(pool.IsCanBeReverseMatched);
        Assert.Equal("Box 003", pool.FoundInBinLabel);

        var missing = vm.Parts.Single(p => p.PartNumber == "MISSING");
        Assert.False(missing.IsKept);
        Assert.False(missing.IsCanBeReverseMatched);
    }

    [Fact]
    public async Task SelectedPartsForReverseMatch_returns_only_kept_pool_or_trigger_parts()
    {
        var catalog = new StubCatalog();
        catalog.MinifigDetails = new BlItem
        {
            ItemType = "M", ItemNo = "fig3", Name = "Mixed",
            DataCompleteness = DataCompleteness.Full, FetchedAt = DateTime.UtcNow
        };
        catalog.Subsets = new List<BlSubset>
        {
            new() { ItemNo = "TRIG", ColorId = 1, Quantity = 1 },
            new() { ItemNo = "POOL", ColorId = 2, Quantity = 1 },
            new() { ItemNo = "MISSING", ColorId = 3, Quantity = 1 }
        };
        catalog.Colors = new List<BlColor>();
        var partLookup = new StubPartLookup();
        partLookup.LocationsByKey[("POOL", 2)] = new List<FloatingPartLocation>
        {
            new(StorageBinId: 1, StorageBinLabel: "Box 001", TotalQuantity: 1)
        };

        var vm = new CollectMinifigSelectionViewModel(
            catalog, partLookup, new StubImageProvider());
        await vm.LoadAsync("fig3", "TRIG", 1);

        // User entscheidet: POOL doch nicht aus dem Lager nehmen.
        var pool = vm.Parts.Single(p => p.PartNumber == "POOL");
        pool.IsKept = false;

        var selection = vm.SelectedPartsForReverseMatch;

        // Trigger muss drin sein, POOL nicht (User abgewaehlt), MISSING nie.
        Assert.Single(selection);
        Assert.Equal("TRIG", selection[0].PartNo);
        Assert.Equal(1, selection[0].ColorId);
    }

    [Fact]
    public async Task SelectedPartsForReverseMatch_includes_trigger_even_if_only_trigger_kept()
    {
        // Edge-Case: User waehlt alle Pool-Treffer ab - Selektion = nur Trigger.
        var catalog = new StubCatalog();
        catalog.MinifigDetails = new BlItem
        {
            ItemType = "M", ItemNo = "fig4", Name = "Only-Trig",
            DataCompleteness = DataCompleteness.Full, FetchedAt = DateTime.UtcNow
        };
        catalog.Subsets = new List<BlSubset>
        {
            new() { ItemNo = "TRIG", ColorId = 1, Quantity = 1 },
            new() { ItemNo = "POOL1", ColorId = 2, Quantity = 1 },
            new() { ItemNo = "POOL2", ColorId = 3, Quantity = 1 }
        };
        catalog.Colors = new List<BlColor>();
        var partLookup = new StubPartLookup();
        partLookup.LocationsByKey[("POOL1", 2)] = new List<FloatingPartLocation>
        {
            new(1, "Box A", 1)
        };
        partLookup.LocationsByKey[("POOL2", 3)] = new List<FloatingPartLocation>
        {
            new(2, "Box B", 1)
        };

        var vm = new CollectMinifigSelectionViewModel(
            catalog, partLookup, new StubImageProvider());
        await vm.LoadAsync("fig4", "TRIG", 1);

        // User waehlt beide Pool-Treffer ab.
        vm.Parts.Single(p => p.PartNumber == "POOL1").IsKept = false;
        vm.Parts.Single(p => p.PartNumber == "POOL2").IsKept = false;

        var selection = vm.SelectedPartsForReverseMatch;

        Assert.Single(selection);
        Assert.Equal("TRIG", selection[0].PartNo);
    }

    // ===== Stubs =====

    /// <summary>
    /// Minimaler IBlCatalogService-Stub - nur die im VM benoetigten Methoden,
    /// alle anderen werfen NotImplementedException.
    /// </summary>
    private sealed class StubCatalog : IBlCatalogService
    {
        public BlItem? MinifigDetails { get; set; }
        public List<BlSubset> Subsets { get; set; } = new();
        public List<BlColor> Colors { get; set; } = new();

        public Task<BlItem?> GetMinifigDetailsAsync(string blMinifigId, CancellationToken ct = default)
            => Task.FromResult(MinifigDetails);

        public Task<List<BlSubset>> GetMinifigPartsAsync(string blMinifigId, CancellationToken ct = default)
            => Task.FromResult(Subsets);

        public Task<List<BlColor>> GetAllColorsAsync(CancellationToken ct = default)
            => Task.FromResult(Colors);

        public Task<bool> EnsureFullSubsetsAsync(string blMinifigId, CancellationToken ct = default)
            => Task.FromResult(true);

        // Restliche Methoden: nicht relevant fuer diese Tests.
        public Task<BlItem?> GetPartDetailsAsync(string blPartNo, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<List<string>> FindWaitingMinifigsForPartAsync(string blPartNo, int blColorId, IEnumerable<string> waitingMinifigIds, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<BlCacheStats> GetCacheStatsAsync(CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task ClearCacheAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> ClearStaleAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BlSubset>> GetSupersetsAsync(string blPartNo, int blColorId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<List<BlColor>> GetKnownColorsAsync(string blPartNo, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<List<BlMinifigSubsetMatch>> FindMinifigsContainingPartAsync(string blPartNo, int blColorId, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Minimaler IPartLookupService-Stub - nur FindFloatingLocationsAsync wird
    /// vom VM aufgerufen.
    /// </summary>
    private sealed class StubPartLookup : IPartLookupService
    {
        public Dictionary<(string, int), List<FloatingPartLocation>> LocationsByKey { get; }
            = new();

        public Task<List<FloatingPartLocation>> FindFloatingLocationsAsync(
            string blPartNo, int blColorId, CancellationToken ct = default)
        {
            if (LocationsByKey.TryGetValue((blPartNo, blColorId), out var list))
                return Task.FromResult(list);
            return Task.FromResult(new List<FloatingPartLocation>());
        }

        // Restliche Methoden: nicht relevant.
        public Task<PartLookupResult> LookupPartAsync(string blPartNo, int blColorId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<bool> AssignPartToMinifigAsync(int trackedMinifigPartId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<bool> UnassignPartFromMinifigAsync(int trackedMinifigPartId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<FloatingPart> AddPartToFloatingAsync(
            string blPartNo, int blColorId, string partName, string colorName,
            int quantity, int storageBinId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<TrackedMinifig> CollectMinifigFromSupersetAsync(
            string blMinifigId, int storageBinId,
            string triggerPartNo, int triggerColorId, int triggerQuantity,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<CollectMinifigResult> CollectMinifigFromSupersetWithSelectionAsync(
            string blMinifigId, int storageBinId,
            string triggerPartNo, int triggerColorId, int triggerQuantity,
            IReadOnlyCollection<(string PartNo, int ColorId)> consumePartsFromFloating,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<bool> DeleteFloatingPartAsync(int floatingPartId, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Minimaler IPartImageProvider-Stub - liefert leere Strings
    /// (Bilder sind fuer den Selection-Dialog optional).
    /// </summary>
    private sealed class StubImageProvider : IPartImageProvider
    {
        public Task<string> GetImageFileAsync(BrickognizeItem item, int? bricklinkColorId, CancellationToken ct = default)
            => Task.FromResult(string.Empty);

        public Task<string> GetImageFileByBlAsync(string bricklinkType, string bricklinkId, int? bricklinkColorId, CancellationToken ct = default)
            => Task.FromResult(string.Empty);
    }
}
