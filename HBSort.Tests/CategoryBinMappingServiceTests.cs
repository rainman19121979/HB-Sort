using HBSort.Core.Database;
using HBSort.Core.Models;
using HBSort.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HBSort.Tests;

/// <summary>
/// UX X.33 v0.1.19-beta.7 Block M: Tests fuer den CategoryBinMappingService.
/// Nutzt In-Memory-SQLite + In-Memory-SettingsService-Stub.
/// </summary>
public class CategoryBinMappingServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<UserDataContext> _factory;
    private readonly StorageBinService _binService;
    private readonly StubSettingsService _settings;
    private readonly CategoryBinMappingService _sut;

    public CategoryBinMappingServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<UserDataContext>()
            .UseSqlite(_connection)
            .Options;

        using (var ctx = new UserDataContext(options))
            ctx.Database.EnsureCreated();

        _factory = new SimpleFactory(options);
        _binService = new StorageBinService(_factory);
        _settings = new StubSettingsService();
        _sut = new CategoryBinMappingService(_settings, _binService);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task ResolveBinForCategory_returns_null_for_empty_string()
    {
        Assert.Null(await _sut.ResolveBinForCategoryAsync(null));
        Assert.Null(await _sut.ResolveBinForCategoryAsync(""));
        Assert.Null(await _sut.ResolveBinForCategoryAsync("   "));
    }

    [Fact]
    public async Task ResolveBinForCategory_returns_null_when_no_mapping()
    {
        await _binService.CreateSingleAsync("Box 01");
        Assert.Null(await _sut.ResolveBinForCategoryAsync("Minifigure, Head"));
    }

    [Fact]
    public async Task ResolveBinForCategory_returns_mapped_bin()
    {
        var bin = await _binService.CreateSingleAsync("Box 01");
        _settings.Current.CategoryToBinMapping["Minifigure, Head"] = bin.Id;

        var result = await _sut.ResolveBinForCategoryAsync("Minifigure, Head");

        Assert.NotNull(result);
        Assert.Equal("Box 01", result!.Label);
    }

    [Fact]
    public async Task ResolveBinForCategory_returns_null_when_mapped_bin_was_deleted()
    {
        // Bin -> Mapping setzen -> Bin loeschen. ResolveBin muss null
        // liefern, NICHT crashen, damit der Aufrufer auf den Suggest-Pfad
        // zurueckfaellt.
        var bin = await _binService.CreateSingleAsync("Box 01");
        _settings.Current.CategoryToBinMapping["Minifigure, Head"] = bin.Id;
        await _binService.DeleteAsync(bin.Id);

        var result = await _sut.ResolveBinForCategoryAsync("Minifigure, Head");
        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveBinForPartName_matches_prefix()
    {
        // BL-Part-Name beginnt mit dem Brickognize-Category-String -
        // wie "Minifigure, Head Black Eyebrows ..." matcht "Minifigure, Head".
        // UX X.33 Block N: Heuristik laeuft jetzt gegen
        // SeenBrickognizeCategories (organisch gewachsene Liste). Mapping
        // muss zusaetzlich gesetzt sein damit ResolveBinForPartName ein Bin
        // liefert.
        var bin = await _binService.CreateSingleAsync("Box 01");
        _settings.Current.SeenBrickognizeCategories.Add("Minifigure, Head");
        _settings.Current.CategoryToBinMapping["Minifigure, Head"] = bin.Id;

        var result = await _sut.ResolveBinForPartNameAsync(
            "Minifigure, Head Black Eyebrows, Smile Pattern");

        Assert.NotNull(result);
        Assert.Equal("Box 01", result!.Label);
    }

    [Fact]
    public async Task ResolveBinForPartName_picks_longest_matching_prefix()
    {
        // Spezifischer schlaegt allgemeiner - "Minifigure, Headgear" gewinnt
        // ueber "Minifigure,". UX X.33 Block N: Match-Quelle ist
        // SeenBrickognizeCategories.
        var binGeneral = await _binService.CreateSingleAsync("Box 01");
        var binSpecific = await _binService.CreateSingleAsync("Box 02");
        _settings.Current.SeenBrickognizeCategories.Add("Minifigure,");
        _settings.Current.SeenBrickognizeCategories.Add("Minifigure, Headgear");
        _settings.Current.CategoryToBinMapping["Minifigure,"] = binGeneral.Id;
        _settings.Current.CategoryToBinMapping["Minifigure, Headgear"] = binSpecific.Id;

        var result = await _sut.ResolveBinForPartNameAsync(
            "Minifigure, Headgear Helmet Construction with Ponytail");

        Assert.NotNull(result);
        Assert.Equal("Box 02", result!.Label);
    }

    [Fact]
    public async Task ResolveBinForPartName_returns_null_when_no_prefix_matches()
    {
        var bin = await _binService.CreateSingleAsync("Box 01");
        _settings.Current.SeenBrickognizeCategories.Add("Minifigure, Head");
        _settings.Current.CategoryToBinMapping["Minifigure, Head"] = bin.Id;

        var result = await _sut.ResolveBinForPartNameAsync(
            "Antenna Whip 4L with Stop Ring");

        Assert.Null(result);
    }

    [Fact]
    public void DeriveCategoryFromPartName_returns_longest_match_from_seen_list()
    {
        // UX X.33 Block N: Heuristik B fuer den Wizard-Pfad. Match gegen
        // SeenBrickognizeCategories (kein Mapping noetig).
        _settings.Current.SeenBrickognizeCategories.Add("Minifigure, Head");
        _settings.Current.SeenBrickognizeCategories.Add("Minifigure, Headgear");

        var result = _sut.DeriveCategoryFromPartName(
            "Minifigure, Headgear Helmet with Visor");

        Assert.Equal("Minifigure, Headgear", result);
    }

    [Fact]
    public void DeriveCategoryFromPartName_returns_unknown_for_no_prefix_match()
    {
        // Pre-Tag-Fix v0.1.19-beta.7: Heuristik liefert IMMER einen String.
        // "Minifigure Air Tanks" hat kein Komma - matcht keinen Praefix aus
        // der Seen-Liste, faellt auf "Unbekannt" als Pseudo-Kategorie zurueck.
        _settings.Current.SeenBrickognizeCategories.Add("Minifigure, Head");
        _settings.Current.SeenBrickognizeCategories.Add("Minifigure, Body Wear");

        var result = _sut.DeriveCategoryFromPartName("Minifigure Air Tanks");

        Assert.Equal(ICategoryBinMappingService.UnknownCategory, result);
        Assert.Equal("Unbekannt", result);
    }

    [Fact]
    public void DeriveCategoryFromPartName_returns_unknown_for_null_or_empty_input()
    {
        // Edge-Case: null/empty PartName liefert "Unbekannt", nicht null.
        _settings.Current.SeenBrickognizeCategories.Add("Minifigure, Head");

        Assert.Equal(ICategoryBinMappingService.UnknownCategory, _sut.DeriveCategoryFromPartName(null));
        Assert.Equal(ICategoryBinMappingService.UnknownCategory, _sut.DeriveCategoryFromPartName(""));
        Assert.Equal(ICategoryBinMappingService.UnknownCategory, _sut.DeriveCategoryFromPartName("   "));
    }

    [Fact]
    public void DeriveCategoryFromPartName_returns_unknown_when_seen_list_empty()
    {
        // Vor dem ersten Scan ist SeenBrickognizeCategories leer - jeder
        // PartName liefert "Unbekannt".
        Assert.Empty(_settings.Current.SeenBrickognizeCategories);
        Assert.Equal(
            ICategoryBinMappingService.UnknownCategory,
            _sut.DeriveCategoryFromPartName("Minifigure, Head Black Eyebrows"));
    }

    [Fact]
    public async Task RememberSeenCategory_adds_new_string_and_persists()
    {
        Assert.Empty(_settings.Current.SeenBrickognizeCategories);
        Assert.Equal(0, _settings.SaveCallCount);

        await _sut.RememberSeenCategoryAsync("Minifigure, Head");

        Assert.Single(_settings.Current.SeenBrickognizeCategories);
        Assert.Equal("Minifigure, Head", _settings.Current.SeenBrickognizeCategories[0]);
        Assert.Equal(1, _settings.SaveCallCount);
    }

    [Fact]
    public async Task RememberSeenCategory_skips_duplicates()
    {
        await _sut.RememberSeenCategoryAsync("Minifigure, Head");
        await _sut.RememberSeenCategoryAsync("Minifigure, Head");

        Assert.Single(_settings.Current.SeenBrickognizeCategories);
        // Beim zweiten Aufruf nicht persistiert.
        Assert.Equal(1, _settings.SaveCallCount);
    }

    [Fact]
    public async Task RememberSeenCategory_keeps_alphabetical_order()
    {
        await _sut.RememberSeenCategoryAsync("Minifigure, Torso");
        await _sut.RememberSeenCategoryAsync("Minifigure, Head");
        await _sut.RememberSeenCategoryAsync("Minifigure, Hair");

        Assert.Equal(
            new[] { "Minifigure, Hair", "Minifigure, Head", "Minifigure, Torso" },
            _settings.Current.SeenBrickognizeCategories);
    }

    [Fact]
    public async Task RememberSeenCategory_ignores_empty_input()
    {
        await _sut.RememberSeenCategoryAsync("");
        await _sut.RememberSeenCategoryAsync(null!);
        await _sut.RememberSeenCategoryAsync("   ");

        Assert.Empty(_settings.Current.SeenBrickognizeCategories);
        Assert.Equal(0, _settings.SaveCallCount);
    }

    // UX X.33 Block N (v0.1.19-beta.7 Refactor): die 4 UpdateMapping-Tests
    // sind weg, weil die Methode aus dem Service entfernt wurde. Auto-
    // Mapping beim Lagern ist raus, Settings-UI schreibt direkt ins
    // AppSettings.CategoryToBinMapping.

    private sealed class SimpleFactory : IDbContextFactory<UserDataContext>
    {
        private readonly DbContextOptions<UserDataContext> _options;
        public SimpleFactory(DbContextOptions<UserDataContext> options) => _options = options;
        public UserDataContext CreateDbContext() => new(_options);
    }

    /// <summary>
    /// Minimaler ISettingsService-Stub - haelt eine in-memory AppSettings,
    /// SaveAsync zaehlt Aufrufe damit die Tests Persistenz beobachten koennen.
    /// </summary>
    private sealed class StubSettingsService : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public int SaveCallCount { get; private set; }
        public Task LoadAsync() => Task.CompletedTask;
        public Task SaveAsync()
        {
            SaveCallCount++;
            return Task.CompletedTask;
        }
    }
}
