using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HBSort.Core.Database;
using HBSort.Core.Models;
using HBSort.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace HBSort.ViewModels;

/// <summary>
/// ViewModel fuer den Bauvorschlag-Detail-Dialog (UX-Iteration X.4).
/// Zeigt eine BuildSuggestion (eine bisher ungetrackte BL-Minifig) mit:
///   - Header (Name, BL-ID, Jahr, Bild)
///   - Liste aller Required-Parts (Subsets der Minifig) mit Status pro Teil:
///     "Vorhanden in Box X" wenn ein passender FloatingPart existiert,
///     sonst "Fehlt".
///   - Lagerfach-Dropdown (frei vs. belegt)
///   - Notizen-Feld
/// Beim "Figur anlegen"-Klick ruft das Dialog-Code-Behind den
/// IMinifigPersistenceService.PersistAndStoreAsync auf, der den Reverse-Match
/// gegen die FloatingParts erledigt (und ggf. die Figur direkt als Complete
/// markiert wenn alles vorhanden war).
/// </summary>
public partial class BuildSuggestionDetailViewModel : ObservableObject
{
    private readonly IBlCacheRepository _cache;
    private readonly IDbContextFactory<UserDataContext> _ctxFactory;
    private readonly IPartImageProvider _imageProvider;

    public string BricklinkId { get; }
    public string Name { get; }
    public int? YearReleased { get; }

    [ObservableProperty]
    private string? _imageUrl;

    /// <summary>Required-Parts der Figur mit "Vorhanden / Fehlt"-Status.</summary>
    public ObservableCollection<BuildSuggestionPartViewModel> Parts { get; } = new();

