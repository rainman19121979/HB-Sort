using HBSort.Core.Models;
using HBSort.Core.Models.Bricklink;
using HBSort.Core.Services;
using HBSort.ViewModels;

namespace HBSort.Tests;

/// <summary>
/// v0.1.24-beta.1 Phase 2a Tests fuer den 2-stufigen Wizard
/// (<see cref="CollectMinifigWizardViewModel"/>).
///
/// Pflicht-Szenarien aus Konzept 7.1:
///   W1: Alle Required-Parts via Reverse-Match verfuegbar
///   W2: Kein Reverse-Match
///   W7: Zurueck-Navigation erhaelt State
/// Plus IsManuallyClaimed-Toggle, MovementCount-Berechnung, CanSave-Logik.
/// </summary>
public class CollectMinifigWizardViewModelTests
{
    [Fact]
    public async Task W1_alle_required_parts_via_reverse_match()
    {
        // Alle 3 Required-Parts liegen im Pool. Trigger ist der scan, andere
        // 2 sind als InStorage klassifiziert.
        var catalog = new StubCatalog
        {
            MinifigDetails = NewItem("fig1", "Test Fig"),
            Subsets = new List<BlSubset>
            {
                new() { ItemNo = "TRIG", ColorId = 1, Quantity = 1 },
                new() { ItemNo = "P2", ColorId = 2, Quantity = 1 },
                new() { ItemNo = "P3", ColorId = 3, Quantity = 1 }
            }
        };
        var partLookup = new StubPartLookup();
        partLookup.LocationsByKey[("P2", 2)] = new() { new(10, "Box A", 1) };
        partLookup.LocationsByKey[("P3", 3)] = new() { new(11, "Box B", 1) };

        var vm = new CollectMinifigWizardViewModel(catalog, partLookup, new StubImageProvider());
        await vm.LoadAsync("fig1", "TRIG", 1);

        Assert.Equal(3, vm.Parts.Count);
        Assert.Equal(1, vm.TriggerPartCount);
        Assert.Equal(2, vm.InStoragePartCount);
        Assert.Equal(0, vm.MissingPartCount);

        var p2 = vm.Parts.Single(p => p.PartNumber == "P2");
        Assert.Equal(WizardPartStatus.InStorage, p2.Status);
        Assert.Equal("Box A", p2.FoundInBinLabel);
    }

    [Fact]
    public async Task W2_kein_reverse_match_zeigt_trigger_und_missing()
    {
        // Nur das Trigger-Teil ist da; rest = Missing mit Checkbox.
        var catalog = new StubCatalog
        {
            MinifigDetails = NewItem("fig2", "Pool-Less"),
            Subsets = new List<BlSubset>
            {
                new() { ItemNo = "TRIG", ColorId = 1, Quantity = 1 },
                new() { ItemNo = "MISS1", ColorId = 2, Quantity = 1 },
                new() { ItemNo = "MISS2", ColorId = 3, Quantity = 1 }
            }
        };

        var vm = new CollectMinifigWizardViewModel(catalog, new StubPartLookup(), new StubImageProvider());
        await vm.LoadAsync("fig2", "TRIG", 1);

        Assert.Equal(1, vm.TriggerPartCount);
        Assert.Equal(0, vm.InStoragePartCount);
        Assert.Equal(2, vm.MissingPartCount);

        var missing = vm.Parts.First(p => p.PartNumber == "MISS1");
        Assert.Equal(WizardPartStatus.Missing, missing.Status);
        Assert.True(missing.IsCheckBoxVisible);
        Assert.False(missing.IsManuallyClaimed);
        Assert.Equal("fehlt - bleibt wartend", missing.StatusText);
    }

    [Fact]
    public async Task W7_zurueck_navigation_erhaelt_state()
    {
        // Stufe 1 -> Stufe 2 -> Zurueck. IsManuallyClaimed-Toggle und
        // SelectedBin-Wahl muessen erhalten bleiben.
        var catalog = new StubCatalog
        {
            MinifigDetails = NewItem("fig3", "Stage Test"),
            Subsets = new List<BlSubset>
            {
                new() { ItemNo = "TRIG", ColorId = 1, Quantity = 1 },
                new() { ItemNo = "MISS", ColorId = 2, Quantity = 1 }
            }
        };

        var vm = new CollectMinifigWizardViewModel(catalog, new StubPartLookup(), new StubImageProvider());
        await vm.LoadAsync("fig3", "TRIG", 1);
        Assert.True(vm.IsStageOne);

        var missing = vm.Parts.Single(p => p.PartNumber == "MISS");
        missing.IsManuallyClaimed = true;

        vm.GoToStage2();
        Assert.True(vm.IsStageTwo);

        vm.GoToStage1();
        Assert.True(vm.IsStageOne);

        // State erhalten - Toggle bleibt, Parts.Count unveraendert.
        Assert.True(vm.Parts.Single(p => p.PartNumber == "MISS").IsManuallyClaimed);
        Assert.Equal(2, vm.Parts.Count);
    }

