using HBSort.Core.Database;
using HBSort.Core.Models;
using HBSort.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HBSort.Tests;

/// <summary>
/// Tests fuer StorageBinService. Wie BsxExportServiceTests mit In-Memory-SQLite,
/// damit Constraints (Unique-Index auf Label, FK-Verhalten) realistisch getestet werden.
/// </summary>
public class StorageBinServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<UserDataContext> _factory;
    private readonly StorageBinService _sut;

    public StorageBinServiceTests()
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
        _sut = new StorageBinService(_factory);
    }

    public void Dispose() => _connection.Dispose();

    // ---- CreateSingle ----

    [Fact]
    public async Task CreateSingle_persists_bin_with_label_and_freedAt_null()
    {
        var bin = await _sut.CreateSingleAsync("Box 01");

        Assert.True(bin.Id > 0);
        Assert.Equal("Box 01", bin.Label);
        Assert.Null(bin.FreedAt);

        var fromDb = await _sut.GetByIdAsync(bin.Id);
        Assert.NotNull(fromDb);
        Assert.Equal("Box 01", fromDb!.Label);
    }

    [Fact]
    public async Task CreateSingle_throws_on_duplicate_label()
    {
        await _sut.CreateSingleAsync("Box 01");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.CreateSingleAsync("Box 01"));
    }

    [Fact]
    public async Task CreateSingle_throws_on_empty_label()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.CreateSingleAsync("   "));
    }

    // ---- CreateBulk ----

    [Fact]
    public async Task CreateBulk_creates_all_when_no_conflicts()
    {
        var result = await _sut.CreateBulkAsync(new[] { "Box 01", "Box 02", "Box 03" });

        Assert.Equal(3, result.Created.Count);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public async Task CreateBulk_skips_existing_labels_and_returns_them_as_conflicts()
    {
        await _sut.CreateSingleAsync("Box 02");

        var result = await _sut.CreateBulkAsync(new[] { "Box 01", "Box 02", "Box 03" });

        Assert.Equal(2, result.Created.Count);
        Assert.Single(result.Conflicts);
        Assert.Contains("Box 02", result.Conflicts);
    }

    [Fact]
    public async Task CreateBulk_skips_in_batch_duplicates()
    {
        // Innerhalb des Batches kommt "Box 01" zweimal -> zweite Vorkommen ist Konflikt.
        var result = await _sut.CreateBulkAsync(new[] { "Box 01", "Box 01", "Box 02" });

        Assert.Equal(2, result.Created.Count);
        Assert.Single(result.Conflicts);
    }

    // ---- Empty ----

    [Fact]
    public async Task Empty_detaches_minifigs_and_deletes_floating_parts_and_sets_FreedAt()
    {
        var bin = await _sut.CreateSingleAsync("Box 01");
        await SeedMinifigInBinAsync(bin.Id, "arc007", TrackedMinifigStatus.Waiting);
        await SeedFloatingPartInBinAsync(bin.Id, "3001", 5);

        var ok = await _sut.EmptyAsync(bin.Id);
        Assert.True(ok);

        // Bin: FreedAt gesetzt
        await using var ctx = await _factory.CreateDbContextAsync();
        var fromDb = await ctx.StorageBins.SingleAsync(b => b.Id == bin.Id);
        Assert.NotNull(fromDb.FreedAt);

        // Minifig: bleibt in DB, aber StorageBinId=null
        var mfg = await ctx.TrackedMinifigs.SingleAsync();
        Assert.Null(mfg.StorageBinId);

        // FloatingParts: weg
        Assert.Empty(await ctx.FloatingParts.ToListAsync());
    }

    [Fact]
    public async Task Empty_returns_false_when_bin_does_not_exist()
    {
        var ok = await _sut.EmptyAsync(9999);
        Assert.False(ok);
    }

    // ---- Delete ----

    [Fact]
    public async Task Delete_throws_when_bin_has_waiting_minifigs()
    {
        var bin = await _sut.CreateSingleAsync("Box 01");
        await SeedMinifigInBinAsync(bin.Id, "arc007", TrackedMinifigStatus.Waiting);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.DeleteAsync(bin.Id));
    }

    [Fact]
    public async Task Delete_throws_when_bin_has_floating_parts()
    {
        var bin = await _sut.CreateSingleAsync("Box 01");
        await SeedFloatingPartInBinAsync(bin.Id, "3001", 1);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.DeleteAsync(bin.Id));
    }

    [Fact]
    public async Task Delete_succeeds_when_only_completed_minifigs_remain()
    {
        var bin = await _sut.CreateSingleAsync("Box 01");
        await SeedMinifigInBinAsync(bin.Id, "arc007", TrackedMinifigStatus.Complete);

        var ok = await _sut.DeleteAsync(bin.Id);
        Assert.True(ok);

        // Bin geloescht, Minifig aber noch da (StorageBinId=null)
        await using var ctx = await _factory.CreateDbContextAsync();
        Assert.Empty(await ctx.StorageBins.ToListAsync());
        var m = await ctx.TrackedMinifigs.SingleAsync();
        Assert.Null(m.StorageBinId);
    }

    // ---- Free / NextFree ----

    [Fact]
    public async Task GetNextFree_returns_first_alphabetical_bin_without_waiting_or_floats()
    {
        await _sut.CreateBulkAsync(new[] { "Box 02", "Box 03", "Box 01" });
        // Box 01 mit wartender Figur belegen.
        var box01 = await _sut.GetByLabelAsync("Box 01");
        await SeedMinifigInBinAsync(box01!.Id, "arc007", TrackedMinifigStatus.Waiting);

        var next = await _sut.GetNextFreeAsync();
        Assert.NotNull(next);
        Assert.Equal("Box 02", next!.Label);
    }

    [Fact]
    public async Task GetFree_excludes_bins_with_floating_parts()
    {
        await _sut.CreateBulkAsync(new[] { "Box 01", "Box 02" });
        var b1 = await _sut.GetByLabelAsync("Box 01");
        await SeedFloatingPartInBinAsync(b1!.Id, "3001", 2);

        var free = await _sut.GetFreeAsync();
        Assert.Single(free);
        Assert.Equal("Box 02", free[0].Label);
    }

    // ---- FindBinsThatWouldBeEmpty + ReleaseBins (Phase 7) ----

    [Fact]
    public async Task FindBinsThatWouldBeEmpty_returns_bins_only_holding_to_be_removed_minifigs()
    {
        // Box 01: enthält nur die zu entfernende Figur -> waere leer
        // Box 02: enthält die zu entfernende Figur PLUS eine andere -> waere nicht leer
        // Box 03: enthält nur die zu entfernende Figur, aber ein FloatingPart -> waere nicht leer
        var b1 = await _sut.CreateSingleAsync("Box 01");
        var b2 = await _sut.CreateSingleAsync("Box 02");
        var b3 = await _sut.CreateSingleAsync("Box 03");

        var idMfg1 = await SeedMinifigInBinAsync(b1.Id, "fig1", TrackedMinifigStatus.Complete);
        var idMfg2 = await SeedMinifigInBinAsync(b2.Id, "fig2", TrackedMinifigStatus.Complete);
        await SeedMinifigInBinAsync(b2.Id, "fig3", TrackedMinifigStatus.Waiting);   // bleibt
        var idMfg4 = await SeedMinifigInBinAsync(b3.Id, "fig4", TrackedMinifigStatus.Complete);
        await SeedFloatingPartInBinAsync(b3.Id, "3001", 1);                          // bleibt

        var result = await _sut.FindBinsThatWouldBeEmptyAsync(new[] { idMfg1, idMfg2, idMfg4 });

        Assert.Single(result);
        Assert.Equal("Box 01", result[0].Label);
    }

    [Fact]
    public async Task FindBinsThatWouldBeEmpty_works_with_floating_part_ids()
    {
        // Bin enthaelt nur einen FloatingPart - wenn der entfernt wird, ist es leer.
        var b1 = await _sut.CreateSingleAsync("Box 01");
        await SeedFloatingPartInBinAsync(b1.Id, "3001", 5);

        await using var ctx = await _factory.CreateDbContextAsync();
        var fpId = (await ctx.FloatingParts.SingleAsync()).Id;

        var result = await _sut.FindBinsThatWouldBeEmptyAsync(
            Array.Empty<int>(), new[] { fpId });

        Assert.Single(result);
        Assert.Equal("Box 01", result[0].Label);
    }

    [Fact]
    public async Task FindBinsThatWouldBeEmpty_combines_minifig_and_floating_removal()
    {
        // Bin enthaelt EINE komplette Minifig UND EINEN FloatingPart -
        // beide muessen entfernt werden damit es leer wird.
        var b1 = await _sut.CreateSingleAsync("Box 01");
        var minifigId = await SeedMinifigInBinAsync(b1.Id, "arc007", TrackedMinifigStatus.Complete);
        await SeedFloatingPartInBinAsync(b1.Id, "3001", 1);

        await using var ctx = await _factory.CreateDbContextAsync();
        var fpId = (await ctx.FloatingParts.SingleAsync()).Id;

        // Nur Minifig entfernen -> Bin nicht leer (FloatingPart bleibt).
        var resultOnlyMfg = await _sut.FindBinsThatWouldBeEmptyAsync(
            new[] { minifigId }, Array.Empty<int>());
        Assert.Empty(resultOnlyMfg);

        // Nur FloatingPart entfernen -> Bin nicht leer (Minifig bleibt).
        var resultOnlyFp = await _sut.FindBinsThatWouldBeEmptyAsync(
            Array.Empty<int>(), new[] { fpId });
        Assert.Empty(resultOnlyFp);

        // Beides entfernen -> Bin leer.
        var resultBoth = await _sut.FindBinsThatWouldBeEmptyAsync(
            new[] { minifigId }, new[] { fpId });
        Assert.Single(resultBoth);
    }

    // ===== UX X.13c (User-Bug-Repro): 3 FloatingParts + 0 Minifigs =====
    // Genau das vom User per Screenshot gemeldete Szenario - drei Einzelteile
    // in einem Lagerfach, kein Minifig drin. Vor-Pruefung muss das Bin als
    // "wuerde leer" melden, sonst ist die UI-Checkbox ausgegraut.

    [Fact]
    public async Task FindBinsThatWouldBeEmpty_three_floatings_no_minifigs()
    {
        var bin = await _sut.CreateSingleAsync("Box 002");
        await SeedFloatingPartInBinAsync(bin.Id, "3001", 5);
        await SeedFloatingPartInBinAsync(bin.Id, "3024", 3);
        await SeedFloatingPartInBinAsync(bin.Id, "3622", 1);

        await using var ctx = await _factory.CreateDbContextAsync();
        var fpIds = await ctx.FloatingParts.Select(p => p.Id).ToListAsync();
        Assert.Equal(3, fpIds.Count);

        var result = await _sut.FindBinsThatWouldBeEmptyAsync(
            Array.Empty<int>(), fpIds);

        Assert.Single(result);
        Assert.Equal("Box 002", result[0].Label);
    }

    [Fact]
    public async Task FindBinsThatWouldBeEmpty_skips_bin_with_unrelated_floating_left_behind()
    {
        // Realistischer Edge-Case: 3 Einzelteile sollen exportiert werden,
        // aber im Bin liegt noch ein VIERTES Einzelteil das nicht in der
        // Export-Liste ist. Das Bin wird also NICHT leer.
        var bin = await _sut.CreateSingleAsync("Box 002");
        await SeedFloatingPartInBinAsync(bin.Id, "3001", 5);
        await SeedFloatingPartInBinAsync(bin.Id, "3024", 3);
        await SeedFloatingPartInBinAsync(bin.Id, "3622", 1);
        await SeedFloatingPartInBinAsync(bin.Id, "9999", 1);  // bleibt!

        await using var ctx = await _factory.CreateDbContextAsync();
        // Nur die ersten 3 in den Export-Pfad geben.
        var fpIds = await ctx.FloatingParts
            .Where(p => p.PartNumber != "9999")
            .Select(p => p.Id)
            .ToListAsync();
        Assert.Equal(3, fpIds.Count);

        var result = await _sut.FindBinsThatWouldBeEmptyAsync(
            Array.Empty<int>(), fpIds);

        Assert.Empty(result);
    }

    [Fact]
    public async Task FindBinsThatWouldBeEmpty_skips_bin_with_waiting_minifig()
    {
        // Box hat einen FloatingPart (wird exportiert) UND eine Wartende
        // Minifig (bleibt). Das Bin darf NICHT als leer gelten.
        var bin = await _sut.CreateSingleAsync("Box 002");
        await SeedFloatingPartInBinAsync(bin.Id, "3001", 5);
        await SeedMinifigInBinAsync(bin.Id, "arc007", TrackedMinifigStatus.Waiting);

        await using var ctx = await _factory.CreateDbContextAsync();
        var fpId = (await ctx.FloatingParts.SingleAsync()).Id;

        var result = await _sut.FindBinsThatWouldBeEmptyAsync(
            Array.Empty<int>(), new[] { fpId });

        Assert.Empty(result);
    }

    [Fact]
    public async Task End_to_end_release_after_floating_only_export_marks_bin_as_freed()
    {
        // Komplettes End-to-End fuer den User-Pfad:
        // 1) Box 002 mit 3 FloatingParts.
        // 2) Vor-Pruefung sagt: Bin wird leer.
        // 3) FloatingParts loeschen (simuliert RemoveExportedFloatingPartsAsync).
        // 4) ReleaseBins ruft.
        // 5) Bin in DB hat FreedAt != null.
        var bin = await _sut.CreateSingleAsync("Box 002");
        await SeedFloatingPartInBinAsync(bin.Id, "3001", 5);
        await SeedFloatingPartInBinAsync(bin.Id, "3024", 3);
        await SeedFloatingPartInBinAsync(bin.Id, "3622", 1);

        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            var fpIds = await ctx.FloatingParts.Select(p => p.Id).ToListAsync();

            // Schritt 2: Vor-Pruefung
            var preview = await _sut.FindBinsThatWouldBeEmptyAsync(
                Array.Empty<int>(), fpIds);
            Assert.Single(preview);
            Assert.Equal("Box 002", preview[0].Label);

            // Schritt 3: FloatingParts loeschen (vereinfachter Cleanup;
            // im Production-Code laeuft RemoveExportedFloatingPartsAsync).
            ctx.FloatingParts.RemoveRange(ctx.FloatingParts);
            await ctx.SaveChangesAsync();
        }

        // Schritt 4: ReleaseBins.
        var released = await _sut.ReleaseBinsAsync(new[] { bin.Id });
        Assert.Equal(1, released);

        // Schritt 5: FreedAt ist gesetzt.
        await using var ctx2 = await _factory.CreateDbContextAsync();
        var fromDb = await ctx2.StorageBins.SingleAsync(b => b.Id == bin.Id);
        Assert.NotNull(fromDb.FreedAt);
    }

    [Fact]
    public async Task ReleaseBins_sets_FreedAt_only_for_unreleased_bins()
    {
        var b1 = await _sut.CreateSingleAsync("Box 01");
        var b2 = await _sut.CreateSingleAsync("Box 02");
        // Box 02 vorab freigeben.
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            var x = await ctx.StorageBins.SingleAsync(b => b.Id == b2.Id);
            x.FreedAt = DateTime.UtcNow.AddHours(-1);
            await ctx.SaveChangesAsync();
        }

        var count = await _sut.ReleaseBinsAsync(new[] { b1.Id, b2.Id });

        Assert.Equal(1, count); // Box 02 war schon released, wurde nicht erneut angefasst
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.NotNull((await verify.StorageBins.SingleAsync(b => b.Id == b1.Id)).FreedAt);
    }

    // ---- Helpers ----

    private async Task<int> SeedMinifigInBinAsync(int binId, string blId, TrackedMinifigStatus status)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        var m = new TrackedMinifig
        {
            BricklinkId = blId,
            FigNum = blId,
            Name = blId,
            CreatedAt = DateTime.UtcNow,
            Status = status,
            StorageBinId = binId
        };
        ctx.TrackedMinifigs.Add(m);
        await ctx.SaveChangesAsync();
        return m.Id;
    }

    private async Task SeedFloatingPartInBinAsync(int binId, string partNo, int qty)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        ctx.FloatingParts.Add(new FloatingPart
        {
            PartNumber = partNo,
            ColorId = 0,
            ColorName = "Black",
            PartName = partNo,
            Quantity = qty,
            StorageBinId = binId,
            AddedAt = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();
    }

    private sealed class SimpleContextFactory : IDbContextFactory<UserDataContext>
    {
        private readonly DbContextOptions<UserDataContext> _options;
        public SimpleContextFactory(DbContextOptions<UserDataContext> options) => _options = options;
        public UserDataContext CreateDbContext() => new(_options);
    }
}