    /// <summary>Lagerfach-Auswahl (frei zuerst, dann Trenner, dann belegte).</summary>
    public ObservableCollection<StorageBin> AvailableBins { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreate))]
    private StorageBin? _selectedBin;

    [ObservableProperty]
    private string _userNotes = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreate))]
    private bool _isCreating;

    /// <summary>Gesamt-Anzahl benoetigter Teile (nur fuer Header-Anzeige).</summary>
    public int TotalQuantityNeeded => Parts.Sum(p => p.QuantityNeeded);

    /// <summary>Gesamt-Anzahl bereits vorhandener Teile (per Reverse-Match aus FloatingPool).</summary>
    public int TotalQuantityHave => Parts.Sum(p => p.QuantityHave);

    /// <summary>"Es sind X von Y Teilen direkt verfuegbar"-Label.</summary>
    public string AvailabilityLabel
    {
        get
        {
            var have = TotalQuantityHave;
            var need = TotalQuantityNeeded;
            if (need == 0) return "Keine Teile-Angabe in den BL-Daten";
            if (have == 0) return $"Noch keine der {need} benoetigten Teile vorhanden";
            if (have >= need) return $"Alle {need} Teile vorhanden - Figur wird direkt KOMPLETT angelegt";
            return $"{have} von {need} Teilen direkt verfuegbar";
        }
    }

    public bool CanCreate => SelectedBin != null && !IsCreating;

    public BuildSuggestionDetailViewModel(
        string bricklinkId,
        string name,
        int? yearReleased,
        string? imageUrl,
        IBlCacheRepository cache,
        IDbContextFactory<UserDataContext> ctxFactory,
        IPartImageProvider imageProvider)
    {
        BricklinkId = bricklinkId;
        Name = name;
        YearReleased = yearReleased;
        ImageUrl = imageUrl;
        _cache = cache;
        _ctxFactory = ctxFactory;
        _imageProvider = imageProvider;
    }

    /// <summary>
    /// Subsets der Minifig laden, mit FloatingParts vergleichen und die Liste
    /// "Vorhanden / Fehlt" befuellen. Parallel die Lagerfaecher laden.
    /// </summary>
    public async Task LoadAsync(IStorageBinService binService)
    {
        // 1) Subsets aus dem BL-Cache (sollten alle vorhanden sein - sonst gabs
        //    den Bauvorschlag gar nicht erst).
        var subsets = await _cache.GetSubsetsAsync("M", BricklinkId);
        subsets = subsets.Where(s => !s.IsFromSupersets && s.ItemType == "P").ToList();

        // 2) Floating-Pool laden + nach (PartNo, ColorId) gruppieren mit Bin-Liste.
        await using var ctx = await _ctxFactory.CreateDbContextAsync();
        var floats = await ctx.FloatingParts.AsNoTracking()
            .Include(fp => fp.StorageBin)
            .ToListAsync();
        // Map: (PartNo, ColorId) -> List<(BinLabel, Quantity)>
        var floatLookup = floats
            .GroupBy(fp => (fp.PartNumber, fp.ColorId))
            .ToDictionary(
                g => g.Key,
                g => g.Select(fp => (BinLabel: fp.StorageBin?.Label ?? "?",
                                     fp.Quantity))
                      .ToList());

        // 3) Color-Lookup fuer schoenere Anzeige der ColorName.
        var colors = (await _cache.GetAllColorsAsync()).ToDictionary(c => c.ColorId);

        // 4) Item-Names der Teile (best-effort aus dem Cache).
        var partNames = new Dictionary<string, string>();
        foreach (var s in subsets.Select(x => x.ItemNo).Distinct())
        {
            var item = await _cache.GetItemAsync("P", s);
            if (item != null) partNames[s] = item.Name;
        }

        // 5) Liste aufbauen
        Parts.Clear();
        foreach (var s in subsets.OrderBy(x => x.ItemNo).ThenBy(x => x.ColorId))
        {
            colors.TryGetValue(s.ColorId, out var color);
            partNames.TryGetValue(s.ItemNo, out var partName);

            // Wieviel Quantity haben wir tatsaechlich (capped auf Bedarf)
            int totalAvail = 0;
            string statusLabel;
            if (floatLookup.TryGetValue((s.ItemNo, s.ColorId), out var locs))
            {
                totalAvail = locs.Sum(x => x.Quantity);
                var binLabels = string.Join(", ", locs.Select(x => x.BinLabel).Distinct());
                statusLabel = totalAvail >= s.Quantity
                    ? $"Vorhanden ({totalAvail}x in {binLabels})"
                    : $"Teilweise vorhanden ({totalAvail}/{s.Quantity}x in {binLabels})";
            }
            else
            {
                statusLabel = "Fehlt";
            }

            Parts.Add(new BuildSuggestionPartViewModel
            {
                BlPartNo = s.ItemNo,
                ColorId = s.ColorId,
                PartName = partName ?? s.ItemNo,
                ColorName = color?.Name ?? $"Color {s.ColorId}",
                ColorRgb = color?.Rgb,
                QuantityNeeded = s.Quantity,
                QuantityHave = Math.Min(totalAvail, s.Quantity),
                StatusLabel = statusLabel,
                IsFullyAvailable = totalAvail >= s.Quantity
            });
        }

        OnPropertyChanged(nameof(TotalQuantityNeeded));
        OnPropertyChanged(nameof(TotalQuantityHave));
        OnPropertyChanged(nameof(AvailabilityLabel));

        // 6) Lagerfaecher fuellen - frei zuerst (sortiert), dann belegte.
        AvailableBins.Clear();
        var free = await binService.GetFreeAsync();
        var occupied = await binService.GetOccupiedAsync();
        foreach (var b in free) AvailableBins.Add(b);
        foreach (var b in occupied) AvailableBins.Add(b);

        // UX X.31 (v0.1.18): Default-Auswahl aus Suggest-Service holen, damit
        // Faecher mit Complete-Figuren NICHT als "frei" vorgeschlagen werden
        // (UX-X.6-Konvention). Aus AvailableBins per Reference-Equality holen,
        // damit die ComboBox den Eintrag findet.
        var suggested = await binService.SuggestBinForWaitingMinifigAsync();
        SelectedBin = suggested != null
            ? AvailableBins.FirstOrDefault(b => b.Id == suggested.Id) ?? AvailableBins.FirstOrDefault()
            : AvailableBins.FirstOrDefault();

        // 7) Bild im Hintergrund laden falls noch nicht da
        if (string.IsNullOrEmpty(ImageUrl))
        {
            try
            {
                var url = await _imageProvider.GetImageFileByBlAsync("M", BricklinkId, null);
                if (!string.IsNullOrEmpty(url)) ImageUrl = url;
            }
            catch
            {
                // Bild-Fehler ist nicht kritisch - Dialog funktioniert auch ohne.
            }
        }
    }
}

/// <summary>Ein Required-Part im Bauvorschlag-Dialog.</summary>
public partial class BuildSuggestionPartViewModel : ObservableObject
{
    public string BlPartNo { get; init; } = string.Empty;
    public int ColorId { get; init; }
    public string PartName { get; init; } = string.Empty;
    public string ColorName { get; init; } = string.Empty;
    public string? ColorRgb { get; init; }
    public int QuantityNeeded { get; init; }
    public int QuantityHave { get; init; }
    public string StatusLabel { get; init; } = string.Empty;
    public bool IsFullyAvailable { get; init; }

    public string DisplayLine =>
        $"{QuantityNeeded}x {PartName} ({ColorName}) [BL:{BlPartNo}]";
}