    [Fact]
    public async Task IsManuallyClaimed_toggle_updates_status_text_and_movement_count()
    {
        // Konzept 6.3: Toggle in Missing -> StatusText "manuell markiert" +
        // MovementCount erhoeht sich um 1.
        var catalog = new StubCatalog
        {
            MinifigDetails = NewItem("fig4", "Manuell"),
            Subsets = new List<BlSubset>
            {
                new() { ItemNo = "TRIG", ColorId = 1, Quantity = 1 },
                new() { ItemNo = "MISS", ColorId = 2, Quantity = 1 }
            }
        };

        var vm = new CollectMinifigWizardViewModel(catalog, new StubPartLookup(), new StubImageProvider());
        await vm.LoadAsync("fig4", "TRIG", 1);

        // Baseline: Trigger=1 + Figur=1 = 2 Bewegungen.
        Assert.Equal(2, vm.MovementCount);

        var missing = vm.Parts.Single(p => p.PartNumber == "MISS");
        missing.IsManuallyClaimed = true;

        Assert.Equal("manuell markiert", missing.StatusText);
        // Trigger + Manuell + Figur = 3 Bewegungen.
        Assert.Equal(3, vm.MovementCount);
    }

    [Fact]
    public async Task IsManuallyClaimed_toggle_off_decrements_movement_count()
    {
        // Toggle on -> off; MovementCount geht wieder runter.
        var catalog = new StubCatalog
        {
            MinifigDetails = NewItem("fig5", "Toggle"),
            Subsets = new List<BlSubset>
            {
                new() { ItemNo = "TRIG", ColorId = 1, Quantity = 1 },
                new() { ItemNo = "MISS", ColorId = 2, Quantity = 1 }
            }
        };

        var vm = new CollectMinifigWizardViewModel(catalog, new StubPartLookup(), new StubImageProvider());
        await vm.LoadAsync("fig5", "TRIG", 1);
        var missing = vm.Parts.Single(p => p.PartNumber == "MISS");

        missing.IsManuallyClaimed = true;
        Assert.Equal(3, vm.MovementCount);

        missing.IsManuallyClaimed = false;
        Assert.Equal(2, vm.MovementCount);
        Assert.Equal("fehlt - bleibt wartend", missing.StatusText);
    }

    [Fact]
    public async Task CanSave_false_when_no_bin_selected_true_when_bin_set()
    {
        var catalog = new StubCatalog
        {
            MinifigDetails = NewItem("fig6", "SaveLogic"),
            Subsets = new List<BlSubset>
            {
                new() { ItemNo = "TRIG", ColorId = 1, Quantity = 1 }
            }
        };

        var vm = new CollectMinifigWizardViewModel(catalog, new StubPartLookup(), new StubImageProvider());
        await vm.LoadAsync("fig6", "TRIG", 1);

        // Ohne BinService -> SelectedBin null, CanSave false.
        Assert.Null(vm.SelectedBin);
        Assert.False(vm.CanSave);

        // Manuell setzen -> CanSave true.
        // v0.1.24-beta.1 Phase 2b: SelectedBin ist BinDisplayItem.
        vm.SelectedBin = new HBSort.Helpers.BinDisplayItem(
            new StorageBin { Id = 1, Label = "X" }, "X");
        Assert.True(vm.CanSave);
    }

