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

    // ====================================================================
    // UX X.31 (v0.1.18) - Bin-Vorschlag-Konsistenz
    // ====================================================================

    [Fact]
    public async Task SuggestBinForWaitingMinifig_skips_bin_with_complete_minifig()
    {
        // Bug-Repro: GetFreeAsync wertet ein Fach mit nur Complete-Figuren als
        // "frei" - das war der Hauptbefund. SuggestBin* muss Complete-Figuren
        // strikt als belegend werten (UX-X.6-Konvention).
        await _sut.CreateBulkAsync(new[] { "Box 01", "Box 02" });
        var b1 = await _sut.GetByLabelAsync("Box 01");
        await SeedMinifigInBinAsync(b1!.Id, "arc007", TrackedMinifigStatus.Complete);

        var suggestion = await _sut.SuggestBinForWaitingMinifigAsync();

        Assert.NotNull(suggestion);
        Assert.Equal("Box 02", suggestion!.Label);
    }

    [Fact]
    public async Task SuggestBinForWaitingMinifig_skips_bin_with_floating_part()
    {
        await _sut.CreateBulkAsync(new[] { "Box 01", "Box 02" });
        var b1 = await _sut.GetByLabelAsync("Box 01");
        await SeedFloatingPartInBinAsync(b1!.Id, "3001", 1);

        var suggestion = await _sut.SuggestBinForWaitingMinifigAsync();

        Assert.NotNull(suggestion);
        Assert.Equal("Box 02", suggestion!.Label);
    }

    [Fact]
    public async Task SuggestBinForWaitingMinifig_skips_bin_with_waiting_minifig()
    {
        await _sut.CreateBulkAsync(new[] { "Box 01", "Box 02" });
        var b1 = await _sut.GetByLabelAsync("Box 01");
        await SeedMinifigInBinAsync(b1!.Id, "arc007", TrackedMinifigStatus.Waiting);

        var suggestion = await _sut.SuggestBinForWaitingMinifigAsync();

        Assert.NotNull(suggestion);
        Assert.Equal("Box 02", suggestion!.Label);
    }

    [Fact]
    public async Task SuggestBinForWaitingMinifig_picks_completely_empty_bin()
    {
        await _sut.CreateBulkAsync(new[] { "Box 01", "Box 02" });

        var suggestion = await _sut.SuggestBinForWaitingMinifigAsync();

        Assert.NotNull(suggestion);
        Assert.Equal("Box 01", suggestion!.Label);
    }

    [Fact]
    public async Task SuggestBinForWaitingMinifig_returns_null_when_all_blocked()
    {
        await _sut.CreateBulkAsync(new[] { "Box 01", "Box 02" });
        var b1 = await _sut.GetByLabelAsync("Box 01");
        var b2 = await _sut.GetByLabelAsync("Box 02");
        await SeedMinifigInBinAsync(b1!.Id, "arc007", TrackedMinifigStatus.Complete);
        await SeedMinifigInBinAsync(b2!.Id, "cty0685", TrackedMinifigStatus.Waiting);

        var suggestion = await _sut.SuggestBinForWaitingMinifigAsync();

        Assert.Null(suggestion);
    }

    // ===== UX X.32 v0.1.19-beta.4: maxWaitingLimit > 1 =====

    [Fact]
    public async Task SuggestBinForWaitingMinifig_limit3_with_existing_waiting_picks_stack_bin()
    {
        // Limit=3, Box 01 hat schon 1 wartende Figur, Box 02 ist frei.
        // Stapel-Pfad: Box 01 wird vorgeschlagen (Stapel waechst).
        await _sut.CreateBulkAsync(new[] { "Box 01", "Box 02" });
        var b1 = await _sut.GetByLabelAsync("Box 01");
        await SeedMinifigInBinAsync(b1!.Id, "fig1", TrackedMinifigStatus.Waiting);

        var suggestion = await _sut.SuggestBinForWaitingMinifigAsync(maxWaitingLimit: 3);

        Assert.NotNull(suggestion);
        Assert.Equal("Box 01", suggestion!.Label);
    }

    [Fact]
    public async Task SuggestBinForWaitingMinifig_limit3_at_limit_falls_back_to_free_bin()
    {
        // Limit=3, Box 01 hat schon 3 wartende -> nicht mehr stapelbar.
        // Box 02 ist frei -> wird vorgeschlagen.
        await _sut.CreateBulkAsync(new[] { "Box 01", "Box 02" });
        var b1 = await _sut.GetByLabelAsync("Box 01");
        for (int i = 0; i < 3; i++)
            await SeedMinifigInBinAsync(b1!.Id, $"fig{i}", TrackedMinifigStatus.Waiting);

        var suggestion = await _sut.SuggestBinForWaitingMinifigAsync(maxWaitingLimit: 3);

        Assert.NotNull(suggestion);
        Assert.Equal("Box 02", suggestion!.Label);
    }

    [Fact]
    public async Task SuggestBinForWaitingMinifig_limit3_allows_stack_bin_with_complete()
    {
        // v0.1.22-beta.1 Block G: Mix Wartend+Complete ist erlaubt
        // (Reifungspfad). Vorher (UX X.31) hat Box 01 wegen der Complete-
        // Figur als Mix-Verbot gegolten; jetzt nicht mehr - Box 01 wins
        // weil sie schon eine wartende Figur enthaelt (Stapel waechst).
        await _sut.CreateBulkAsync(new[] { "Box 01", "Box 02" });
        var b1 = await _sut.GetByLabelAsync("Box 01");
        await SeedMinifigInBinAsync(b1!.Id, "wait1", TrackedMinifigStatus.Waiting);
        await SeedMinifigInBinAsync(b1!.Id, "done1", TrackedMinifigStatus.Complete);

        var suggestion = await _sut.SuggestBinForWaitingMinifigAsync(maxWaitingLimit: 3);

        Assert.NotNull(suggestion);
        Assert.Equal("Box 01", suggestion!.Label);
    }

    [Fact]
    public async Task SuggestBinForWaitingMinifig_limit3_prefers_fuller_stack()
    {
        // Limit=3, Box 01 hat 1 wartende, Box 02 hat 2 wartende.
        // Voller Bin zuerst -> Box 02.
        await _sut.CreateBulkAsync(new[] { "Box 01", "Box 02", "Box 03" });
        var b1 = await _sut.GetByLabelAsync("Box 01");
        var b2 = await _sut.GetByLabelAsync("Box 02");
        await SeedMinifigInBinAsync(b1!.Id, "f1", TrackedMinifigStatus.Waiting);
        await SeedMinifigInBinAsync(b2!.Id, "f2", TrackedMinifigStatus.Waiting);
        await SeedMinifigInBinAsync(b2!.Id, "f3", TrackedMinifigStatus.Waiting);

        var suggestion = await _sut.SuggestBinForWaitingMinifigAsync(maxWaitingLimit: 3);

        Assert.NotNull(suggestion);
        Assert.Equal("Box 02", suggestion!.Label);
    }

    [Fact]
    public async Task SuggestBinForCompleteMinifig_under_limit_picks_existing_complete_bin()
    {
        // Bin mit 4 Complete-Figuren + Limit 5 -> der Stapel waechst.
        await _sut.CreateBulkAsync(new[] { "Box 01", "Box 02" });
        var b1 = await _sut.GetByLabelAsync("Box 01");
        for (int i = 0; i < 4; i++)
            await SeedMinifigInBinAsync(b1!.Id, $"fig{i}", TrackedMinifigStatus.Complete);

        var suggestion = await _sut.SuggestBinForCompleteMinifigAsync(5);

        Assert.NotNull(suggestion);
        Assert.Equal("Box 01", suggestion!.Label);
    }

    [Fact]
    public async Task SuggestBinForCompleteMinifig_at_limit_picks_new_free_bin()
    {
        // Bin mit 5 Complete-Figuren + Limit 5 -> erreichtes Limit, neues
        // freies Fach wird vorgeschlagen.
        await _sut.CreateBulkAsync(new[] { "Box 01", "Box 02" });
        var b1 = await _sut.GetByLabelAsync("Box 01");
        for (int i = 0; i < 5; i++)
            await SeedMinifigInBinAsync(b1!.Id, $"fig{i}", TrackedMinifigStatus.Complete);

        var suggestion = await _sut.SuggestBinForCompleteMinifigAsync(5);

        Assert.NotNull(suggestion);
        Assert.Equal("Box 02", suggestion!.Label);
    }

    [Fact]
    public async Task SuggestBinForCompleteMinifig_allows_bin_with_waiting()
    {
        // v0.1.22-beta.1 Block G: Mix Wartend+Complete ist erlaubt (Reifungs-
        // pfad). Vorher (UX X.31) hat Box 01 wegen der wartenden Figur als
        // Mix-Verbot gegolten; jetzt nicht mehr - Box 01 wins weil sie
        // schon Complete-Figuren enthaelt (Complete-Stapel waechst).
        await _sut.CreateBulkAsync(new[] { "Box 01", "Box 02" });
        var b1 = await _sut.GetByLabelAsync("Box 01");
        await SeedMinifigInBinAsync(b1!.Id, "fig1", TrackedMinifigStatus.Complete);
        await SeedMinifigInBinAsync(b1!.Id, "fig2", TrackedMinifigStatus.Complete);
        await SeedMinifigInBinAsync(b1!.Id, "fig3", TrackedMinifigStatus.Waiting);

        var suggestion = await _sut.SuggestBinForCompleteMinifigAsync(5);

        Assert.NotNull(suggestion);
        Assert.Equal("Box 01", suggestion!.Label);
    }

    [Fact]
    public async Task SuggestBinForCompleteMinifig_skips_bin_with_floating_part()
    {
        await _sut.CreateBulkAsync(new[] { "Box 01", "Box 02" });
        var b1 = await _sut.GetByLabelAsync("Box 01");
        await SeedMinifigInBinAsync(b1!.Id, "fig1", TrackedMinifigStatus.Complete);
        await SeedFloatingPartInBinAsync(b1!.Id, "3001", 1);

        var suggestion = await _sut.SuggestBinForCompleteMinifigAsync(5);

        Assert.NotNull(suggestion);
        Assert.Equal("Box 02", suggestion!.Label);
    }

    [Fact]
    public async Task SuggestBinForCompleteMinifig_prefers_fuller_bin_when_multiple_under_limit()
    {
        // Sortierung: meiste Complete zuerst (Stapel wachsen statt parallele Faecher).
        await _sut.CreateBulkAsync(new[] { "Box 01", "Box 02", "Box 03" });
        var b1 = await _sut.GetByLabelAsync("Box 01");
        var b2 = await _sut.GetByLabelAsync("Box 02");
        await SeedMinifigInBinAsync(b1!.Id, "f1", TrackedMinifigStatus.Complete);
        await SeedMinifigInBinAsync(b2!.Id, "f2", TrackedMinifigStatus.Complete);
        await SeedMinifigInBinAsync(b2!.Id, "f3", TrackedMinifigStatus.Complete);

        var suggestion = await _sut.SuggestBinForCompleteMinifigAsync(5);

        Assert.NotNull(suggestion);
        Assert.Equal("Box 02", suggestion!.Label);
    }

    [Fact]
    public async Task SuggestBinForFloatingPart_picks_existing_bin_with_same_part_color()
    {
        // Spec: gleiches Teil + gleiche Farbe -> bestehendes Bin (Stapel-Match).
        await _sut.CreateBulkAsync(new[] { "Box 01", "Box 02" });
        var b1 = await _sut.GetByLabelAsync("Box 01");
        await SeedFloatingPartInBinAsync(b1!.Id, "3001", 5);

        var suggestion = await _sut.SuggestBinForFloatingPartAsync("3001", 0, "");

        Assert.NotNull(suggestion);
        Assert.Equal("Box 01", suggestion!.Label);
    }

    [Fact]
    public async Task SuggestBinForFloatingPart_new_part_picks_bin_without_complete()
    {
        // Spec: neuer Typ+Farbe -> naechstes Fach OHNE Complete drin.
        await _sut.CreateBulkAsync(new[] { "Box 01", "Box 02" });
        var b1 = await _sut.GetByLabelAsync("Box 01");
        await SeedMinifigInBinAsync(b1!.Id, "fig1", TrackedMinifigStatus.Complete);

        var suggestion = await _sut.SuggestBinForFloatingPartAsync("3024", 11, "");

        Assert.NotNull(suggestion);
        Assert.Equal("Box 02", suggestion!.Label);
    }

    [Fact]
    public async Task SuggestBinForFloatingPart_returns_null_when_all_bins_have_complete_figures()
    {
        // UX X.33 v0.1.19-beta.7 Block K: vorher (beta.2) hat ein Step 5
        // ("am wenigsten belegt"-Fallback) das am wenigsten gefuellte Bin
        // mit Complete-Figuren stillschweigend zurueckgegeben - Teile wurden
        // dann mit verkaufsfertigen Figuren vermischt. Step 5 ist jetzt raus,
        // die Methode liefert null. Aufrufer muss eine Warnung anzeigen.
        await _sut.CreateBulkAsync(new[] { "Box 01", "Box 02" });
        var b1 = await _sut.GetByLabelAsync("Box 01");
        var b2 = await _sut.GetByLabelAsync("Box 02");
        await SeedMinifigInBinAsync(b1!.Id, "fig1", TrackedMinifigStatus.Complete);
        await SeedMinifigInBinAsync(b1!.Id, "fig1b", TrackedMinifigStatus.Complete);
        await SeedMinifigInBinAsync(b2!.Id, "fig2", TrackedMinifigStatus.Complete);

        var suggestion = await _sut.SuggestBinForFloatingPartAsync("3024", 11, "");

        Assert.Null(suggestion);
    }

    [Fact]
    public async Task SuggestBinForFloatingPart_returns_null_only_when_no_bins_exist()
    {
        // Bug-Fix v0.1.19-beta.2: ohne Bins gibt's logischerweise nichts
        // vorzuschlagen.
        var suggestion = await _sut.SuggestBinForFloatingPartAsync("3024", 11, "");
        Assert.Null(suggestion);
    }

    [Fact]
    public async Task SuggestBinForFloatingPart_picks_bin_with_floating_of_different_category()
    {
        // UX X.33 Block N (v0.1.19-beta.7): Bin mit anderem FloatingPart-Typ
        // ist OK wenn die Brickognize-Kategorien unterschiedlich sind.
        // Verschiedene Kategorien duerfen sich ein Bin teilen.
        await _sut.CreateBulkAsync(new[] { "Box 01", "Box 02" });
        var b1 = await _sut.GetByLabelAsync("Box 01");
        var b2 = await _sut.GetByLabelAsync("Box 02");
        await SeedMinifigInBinAsync(b1!.Id, "fig1", TrackedMinifigStatus.Complete);
        await SeedFloatingPartInBinAsync(b2!.Id, "9999", qty: 1, colorId: 99,
            brickognizeCategory: "Minifigure, Hair");

        // Neuer Part hat ANDERE Kategorie ("Minifigure, Head") - darf zu
        // Box 02 weil Hair und Head verschiedene Kategorien sind.
        var suggestion = await _sut.SuggestBinForFloatingPartAsync(
            "3024", 11, "Minifigure, Head");

        Assert.NotNull(suggestion);
        Assert.Equal("Box 02", suggestion!.Label);
    }

    [Fact]
    public async Task SuggestBinForFloatingPart_skips_bin_with_floating_of_same_category()
    {
        // UX X.33 Block N: Default-Regel "max 1 PartId pro Kategorie pro Bin".
        // Box 01 hat schon einen Helm-A. Helm-B (gleiche Kategorie, andere
        // PartNo) kommt nicht ins gleiche Bin.
        await _sut.CreateBulkAsync(new[] { "Box 01", "Box 02" });
        var b1 = await _sut.GetByLabelAsync("Box 01");
        await SeedFloatingPartInBinAsync(b1!.Id, "2446", qty: 1, colorId: 5,
            brickognizeCategory: "Minifigure, Headgear");

        var suggestion = await _sut.SuggestBinForFloatingPartAsync(
            "193a2", 5, "Minifigure, Headgear");

        Assert.NotNull(suggestion);
        Assert.Equal("Box 02", suggestion!.Label);
    }

    [Fact]
    public async Task SuggestBinForFloatingPart_unknown_category_collides_with_other_unknown()
    {
        // UX X.33 Block N + Migration A: Bestand-FloatingParts haben
        // BrickognizeCategory=null = "Unbekannt"-Pseudo-Kategorie. Ein
        // neuer ungelabelter Part mit anderer PartNo kollidiert. Damit
        // wird der Bestand-Stapel sauber gehalten und neue ungelabelte
        // Items landen in eigenen Bins.
        await _sut.CreateBulkAsync(new[] { "Box 01", "Box 02" });
        var b1 = await _sut.GetByLabelAsync("Box 01");
        await SeedFloatingPartInBinAsync(b1!.Id, "9999", qty: 1, colorId: 99,
            brickognizeCategory: null);

        var suggestion = await _sut.SuggestBinForFloatingPartAsync(
            "3024", 11, "");

        Assert.NotNull(suggestion);
        Assert.Equal("Box 02", suggestion!.Label);
    }

    [Fact]
    public async Task SuggestBinForFloatingPart_user_mapping_overrides_default_rule()
    {
        // UX X.33 Block N: User-Mapping gewinnt ueber die Default-Regel.
        // Box 01 hat Helm-A, neuer Helm-B wuerde via Default-Regel in Box 02
        // gehen. Aber User hat "Minifigure, Headgear" -> Box 01 gemappt -
        // also kommt Helm-B trotzdem in Box 01 (User-Wille gilt).
        await _sut.CreateBulkAsync(new[] { "Box 01", "Box 02" });
        var b1 = await _sut.GetByLabelAsync("Box 01");
        await SeedFloatingPartInBinAsync(b1!.Id, "2446", qty: 1, colorId: 5,
            brickognizeCategory: "Minifigure, Headgear");

        var userMapping = new Dictionary<string, int>
        {
            ["Minifigure, Headgear"] = b1.Id
        };

        var suggestion = await _sut.SuggestBinForFloatingPartAsync(
            "193a2", 5, "Minifigure, Headgear",
            userMapping: userMapping);

        Assert.NotNull(suggestion);
        Assert.Equal("Box 01", suggestion!.Label);
    }

    [Fact]
    public async Task SuggestBinForFloatingPart_user_mapping_falls_through_when_bin_deleted()
    {
        // Mapping zeigt auf eine ungueltige Bin-Id (z.B. weil das Fach
        // geloescht wurde). Service faellt auf Default-Regel zurueck.
        await _sut.CreateBulkAsync(new[] { "Box 01" });
        var b1 = await _sut.GetByLabelAsync("Box 01");

        var userMapping = new Dictionary<string, int>
        {
            ["Minifigure, Head"] = 9999 // existiert nicht
        };

        var suggestion = await _sut.SuggestBinForFloatingPartAsync(
            "3024", 11, "Minifigure, Head",
            userMapping: userMapping);

        Assert.NotNull(suggestion);
        Assert.Equal("Box 01", suggestion!.Label);
    }

    // ===== Bug-Fix v0.1.19-beta.2: excludeMinifigId fuer DismantleWizard =====

    [Fact]
    public async Task SuggestBinForFloatingPart_excludeMinifigId_treats_excluded_bin_as_free()
    {
        // Szenario: Box 01 hat eine Complete-Figur (cty0685). Sie soll gleich
        // zerlegt werden -> ihr Fach ist eigentlich ein guter Kandidat fuer
        // die Teile. Mit excludeMinifigId=cty0685.Id soll Box 01 als Stufe-2-
        // Treffer (wirklich frei) zurueckkommen.
        await _sut.CreateBulkAsync(new[] { "Box 01" });
        var b1 = await _sut.GetByLabelAsync("Box 01");
        var minifigId = await SeedMinifigInBinAsync(b1!.Id, "cty0685", TrackedMinifigStatus.Complete);

        var suggestion = await _sut.SuggestBinForFloatingPartAsync(
            "3024", 11, "", excludeMinifigId: minifigId);

        Assert.NotNull(suggestion);
        Assert.Equal("Box 01", suggestion!.Label);
    }

    [Fact]
    public async Task SuggestBinForFloatingPart_without_excludeMinifigId_skips_bin_with_complete()
    {
        // Sanity: ohne excludeMinifigId zaehlt Complete weiterhin als
        // belegend - User-Bug-Repro vor dem Fix wuerde Box 01 nicht
        // vorschlagen, sondern Box 02 oder Stufe-4-Fallback.
        await _sut.CreateBulkAsync(new[] { "Box 01", "Box 02" });
        var b1 = await _sut.GetByLabelAsync("Box 01");
        await SeedMinifigInBinAsync(b1!.Id, "cty0685", TrackedMinifigStatus.Complete);

        var suggestion = await _sut.SuggestBinForFloatingPartAsync("3024", 11, "");

        Assert.NotNull(suggestion);
        Assert.Equal("Box 02", suggestion!.Label);
    }

    [Fact]
    public async Task SuggestBinForFloatingPart_excludeMinifigId_does_not_mask_other_minifigs()
    {
        // Box 01 hat ZWEI Complete-Figuren (fig1 + fig2). Wir excluden nur
        // fig1 - fig2 bleibt belegend, Box 01 darf nicht als frei gelten.
        await _sut.CreateBulkAsync(new[] { "Box 01", "Box 02" });
        var b1 = await _sut.GetByLabelAsync("Box 01");
        var fig1 = await SeedMinifigInBinAsync(b1!.Id, "fig1", TrackedMinifigStatus.Complete);
        await SeedMinifigInBinAsync(b1!.Id, "fig2", TrackedMinifigStatus.Complete);

        var suggestion = await _sut.SuggestBinForFloatingPartAsync(
            "3024", 11, "", excludeMinifigId: fig1);

        Assert.NotNull(suggestion);
        Assert.Equal("Box 02", suggestion!.Label);
    }

    [Fact]
    public async Task SuggestBinForFloatingPart_different_color_gets_separate_bin()
    {
        // 3001/Black liegt in Box 01. Wir scannen 3001/Red -> Stapel-Match
        // schlaegt fehl (verschiedene Farbe). Default-Regel: Box 01 hat
        // schon einen FloatingPart mit gleicher Kategorie aber anderer
        // PartId-Color-Kombination - kollidiert wenn Cat gleich. Mit
        // beiden Cat="" gilt das. -> Box 02.
        await _sut.CreateBulkAsync(new[] { "Box 01", "Box 02" });
        var b1 = await _sut.GetByLabelAsync("Box 01");
        await SeedFloatingPartInBinAsync(b1!.Id, "3001", 3); // ColorId=0 (Black)

        var suggestion = await _sut.SuggestBinForFloatingPartAsync("3001", 5, ""); // ColorId=5 (Red)

        Assert.NotNull(suggestion);
        Assert.Equal("Box 02", suggestion!.Label);
    }

    // ---- FindBinsThatWouldBeEmpty + ReleaseBins (Phase 7) ----

    [Fact]
    public async Task FindBinsThatWouldBeEmpty_returns_bins_only_holding_to_be_removed_minifigs()
    {
        // Box 01: enthaelt nur die zu entfernende Figur -> waere leer
        // Box 02: enthaelt die zu entfernende Figur PLUS eine andere -> waere nicht leer
        // Box 03: enthaelt nur die zu entfernende Figur, aber ein FloatingPart -> waere nicht leer
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

    private async Task SeedFloatingPartInBinAsync(int binId, string partNo, int qty,
        int colorId = 0, string? brickognizeCategory = null)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        ctx.FloatingParts.Add(new FloatingPart
        {
            PartNumber = partNo,
            ColorId = colorId,
            ColorName = colorId == 0 ? "Black" : $"Color{colorId}",
            PartName = partNo,
            Quantity = qty,
            StorageBinId = binId,
            AddedAt = DateTime.UtcNow,
            BrickognizeCategory = brickognizeCategory
        });
        await ctx.SaveChangesAsync();
    }

    private sealed class SimpleContextFactory : IDbContextFactory<UserDataContext>
    {
        private readonly DbContextOptions<UserDataContext> _options;
        public SimpleContextFactory(DbContextOptions<UserDataContext> options) => _options = options;
        public UserDataContext CreateDbContext() => new(_options);
    }

    // ========================================================================
    // v0.1.22-beta.1 Block G: BinKind-Klassifikator + Mix-Bestand-Scan
    // ========================================================================

    [Fact]
    public async Task GetBinKindAsync_classifies_empty_bin_correctly()
    {
        await _sut.CreateBulkAsync(new[] { "Box 01" });
        var b1 = await _sut.GetByLabelAsync("Box 01");

        var kind = await _sut.GetBinKindAsync(b1!.Id);

        Assert.Equal(BinKind.Empty, kind);
    }

    [Fact]
    public async Task GetBinKindAsync_classifies_floating_only_bin_correctly()
    {
        await _sut.CreateBulkAsync(new[] { "Box 01" });
        var b1 = await _sut.GetByLabelAsync("Box 01");
        await SeedFloatingPartInBinAsync(b1!.Id, "3001", 2);

        var kind = await _sut.GetBinKindAsync(b1!.Id);

        Assert.Equal(BinKind.FloatingOnly, kind);
    }

    [Fact]
    public async Task GetBinKindAsync_classifies_waiting_bin_correctly()
    {
        await _sut.CreateBulkAsync(new[] { "Box 01" });
        var b1 = await _sut.GetByLabelAsync("Box 01");
        await SeedMinifigInBinAsync(b1!.Id, "fig1", TrackedMinifigStatus.Waiting);

        var kind = await _sut.GetBinKindAsync(b1!.Id);

        Assert.Equal(BinKind.WaitingMinifig, kind);
    }

    [Fact]
    public async Task GetBinKindAsync_classifies_complete_only_bin_correctly()
    {
        await _sut.CreateBulkAsync(new[] { "Box 01" });
        var b1 = await _sut.GetByLabelAsync("Box 01");
        await SeedMinifigInBinAsync(b1!.Id, "fig1", TrackedMinifigStatus.Complete);

        var kind = await _sut.GetBinKindAsync(b1!.Id);

        Assert.Equal(BinKind.CompleteOnly, kind);
    }

    [Fact]
    public async Task GetBinKindAsync_treats_waiting_plus_complete_as_WaitingMinifig()
    {
        // Reifungspfad: wartende Figur ist komplett geworden und liegt
        // jetzt zusammen mit einer noch wartenden im Bin.
        await _sut.CreateBulkAsync(new[] { "Box 01" });
        var b1 = await _sut.GetByLabelAsync("Box 01");
        await SeedMinifigInBinAsync(b1!.Id, "wait1", TrackedMinifigStatus.Waiting);
        await SeedMinifigInBinAsync(b1!.Id, "done1", TrackedMinifigStatus.Complete);

        var kind = await _sut.GetBinKindAsync(b1!.Id);

        Assert.Equal(BinKind.WaitingMinifig, kind);
    }

    [Fact]
    public async Task FindBestandMixBins_returns_empty_when_no_mix()
    {
        await _sut.CreateBulkAsync(new[] { "Box 01", "Box 02" });
        var b1 = await _sut.GetByLabelAsync("Box 01");
        var b2 = await _sut.GetByLabelAsync("Box 02");
        await SeedMinifigInBinAsync(b1!.Id, "wait1", TrackedMinifigStatus.Waiting);
        await SeedFloatingPartInBinAsync(b2!.Id, "3001", 5);

        var mixBins = await _sut.FindBestandMixBinsAsync();

        Assert.Empty(mixBins);
    }

    [Fact]
    public async Task FindBestandMixBins_finds_complete_plus_floating_mix()
    {
        // Block G: Floating-Bin mit Complete-Figur ist Anomalie.
        await _sut.CreateBulkAsync(new[] { "Box 01" });
        var b1 = await _sut.GetByLabelAsync("Box 01");
        await SeedMinifigInBinAsync(b1!.Id, "done", TrackedMinifigStatus.Complete);
        await SeedFloatingPartInBinAsync(b1!.Id, "3001", 1);

        var mixBins = await _sut.FindBestandMixBinsAsync();

        Assert.Single(mixBins);
        Assert.Equal(b1!.Id, mixBins[0].BinId);
    }
}
