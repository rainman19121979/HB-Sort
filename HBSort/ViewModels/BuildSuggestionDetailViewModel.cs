using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using HBSort.Core.Database;
using HBSort.Core.Models;
using HBSort.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

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

    /// <summary>
    /// UX X.33 v0.1.19-beta.7 Block O.5 (Pre-Tag): Volle-Faecher-Banner-Text.
    /// Wird in <see cref="LoadAsync"/> gesetzt wenn der Suggest-Service kein
    /// passendes Fach findet. Der Dialog rendert ueber dem Bin-Dropdown einen
    /// orangen Banner mit "Lagerfach-Verwaltung oeffnen"-Button. Konsistent
    /// zum Block-K-Muster aus PendingMinifigViewModel + PartLookupViewModel.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoFreeBinWarning))]
    private string? _noFreeBinWarning;

    public bool HasNoFreeBinWarning => !string.IsNullOrEmpty(NoFreeBinWarning);

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
        // v0.1.22-beta.1 Block B: Profiling. Misst die N+1 in der
        // partNames-Schleife unten (pro Part ein _cache.GetItemAsync).
        var swTotal = Stopwatch.StartNew();
        var swPartNames = new Stopwatch();
        int partNameCalls = 0;

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
        swPartNames.Start();
        var partNames = new Dictionary<string, string>();
        foreach (var s in subsets.Select(x => x.ItemNo).Distinct())
        {
            partNameCalls++;
            var item = await _cache.GetItemAsync("P", s);
            if (item != null) partNames[s] = item.Name;
        }
        swPartNames.Stop();

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

        // UX X.33 v0.1.19-beta.7 Block L: wenn schon alle Required-Parts
        // im Floating-Pool liegen, wird die Figur durch den Reverse-Match
        // SOFORT Complete - dann muss der Default-Bin auch nach Complete-
        // Logik (MaxCompleteFiguresPerBin) gewaehlt werden. Sonst landet die
        // Figur in einem Fach mit anderen Wartenden. Limits via App.Services -
        // der ViewModel-Konstruktor hat keinen ISettingsService, ist aber
        // innerhalb der App-DI sicher erreichbar.
        var settings = App.Services.GetRequiredService<HBSort.Core.Services.ISettingsService>();
        var willBeComplete = Parts.Count > 0 && Parts.All(p => p.IsFullyAvailable);

        // 6) Lagerfaecher fuellen - v0.1.22-beta.3 (2026-05-13): typ-gefiltert
        //    je nach willBeComplete-Vorhersage (Reifungspfad-Mix wartend+complete
        //    ist via GetEligibleBinsAsync intern erlaubt). Vorher GetFreeAsync +
        //    GetOccupiedAsync - das hat auch FloatingOnly-Bins als Move-Ziel
        //    angeboten, inkonsistent zum Volle-Faecher-Banner.
        var binTargetKind = willBeComplete
            ? BinTargetKind.CompleteMinifigTarget
            : BinTargetKind.WaitingMinifigTarget;
        AvailableBins.Clear();
        var eligible = await binService.GetEligibleBinsAsync(binTargetKind);
        foreach (var b in eligible) AvailableBins.Add(b);
        StorageBin? suggested;
        if (willBeComplete)
        {
            var maxComplete = settings.Current.MaxCompleteFiguresPerBin;
            suggested = await binService.SuggestBinForCompleteMinifigAsync(maxComplete);
        }
        else
        {
            var maxWaiting = settings.Current.MaxWaitingFiguresPerBin;
            suggested = await binService.SuggestBinForWaitingMinifigAsync(maxWaiting);
        }
        // UX X.33 v0.1.19-beta.7 Block K: bei null vom Service NICHT mehr
        // auf AvailableBins.FirstOrDefault fallback - User soll bewusst
        // ein Fach aus der Combobox waehlen oder erst ein neues anlegen.
        // Dialog-Button "Figur anlegen" ist via CanCreate disabled solange
        // SelectedBin null bleibt.
        //
        // UX X.33 v0.1.19-beta.7 Block O.5 (Pre-Tag): zusaetzlich Volle-
        // Faecher-Banner setzen damit der User SIEHT warum kein Vorschlag
        // kommt. Vorher leeres Dropdown ohne Erklaerung.
        if (suggested != null)
        {
            SelectedBin = AvailableBins.FirstOrDefault(b => b.Id == suggested.Id);
            NoFreeBinWarning = null;
        }
        else
        {
            SelectedBin = null;
            NoFreeBinWarning = AvailableBins.Count == 0
                ? "Es gibt noch keine Lagerfaecher. Lege zuerst eines an."
                : "Alle Lagerfaecher sind belegt. Bitte ein neues Fach anlegen oder ein bestehendes leeren.";
        }

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

        swTotal.Stop();
        Log.Information(
            "[PROFILE] BuildSuggestionDetail.LoadAsync {Bl}: total={Total}ms, " +
            "partNames={Pn}ms ueber {PnCalls}, parts={Parts}",
            BricklinkId, swTotal.ElapsedMilliseconds,
            swPartNames.ElapsedMilliseconds, partNameCalls, Parts.Count);
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