    [Fact]
    public async Task SelectedPartsForReverseMatch_includes_trigger_and_in_storage_not_missing()
    {
        // Service-Aufruf-Daten: Trigger + InStorage gehen in
        // consumePartsFromFloating. Missing (auch IsManuallyClaimed=true)
        // gehoert NICHT dorthin - das ist der separate Pfad.
        var catalog = new StubCatalog
        {
            MinifigDetails = NewItem("fig7", "Select"),
            Subsets = new List<BlSubset>
            {
                new() { ItemNo = "TRIG", ColorId = 1, Quantity = 1 },
                new() { ItemNo = "STORED", ColorId = 2, Quantity = 1 },
                new() { ItemNo = "MISS", ColorId = 3, Quantity = 1 }
            }
        };
        var partLookup = new StubPartLookup();
        partLookup.LocationsByKey[("STORED", 2)] = new() { new(5, "Box 5", 1) };

        var vm = new CollectMinifigWizardViewModel(catalog, partLookup, new StubImageProvider());
        await vm.LoadAsync("fig7", "TRIG", 1);

        // MISS manuell markieren - darf trotzdem nicht im ReverseMatch landen.
        vm.Parts.Single(p => p.PartNumber == "MISS").IsManuallyClaimed = true;

        var reverse = vm.SelectedPartsForReverseMatch;
        Assert.Equal(2, reverse.Count);
        Assert.Contains(reverse, x => x.PartNo == "TRIG");
        Assert.Contains(reverse, x => x.PartNo == "STORED");
        Assert.DoesNotContain(reverse, x => x.PartNo == "MISS");

        var manuell = vm.ManuellClaimedParts;
        Assert.Single(manuell);
        Assert.Equal("MISS", manuell[0].PartNo);
    }

    [Fact]
    public async Task BuildSortInstructionForSave_produces_take_put_with_plus_hint_on_manuell()
    {
        var catalog = new StubCatalog
        {
            MinifigDetails = NewItem("fig8", "Build"),
            Subsets = new List<BlSubset>
            {
                new() { ItemNo = "TRIG", ColorId = 1, Quantity = 1 },
                new() { ItemNo = "MISS", ColorId = 2, Quantity = 1 }
            }
        };
        var vm = new CollectMinifigWizardViewModel(catalog, new StubPartLookup(), new StubImageProvider());
        await vm.LoadAsync("fig8", "TRIG", 1);
        vm.MinifigName = "Build Test";

        // 1 manuell-markiert.
        vm.Parts.Single(p => p.PartNumber == "MISS").IsManuallyClaimed = true;

        var consumed = new List<ConsumedFloatingPartInfo>
        {
            new() { BlPartNo = "EXTRA", BlColorId = 7, PartName = "Extra", ColorName = "Red",
                    Quantity = 1, SourceBinLabel = "Box Source" }
        };

        var instruction = vm.BuildSortInstructionForSave(consumed, "Box Target", isFullyComplete: false);

        Assert.Single(instruction.Take);
        Assert.Equal("Box Source", instruction.Take[0].BinLabel);
        Assert.Single(instruction.Put);
        Assert.Equal("Box Target", instruction.Put[0].BinLabel);
        Assert.NotNull(instruction.PlusHint);
        Assert.Contains("manuell als 'vorhanden' markiert", instruction.PlusHint!);
    }

    [Fact]
    public async Task BuildSortInstructionForSave_no_plus_hint_without_manuell()
    {
        var catalog = new StubCatalog
        {
            MinifigDetails = NewItem("fig9", "NoManuell"),
            Subsets = new List<BlSubset>
            {
                new() { ItemNo = "TRIG", ColorId = 1, Quantity = 1 }
            }
        };
        var vm = new CollectMinifigWizardViewModel(catalog, new StubPartLookup(), new StubImageProvider());
        await vm.LoadAsync("fig9", "TRIG", 1);

        var instruction = vm.BuildSortInstructionForSave(
            new List<ConsumedFloatingPartInfo>(), "Box T", isFullyComplete: true);

        Assert.Null(instruction.PlusHint);
        Assert.Contains("komplett", instruction.HeaderText);
    }

    [Fact]
    public async Task BuildSortInstructionForSave_fully_complete_changes_header()
    {
        // isFullyComplete=true -> Header zeigt prominenten Komplett-Hinweis.
        var catalog = new StubCatalog
        {
            MinifigDetails = NewItem("figA", "Complete"),
            Subsets = new List<BlSubset>
            {
                new() { ItemNo = "TRIG", ColorId = 1, Quantity = 1 }
            }
        };
        var vm = new CollectMinifigWizardViewModel(catalog, new StubPartLookup(), new StubImageProvider());
        await vm.LoadAsync("figA", "TRIG", 1);
        vm.MinifigName = "Boba Fett";

        var i = vm.BuildSortInstructionForSave(
            new List<ConsumedFloatingPartInfo>(), "Box X", isFullyComplete: true);
        Assert.Equal("Figur 'Boba Fett' komplett angelegt", i.HeaderText);

        var i2 = vm.BuildSortInstructionForSave(
            new List<ConsumedFloatingPartInfo>(), "Box X", isFullyComplete: false);
        Assert.Equal("Operation erfolgreich", i2.HeaderText);
    }

