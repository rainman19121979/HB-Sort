using HBSort.Core.Database;
using HBSort.Core.Models;
using HBSort.Core.Services;
using HBSort.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HBSort.Tests;

/// <summary>
/// UX X.32 Block B (v0.1.19): Tests fuer den Pending-Mode des
/// DismantleWizardViewModel - Direkt-Zerlegen einer noch nicht persistierten
/// Figur. ConfirmAsync legt FloatingParts direkt an, kein Figur-Lookup.
/// </summary>
public class DismantleWizardPendingModeTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<UserDataContext> _factory;
    private readonly StorageBinService _binService;
    private readonly MinifigPersistenceService _persistence;

    public DismantleWizardPendingModeTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<UserDataContext>()
            .UseSqlite(_connection)
            .Options;

        using (var ctx = new UserDataContext(options))
        {
            ctx.Database.EnsureCreated();
        }

        _factory = new SimpleContextFactory(options);
        _binService = new StorageBinService(_factory);
        _persistence = new MinifigPersistenceService(_factory);
    }

    public void Dispose() => _connection.Dispose();

    private DismantleWizardViewModel MakeWizard(int trackedMinifigId = 0)
        => new(trackedMinifigId, _factory, _binService, _persistence);

    private static PendingMinifigViewModel MakePending(string name = "TestFig")
    {
        return new PendingMinifigViewModel
        {
            BricklinkId = "arc007",
            Name = name,
            NumParts = 2
        };
    }

    private static PendingPartViewModel MakePendingPart(
        string partNo, int colorId, int quantity, int collected, string partName = "Brick")
    {
        return new PendingPartViewModel
        {
            BricklinkPartNo = partNo,
            BricklinkColorId = colorId,
            PartName = partName,
            ColorName = "Black",
            Quantity = quantity,
            QuantityCollected = collected
        };
    }

    [Fact]
    public void IsPendingMode_true_when_TrackedMinifigId_is_zero()
    {
        var wizard = MakeWizard(0);
        Assert.True(wizard.IsPendingMode);
    }

    [Fact]
    public void IsPendingMode_false_when_TrackedMinifigId_set()
    {
        var wizard = MakeWizard(42);
        Assert.False(wizard.IsPendingMode);
    }

    [Fact]
    public void FromPending_factory_copies_all_relevant_fields()
    {
        var p = MakePendingPart("3001", 11, quantity: 3, collected: 2, partName: "Brick 2x4");
        p.ImageUrl = "C:/cache/3001.png";

        var item = DismantlePartItemViewModel.FromPending(p);

        Assert.Equal(0, item.Id); // nicht persistiert
        Assert.Equal("3001", item.BlPartNo);
        Assert.Equal(11, item.BlColorId);
        Assert.Equal("Brick 2x4", item.PartName);
        Assert.Equal(3, item.QuantityNeeded);
        Assert.Equal(2, item.QuantityCollected);
        Assert.Equal("C:/cache/3001.png", item.ImageUrl);
    }

    [Fact]
    public async Task LoadFromPending_only_includes_collected_parts()
    {
        // Bin anlegen damit AvailableBins nicht leer ist.
        await _binService.CreateSingleAsync("Box 01");

        var pending = MakePending();
        pending.Parts.Add(MakePendingPart("3001", 11, 1, collected: 1));
        pending.Parts.Add(MakePendingPart("3024",  0, 2, collected: 0)); // nicht gesammelt
        pending.Parts.Add(MakePendingPart("3622", 11, 1, collected: 1));

        var wizard = MakeWizard(0);
        await wizard.LoadFromPendingAsync(pending);

        // Nur die gesammelten Teile landen im Wizard.
        Assert.Equal(2, wizard.Parts.Count);
        Assert.DoesNotContain(wizard.Parts, p => p.BlPartNo == "3024");
        Assert.All(wizard.Parts, p => Assert.True(p.IsKept));
    }

    [Fact]
    public async Task ConfirmAsync_PendingMode_creates_floating_parts_in_target_bin()
    {
        var binId = (await _binService.CreateSingleAsync("Box 01")).Id;

        var pending = MakePending();
        pending.Parts.Add(MakePendingPart("3001", 11, 2, collected: 2));

        var wizard = MakeWizard(0);
        await wizard.LoadFromPendingAsync(pending);
        // Der Suggest setzt das Bin auf das einzige verfuegbare ("Box 01").
        Assert.Single(wizard.Parts);
        Assert.NotNull(wizard.Parts[0].TargetBin);

        var result = await wizard.ConfirmAsync();

        Assert.True(result.Success);
        Assert.Equal(1, result.CreatedFloatingParts);
        Assert.Equal(2, result.TotalPartsTransferred);

        // FloatingPart ist in der DB, im Bin "Box 01".
        await using var ctx = await _factory.CreateDbContextAsync();
        var fps = await ctx.FloatingParts.ToListAsync();
        Assert.Single(fps);
        Assert.Equal("3001", fps[0].PartNumber);
        Assert.Equal(11, fps[0].ColorId);
        Assert.Equal(2, fps[0].Quantity);
        Assert.Equal(binId, fps[0].StorageBinId);
    }

    [Fact]
    public async Task ConfirmAsync_PendingMode_aggregates_into_existing_floating_part()
    {
        var binId = (await _binService.CreateSingleAsync("Box 01")).Id;
        // Schon 3 Stueck "3001/11" im Bin liegen.
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            ctx.FloatingParts.Add(new FloatingPart
            {
                PartNumber = "3001", ColorId = 11, PartName = "Brick", ColorName = "Black",
                Quantity = 3, StorageBinId = binId, AddedAt = DateTime.UtcNow.AddDays(-1)
            });
            await ctx.SaveChangesAsync();
        }

        var pending = MakePending();
        pending.Parts.Add(MakePendingPart("3001", 11, 2, collected: 2));

        var wizard = MakeWizard(0);
        await wizard.LoadFromPendingAsync(pending);
        await wizard.ConfirmAsync();

        // Bestehender Eintrag sollte additiv erhoeht sein (3+2=5), kein neuer Eintrag.
        await using var verify = await _factory.CreateDbContextAsync();
        var fps = await verify.FloatingParts.ToListAsync();
        Assert.Single(fps);
        Assert.Equal(5, fps[0].Quantity);
    }

    [Fact]
    public async Task ConfirmAsync_PendingMode_resets_FreedAt_on_target_bin()
    {
        var binId = (await _binService.CreateSingleAsync("Box 01")).Id;
        // Bin als frei markieren (UX-X.6-Konvention: Bin bleibt belegt
        // bis explizit als frei markiert).
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            var bin = await ctx.StorageBins.FirstAsync();
            bin.FreedAt = DateTime.UtcNow.AddDays(-2);
            await ctx.SaveChangesAsync();
        }

        var pending = MakePending();
        pending.Parts.Add(MakePendingPart("3001", 11, 1, collected: 1));

        var wizard = MakeWizard(0);
        await wizard.LoadFromPendingAsync(pending);
        await wizard.ConfirmAsync();

        // Nach dem Lagern muss FreedAt = null sein (Fach ist wieder belegt).
        await using var verify = await _factory.CreateDbContextAsync();
        var bin2 = await verify.StorageBins.FirstAsync();
        Assert.Null(bin2.FreedAt);
    }

    [Fact]
    public async Task ConfirmAsync_PendingMode_populates_LastBinInstructionItems()
    {
        await _binService.CreateSingleAsync("Box 01");

        var pending = MakePending();
        pending.Parts.Add(MakePendingPart("3001", 11, 2, collected: 2, partName: "Brick 2x4"));
        pending.Parts.Add(MakePendingPart("3024",  0, 1, collected: 1, partName: "Plate 1x1"));

        var wizard = MakeWizard(0);
        await wizard.LoadFromPendingAsync(pending);
        await wizard.ConfirmAsync();

        Assert.Equal(2, wizard.LastBinInstructionItems.Count);
        Assert.All(wizard.LastBinInstructionItems,
            i => Assert.Equal("Box 01", i.BinLabel));
        Assert.Contains(wizard.LastBinInstructionItems,
            i => i.ItemLabel.Contains("Brick 2x4") && i.QuantityText.Contains("2"));
        Assert.Contains(wizard.LastBinInstructionItems,
            i => i.ItemLabel.Contains("Plate 1x1") && i.QuantityText.Contains("1"));
    }

    [Fact]
    public async Task ConfirmAsync_PendingMode_empty_keep_returns_zero_results()
    {
        await _binService.CreateSingleAsync("Box 01");

        var wizard = MakeWizard(0);
        await wizard.LoadFromPendingAsync(MakePending()); // keine Parts
        var result = await wizard.ConfirmAsync();

        Assert.True(result.Success);
        Assert.Equal(0, result.CreatedFloatingParts);
        Assert.Empty(wizard.LastBinInstructionItems);
    }

    private sealed class SimpleContextFactory : IDbContextFactory<UserDataContext>
    {
        private readonly DbContextOptions<UserDataContext> _options;
        public SimpleContextFactory(DbContextOptions<UserDataContext> options) => _options = options;
        public UserDataContext CreateDbContext() => new(_options);
    }
}
