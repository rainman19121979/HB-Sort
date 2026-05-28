using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using HBSort.Core.Database;
using HBSort.Core.Models;
using HBSort.Core.Services;
using HBSort.Helpers;
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
    private readonly IBlInventoryService? _blInventory;

    public string BricklinkId { get; }
    public string Name { get; }
    public int? YearReleased { get; }

    [ObservableProperty]
    private string? _imageUrl;

    /// <summary>Required-Parts der Figur mit "Vorhanden / Fehlt"-Status.</summary>
    public ObservableCollection<BuildSuggestionPartViewModel> Parts { get; } = new();

    /// <summary>
    /// Lagerfach-Auswahl. v0.1.24-beta.1 Phase 2b: <see cref="BinDisplayItem"/>
    /// statt rohem <see cref="StorageBin"/> — die Combobox zeigt jetzt
    /// "Box 005 (2 wartend)" statt nur "Box 005". Konsumenten greifen ueber
    /// <c>SelectedBin.Bin.Id</c> / <c>SelectedBin.Bin.Label</c> auf das Original.
    /// </summary>
    public ObservableCollection<BinDisplayItem> AvailableBins { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreate))]
    private BinDisplayItem? _selectedBin;

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

    /// <summary>
    /// v0.1.24-beta.11 Optik-Vereinheitlichung: Fortschritts-Anzeige im
    /// Header (analog MinifigSummaryDialog). Wert 0.0-1.0, fuer ProgressBar.
    /// </summary>
    public double ProgressFraction
    {
        get
        {
            var need = TotalQuantityNeeded;
            if (need <= 0) return 0;
            var have = TotalQuantityHave;
            return Math.Min(1.0, (double)have / need);
        }
    }

    /// <summary>"X von Y direkt verfuegbar" - Header-Text neben dem Fortschritt.</summary>
    public string ProgressLabel => $"{TotalQuantityHave} von {TotalQuantityNeeded} direkt verfuegbar";

    /// <summary>Bin-Label fuer den Header (SelectedBin oder "(noch nicht gewaehlt)").</summary>
    public string BinLabel => SelectedBin?.Bin.Label ?? "(noch nicht gewaehlt)";

    partial void OnSelectedBinChanged(BinDisplayItem? value) => OnPropertyChanged(nameof(BinLabel));

    public bool CanCreate => SelectedBin != null && !IsCreating;

    public BuildSuggestionDetailViewModel(
        string bricklinkId,
        string name,
        int? yearReleased,
        string? imageUrl,
        IBlCacheRepository cache,
        IDbContextFactory<UserDataContext> ctxFactory,
        IPartImageProvider imageProvider,
        IBlInventoryService? blInventory = null)
    {
        BricklinkId = bricklinkId;
        Name = name;
        YearReleased = yearReleased;
        ImageUrl = imageUrl;
        _cache = cache;
        _ctxFactory = ctxFactory;
        _imageProvider = imageProvider;
        _blInventory = blInventory;
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

            var hbHave = Math.Min(totalAvail, s.Quantity);
            Parts.Add(new BuildSuggestionPartViewModel
            {
                BlPartNo = s.ItemNo,
                ColorId = s.ColorId,
                PartName = partName ?? s.ItemNo,
                ColorName = color?.Name ?? $"Color {s.ColorId}",
                ColorRgb = color?.Rgb,
                QuantityNeeded = s.Quantity,
                QuantityHave = hbHave,
                StatusLabel = statusLabel,
                IsFullyAvailable = totalAvail >= s.Quantity
            });
        }

        OnPropertyChanged(nameof(TotalQuantityNeeded));
        OnPropertyChanged(nameof(TotalQuantityHave));
        OnPropertyChanged(nameof(AvailabilityLabel));
        OnPropertyChanged(nameof(ProgressFraction));
        OnPropertyChanged(nameof(ProgressLabel));

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
        // v0.1.24-beta.1 Phase 2b: GetEligibleBinsWithCountsAsync + Suffix-
        // Formatter (Konzept B2 / OPEN-7). Suffix wie "(2 wartend)" macht
        // die Combobox selbstklaerend.
        var eligible = await binService.GetEligibleBinsWithCountsAsync(binTargetKind);
        foreach (var b in eligible)
            AvailableBins.Add(new BinDisplayItem(b.Bin, BinDisplayFormatter.FormatBinDisplayText(b)));
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

        // v0.1.24-beta.11: Lazy Thumbnails + BL-Availability pro Part.
        // Fire-and-forget — UI rendert die Karten sofort, Bilder + Badges
        // erscheinen wenn fertig.
        _ = LoadPartImagesAsync();
        if (_blInventory != null) _ = LoadBlAvailabilitiesAsync();
    }

    /// <summary>
    /// v0.1.24-beta.11: pro Part Thumbnail via IPartImageProvider holen
    /// und auf ImageUrl setzen. Sequenziell — bei typisch &lt;20 Required-
    /// Parts ist Parallelisierung overkill.
    /// </summary>
    private async Task LoadPartImagesAsync()
    {
        try
        {
            var disp = System.Windows.Application.Current?.Dispatcher;
            foreach (var p in Parts.ToList())
            {
                if (!string.IsNullOrEmpty(p.ImageUrl)) continue;
                try
                {
                    var url = await _imageProvider.GetImageFileByBlAsync("P", p.BlPartNo, p.ColorId);
                    if (string.IsNullOrEmpty(url)) continue;
                    if (disp != null)
                        await disp.InvokeAsync(() => p.ImageUrl = url);
                    else
                        p.ImageUrl = url;
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "BuildSuggestionDetail Image-Load fuer {Part}/{Color} fehlgeschlagen",
                        p.BlPartNo, p.ColorId);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "LoadPartImages geworfen");
        }
    }

    /// <summary>
    /// v0.1.24-beta.11: pro Part BL-Lot-Availability lookup via
    /// IBlInventoryService. Best-effort.
    /// </summary>
    private async Task LoadBlAvailabilitiesAsync()
    {
        if (_blInventory == null) return;
        try
        {
            if (!await _blInventory.HasAnyInventoryAsync()) return;
            var disp = System.Windows.Application.Current?.Dispatcher;
            foreach (var p in Parts.ToList())
            {
                try
                {
                    var lots = await _blInventory.FindLotsForPartAsync(p.BlPartNo, p.ColorId);
                    if (lots.Count == 0) continue;

                    var newLots = lots.Where(l => l.Condition == "N").ToList();
                    var usedLots = lots.Where(l => l.Condition == "U").ToList();
                    var info = new BlAvailabilityInfo
                    {
                        NewQty = newLots.Sum(l => l.Quantity - l.ReservedQuantity),
                        UsedQty = usedLots.Sum(l => l.Quantity - l.ReservedQuantity),
                        NewLots = newLots,
                        UsedLots = usedLots
                    };
                    if (disp != null)
                        await disp.InvokeAsync(() => p.BlAvailability = info);
                    else
                        p.BlAvailability = info;
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "BuildSuggestionDetail BL-Availability fuer {Part}/{Color} fehlgeschlagen",
                        p.BlPartNo, p.ColorId);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "LoadBlAvailabilities geworfen");
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

    /// <summary>Legacy-Label fuer alte Render-Pfade; in beta.11 nicht mehr verwendet.</summary>
    public string DisplayLine =>
        $"{QuantityNeeded}x {PartName} ({ColorName}) [BL:{BlPartNo}]";

    /// <summary>"X/Y" - kompakter Quantity-Indikator analog SummaryPartViewModel.QuantityLabel.</summary>
    public string QuantityLabel => $"{QuantityHave}/{QuantityNeeded}";

    // ============================================================
    // v0.1.24-beta.11: Lazy Thumbnail + Quellen-Auswahl pro Teil
    // ============================================================

    /// <summary>Lokal gecachter Bild-Pfad. Wird vom Parent-VM async befuellt.</summary>
    [ObservableProperty]
    private string? _imageUrl;

    /// <summary>True wenn FloatingParts &gt;= 1 fuer dieses (PartNo, ColorId) existieren.</summary>
    public bool HasInternal => QuantityHave > 0;

    /// <summary>
    /// BL-Inventar-Verfuegbarkeit fuer dieses Teil. Wird vom Parent-VM async
    /// gefuellt (FindLotsForPartAsync). Null = noch nicht geprueft.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBlShop))]
    [NotifyPropertyChangedFor(nameof(ShowBlBadge))]
    [NotifyPropertyChangedFor(nameof(ShowMissingLabel))]
    private BlAvailabilityInfo? _blAvailability;

    public bool HasBlShop => BlAvailability != null && BlAvailability.HasAny;

    /// <summary>
    /// Vom User gewaehltes BL-Lot fuer dieses Teil. Null = nicht aus BL.
    /// Wird ueber den BlReserveDialog gesetzt.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsResolved))]
    [NotifyPropertyChangedFor(nameof(ShowBlBadge))]
    [NotifyPropertyChangedFor(nameof(ShowReservedPill))]
    [NotifyPropertyChangedFor(nameof(ShowMissingLabel))]
    [NotifyPropertyChangedFor(nameof(ReservedLotDisplay))]
    [NotifyPropertyChangedFor(nameof(HasReservation))]
    private HBSort.Core.Models.BlInventoryLot? _reservedLot;

    /// <summary>True wenn ein BL-Lot fuer dieses Teil reserviert wurde.</summary>
    public bool HasReservation => ReservedLot != null;

    /// <summary>
    /// v0.1.24-beta.11 (Spec-Anpassung): Quelle wird automatisch bestimmt -
    /// HasInternal hat Vorrang, sonst BL-Shop wenn reserviert, sonst Fehlt.
    /// Kein User-Toggle mehr (vor dem Refactor war hier eine UseInternal-
    /// Checkbox; sie wurde entfernt weil der Shop-Button ohnehin nur ohne
    /// internen Bestand sichtbar ist).
    /// </summary>
    public bool IsResolved => HasInternal || ReservedLot != null;

    /// <summary>
    /// BL-Shop-Badge sichtbar: intern KEIN Bestand, im Shop verfuegbar, noch nicht reserviert.
    /// </summary>
    public bool ShowBlBadge => !HasInternal && HasBlShop && ReservedLot == null;

    /// <summary>Nach Klick auf BL-Badge wurde ein Lot reserviert: kompakte "Shop"-Pill mit Reset.</summary>
    public bool ShowReservedPill => ReservedLot != null;

    /// <summary>"Fehlt"-Label sichtbar: weder intern noch im Shop verfuegbar.</summary>
    public bool ShowMissingLabel => !HasInternal && !HasBlShop && ReservedLot == null;

    /// <summary>Kompakte Anzeige des reservierten Lots fuer den Karten-Fuss.</summary>
    public string ReservedLotDisplay => ReservedLot == null
        ? string.Empty
        : (string.IsNullOrWhiteSpace(ReservedLot.Remarks) ? "(kein Lagerplatz)" : ReservedLot.Remarks!);

    /// <summary>v0.1.24-beta.11: Farb-Swatch (16x16) im Karten-Stil — direkt aus ColorRgb hergeleitet.</summary>
    public Brush SwatchBrush
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ColorRgb)) return Brushes.Gray;
            var clean = ColorRgb.TrimStart('#');
            if (clean.Length != 6) return Brushes.Gray;
            try
            {
                var r = byte.Parse(clean.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var g = byte.Parse(clean.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var b = byte.Parse(clean.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
                brush.Freeze();
                return brush;
            }
            catch { return Brushes.Gray; }
        }
    }
}