    [Fact]
    public async Task Reload_clears_old_parts_no_memory_leak()
    {
        // LoadAsync zweimal -> Parts werden ersetzt, alte Subscriptions
        // duerfen nicht stehenbleiben (Memory-Hygiene).
        var catalog = new StubCatalog
        {
            MinifigDetails = NewItem("figB", "ReloadTest"),
            Subsets = new List<BlSubset>
            {
                new() { ItemNo = "TRIG", ColorId = 1, Quantity = 1 },
                new() { ItemNo = "OLD", ColorId = 2, Quantity = 1 }
            }
        };
        var vm = new CollectMinifigWizardViewModel(catalog, new StubPartLookup(), new StubImageProvider());
        await vm.LoadAsync("figB", "TRIG", 1);
        Assert.Equal(2, vm.Parts.Count);

        catalog.Subsets = new List<BlSubset>
        {
            new() { ItemNo = "TRIG", ColorId = 1, Quantity = 1 }
        };
        await vm.LoadAsync("figB", "TRIG", 1);
        Assert.Single(vm.Parts);
    }

    // ===== Helpers =====

    private static BlItem NewItem(string blId, string name) => new()
    {
        ItemType = "M",
        ItemNo = blId,
        Name = name,
        DataCompleteness = DataCompleteness.Full,
        FetchedAt = DateTime.UtcNow
    };

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

        public Task<BlItem?> GetPartDetailsAsync(string blPartNo, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<List<string>> FindWaitingMinifigsForPartAsync(string blPartNo, int blColorId,
            IEnumerable<string> waitingMinifigIds, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<BlCacheStats> GetCacheStatsAsync(CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task ClearCacheAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> ClearStaleAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BlSubset>> GetSupersetsAsync(string blPartNo, int blColorId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<List<BlColor>> GetKnownColorsAsync(string blPartNo, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<List<BlMinifigSubsetMatch>> FindMinifigsContainingPartAsync(string blPartNo,
            int blColorId, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private sealed class StubPartLookup : IPartLookupService
    {
        public Dictionary<(string, int), List<FloatingPartLocation>> LocationsByKey { get; } = new();

        public Task<Dictionary<(string PartNo, int ColorId), List<FloatingPartLocation>>>
            FindFloatingLocationsForManyAsync(
                IReadOnlyCollection<(string PartNo, int ColorId)> keys,
                CancellationToken ct = default)
        {
            var result = new Dictionary<(string, int), List<FloatingPartLocation>>();
            foreach (var key in keys)
            {
                if (LocationsByKey.TryGetValue(key, out var list) && list.Count > 0)
                    result[key] = list;
            }
            return Task.FromResult(result);
        }

        public Task<List<FloatingPartLocation>> FindFloatingLocationsAsync(
            string blPartNo, int blColorId, CancellationToken ct = default)
            => LocationsByKey.TryGetValue((blPartNo, blColorId), out var list)
                ? Task.FromResult(list)
                : Task.FromResult(new List<FloatingPartLocation>());

        public Task<PartLookupResult> LookupPartAsync(string blPartNo, int blColorId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<AssignPartResult> AssignPartToMinifigAsync(int trackedMinifigPartId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<bool> UnassignPartFromMinifigAsync(int trackedMinifigPartId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<FloatingPart> AddPartToFloatingAsync(string blPartNo, int blColorId, string partName,
            string colorName, int quantity, int storageBinId, string? brickognizeCategory = null,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<TrackedMinifig> CollectMinifigFromSupersetAsync(string blMinifigId, int storageBinId,
            string triggerPartNo, int triggerColorId, int triggerQuantity, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<CollectMinifigResult> CollectMinifigFromSupersetWithSelectionAsync(
            string blMinifigId, int storageBinId,
            string triggerPartNo, int triggerColorId, int triggerQuantity,
            IReadOnlyCollection<(string PartNo, int ColorId)> consumePartsFromFloating,
            IReadOnlyCollection<(string PartNo, int ColorId)>? manuellClaimedParts = null,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<bool> DeleteFloatingPartAsync(int floatingPartId, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private sealed class StubImageProvider : IPartImageProvider
    {
        public Task<string> GetImageFileAsync(BrickognizeItem item, int? bricklinkColorId, CancellationToken ct = default)
            => Task.FromResult(string.Empty);
        public Task<string> GetImageFileByBlAsync(string bricklinkType, string bricklinkId, int? bricklinkColorId, CancellationToken ct = default)
            => Task.FromResult(string.Empty);
    }
}
