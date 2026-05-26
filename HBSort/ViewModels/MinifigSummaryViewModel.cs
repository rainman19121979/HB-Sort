using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HBSort.Core.Database;
using HBSort.Core.Models;
using HBSort.Core.Services;
using HBSort.Helpers;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace HBSort.ViewModels;

/// <summary>
/// ViewModel fuer den MinifigSummaryDialog (Wartende-Liste-Klick).
/// Liest die Figur frisch aus der DB inkl. RequiredParts und berechnet die
/// Anzeige-Felder. Stellt zudem die "Verschieben"-Liste (alle Faecher) bereit.
///
/// UX X.20 Teil 7: ein einheitlicher Dialog fuer wartende UND komplette
/// Figuren. Der Verkaufsempfehlungs-Block wurde entfernt - die Live-Preise
/// leben in der MinifigPriceView im Sortier-Tab (Spalte 3 unten).
/// </summary>
public partial class MinifigSummaryViewModel : ObservableObject
{
    private readonly IDbContextFactory<UserDataContext> _ctxFactory;
    private readonly IStorageBinService _binService;
    private readonly IPartImageProvider? _imageProvider;
    private readonly IBlCatalogService? _catalog;
    private readonly IMinifigPersistenceService? _persistence;
    // v0.1.24-beta.8 Phase 3: BL-Inventar-Lookup fuer fehlende Teile.
    private readonly IBlInventoryService? _blInventory;

    public int MinifigId { get; }
    public string Name { get; private set; } = string.Empty;
    public string BricklinkId { get; private set; } = string.Empty;
    public string? ImageUrl { get; private set; }
    public string BinLabel { get; private set; } = string.Empty;
    public int? CurrentBinId { get; private set; }

    /// <summary>Status der Figur (fuer Loeschen-Confirmation und UI-Logik).</summary>
    public TrackedMinifigStatus Status { get; private set; } = TrackedMinifigStatus.Waiting;

    /// <summary>Phase 6: Visibility-Helper fuer Buttons im Summary-Dialog.</summary>
    public bool IsWaiting  => Status == TrackedMinifigStatus.Waiting;
    public bool IsComplete => Status == TrackedMinifigStatus.Complete;

    public string? Notes { get; private set; }
    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);
    public string NotesDisplay => string.IsNullOrWhiteSpace(Notes) ? string.Empty : $"📝 {Notes}";

    public int TotalParts { get; private set; }
    public int CompletedParts { get; private set; }
    public double ProgressFraction => TotalParts == 0 ? 0 : (double)CompletedParts / TotalParts;
    public string ProgressLabel => $"{CompletedParts} von {TotalParts} komplett";

    /// <summary>Teile mit Status fuer die Anzeige.</summary>
    public ObservableCollection<SummaryPartViewModel> Parts { get; } = new();

    /// <summary>
    /// Verfuegbare Faecher fuer "Verschieben" (ohne aktuelles Fach).
    /// v0.1.24-beta.1 Phase 2b: <see cref="BinDisplayItem"/> mit Belegungs-
    /// Suffix — Konsumenten ueber <c>MoveTarget.Bin.Id</c> / <c>.Bin.Label</c>.
    /// </summary>
    public ObservableCollection<BinDisplayItem> AvailableBins { get; } = new();

    [ObservableProperty]
    private BinDisplayItem? _moveTarget;

    public MinifigSummaryViewModel(
        int minifigId,
        IDbContextFactory<UserDataContext> ctxFactory,
        IStorageBinService binService,
        IPartImageProvider? imageProvider = null,
        IBlCatalogService? catalog = null,
        IMinifigPersistenceService? persistence = null,
        IBlInventoryService? blInventory = null)
    {
        MinifigId = minifigId;
        _ctxFactory = ctxFactory;
        _binService = binService;
        _imageProvider = imageProvider;
        _catalog = catalog;
        _persistence = persistence;
        _blInventory = blInventory;
    }

    /// <summary>Laed die Figur aus der DB inkl. Parts + alle anderen Faecher als Move-Targets.</summary>
    public async Task LoadAsync()
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync();
        var m = await ctx.TrackedMinifigs.AsNoTracking()
            .Where(x => x.Id == MinifigId)
            .Include(x => x.RequiredParts)
            .Include(x => x.StorageBin)
            .FirstOrDefaultAsync();
        if (m == null) return;

        Name = m.Name;
        BricklinkId = m.BricklinkId ?? m.FigNum;
        ImageUrl = m.LocalImagePath ?? m.ImageUrl;
        BinLabel = m.StorageBin?.Label ?? "(kein Fach)";
        CurrentBinId = m.StorageBinId;
        Status = m.Status;
        Notes = m.UserNotes;
        TotalParts = m.RequiredParts.Count;
        // v0.1.24-beta.8 Phase 3: effektiv-komplett (physisch + BL-reserviert).
        CompletedParts = m.RequiredParts.Count(p => p.IsEffectivelyComplete);

        Parts.Clear();
        foreach (var p in m.RequiredParts.OrderBy(x => x.PartName))
        {
            Parts.Add(new SummaryPartViewModel(p));
        }

        // Bilder + Color-Swatches asynchron im Hintergrund laden (best-effort).
        if (_imageProvider != null && _catalog != null)
        {
            _ = LoadPartImagesAndSwatchesAsync();
        }

        // v0.1.24-beta.8 Phase 3: BL-Inventar-Verfuegbarkeit pro fehlendem
        // Teil im Hintergrund laden. Best-effort, blocking nicht den Dialog.
        if (_blInventory != null)
        {
            _ = LoadBlAvailabilitiesAsync();
        }

        // Fallback: wenn LocalImagePath leer ist (alte Figuren oder beim
        // BL-Catalog-Collect nicht gespeichert), Bild nachladen + persistieren.
        if (string.IsNullOrEmpty(m.LocalImagePath) && _imageProvider != null)
        {
            _ = LoadAndPersistMinifigImageAsync(m.Id, m.BricklinkId ?? m.FigNum);
        }

        // Move-Targets: alle TYP-PASSENDEN Faecher AUSSER dem aktuellen.
        // v0.1.22-beta.3 (2026-05-13): vorher GetAllAsync, was auch Floating-
        // Bins als Move-Ziel angeboten hat - inkonsistent zum Volle-Faecher-
        // Banner. Jetzt typ-gefiltert je nach Status der Figur.
        var targetKind = Status == TrackedMinifigStatus.Complete
            ? BinTargetKind.CompleteMinifigTarget
            : BinTargetKind.WaitingMinifigTarget;
        AvailableBins.Clear();
        // v0.1.24-beta.1 Phase 2b: WithCounts-Variante fuer Combobox-Suffix.
        // excludeMinifigId=m.Id sorgt dafuer dass die zu verschiebende Figur
        // sich selbst nicht im Count zaehlt.
        var bins = await _binService.GetEligibleBinsWithCountsAsync(
            targetKind, excludeMinifigId: m.Id);
        foreach (var b in bins.Where(x => x.Bin.Id != CurrentBinId))
            AvailableBins.Add(new BinDisplayItem(b.Bin, BinDisplayFormatter.FormatBinDisplayText(b)));

        // Trigger property notifications fuer die berechneten Properties
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(BricklinkId));
        OnPropertyChanged(nameof(ImageUrl));
        OnPropertyChanged(nameof(BinLabel));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(IsWaiting));
        OnPropertyChanged(nameof(IsComplete));
        OnPropertyChanged(nameof(Notes));
        OnPropertyChanged(nameof(NotesDisplay));
        OnPropertyChanged(nameof(HasNotes));
        OnPropertyChanged(nameof(TotalParts));
        OnPropertyChanged(nameof(CompletedParts));
        OnPropertyChanged(nameof(ProgressFraction));
        OnPropertyChanged(nameof(ProgressLabel));
    }

    /// <summary>
    /// Best-effort Hintergrund-Loading: pro Teil Bild + Color-RGB.
    /// Aenderungen werden auf den UI-Thread gepostet.
    /// </summary>
    private async Task LoadPartImagesAndSwatchesAsync()
    {
        try
        {
            var allColors = _catalog == null ? new List<Core.Models.Bricklink.BlColor>()
                : await _catalog.GetAllColorsAsync();
            var colorMap = allColors.ToDictionary(c => c.ColorId);

            foreach (var p in Parts.ToList())
            {
                // Color-Swatch
                if (colorMap.TryGetValue(p.ColorId, out var color) && color.Rgb != null)
                {
                    var brush = ParseRgbBrush(color.Rgb);
                    Application.Current?.Dispatcher.Invoke(() => p.SwatchBrush = brush);
                }

                // Image
                if (_imageProvider != null)
                {
                    try
                    {
                        var url = await _imageProvider.GetImageFileByBlAsync("P", p.PartNumber, p.ColorId);
                        if (!string.IsNullOrEmpty(url))
                            Application.Current?.Dispatcher.Invoke(() => p.ImageUrl = url);
                    }
                    catch (Exception imgEx)
                    {
                        Log.Debug(imgEx, "Konnte Part-Bild nicht laden ({Part}/{Color})",
                            p.PartNumber, p.ColorId);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "LoadPartImagesAndSwatches geworfen");
        }
    }

    /// <summary>
    /// Best-effort: Header-Bild via PartImageProvider laden, in DB als
    /// LocalImagePath persistieren und das UI aktualisieren. Wird aufgerufen
    /// wenn LocalImagePath beim Laden noch leer war.
    /// </summary>
    private async Task LoadAndPersistMinifigImageAsync(int minifigId, string blId)
    {
        try
        {
            if (_imageProvider == null) return;
            var url = await _imageProvider.GetImageFileByBlAsync("M", blId, null);
            if (string.IsNullOrEmpty(url)) return;

            await using var ctx = await _ctxFactory.CreateDbContextAsync();
            var dbm = await ctx.TrackedMinifigs.FirstOrDefaultAsync(x => x.Id == minifigId);
            if (dbm == null) return;
            dbm.LocalImagePath = url;
            await ctx.SaveChangesAsync();

            // UI aktualisieren - ImageUrl ist ein normales Property mit
            // OnPropertyChanged-Trigger via Dialog-Binding.
            Application.Current?.Dispatcher.Invoke(() =>
            {
                ImageUrl = url;
                OnPropertyChanged(nameof(ImageUrl));
            });
            Log.Debug("MinifigSummary: LocalImagePath fuer {Bl} nachgeladen", blId);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "MinifigSummary: Bild fuer {Bl} nicht nachladbar", blId);
        }
    }

    /// <summary>
    /// v0.1.24-beta.8 Phase 3: Background-Load der BL-Inventar-Verfuegbarkeit
    /// pro fehlendem Required-Part. Best-effort - Fehler werden geloggt aber
    /// schluessen den Dialog nicht.
    /// </summary>
    private async Task LoadBlAvailabilitiesAsync()
    {
        if (_blInventory == null) return;
        try
        {
            // Erst pruefen ob ueberhaupt BL-Inventar synchronisiert ist -
            // ohne Sync sparen wir uns die per-Part-Queries komplett.
            if (!await _blInventory.HasAnyInventoryAsync()) return;

            // Snapshot der Parts-Liste damit wir nicht in eine Collection-
            // Modification-Race kommen falls LoadAsync nochmal laeuft.
            var snapshot = Parts.ToList();
            foreach (var partVm in snapshot)
            {
                if (!partVm.IsMissing) continue;
                try
                {
                    var lots = await _blInventory.FindLotsForPartAsync(
                        partVm.PartNumber, partVm.ColorId);
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

                    var disp = Application.Current?.Dispatcher;
                    if (disp != null)
                        await disp.InvokeAsync(() => partVm.BlAvailability = info);
                    else
                        partVm.BlAvailability = info;
                }
                catch (Exception ex)
                {
                    Log.Debug(ex,
                        "BL-Availability fuer {Part}/{Color} nicht ladbar",
                        partVm.PartNumber, partVm.ColorId);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "LoadBlAvailabilities geworfen");
        }
    }

    /// <summary>
    /// v0.1.24-beta.8 Phase 3: schliesst die Reservierungs-Schleife.
    /// Schritte (best-effort transactional via SaveChanges in einem Context):
    /// <list type="number">
    ///   <item>BL-Lot reservieren (<see cref="IBlInventoryService.ReserveAsync"/>) - kann
    ///   fehlschlagen wenn das Lot inzwischen weg / leer ist.</item>
    ///   <item>TrackedMinifigPart.QuantityReservedFromBl += 1.</item>
    ///   <item>ScanEvent (BlInventoryReservation) fuer Audit-Trail + Undo.</item>
    ///   <item>Komplett-Check ueber Effective (analog AssignPartToMinifig).</item>
    ///   <item>SummaryPartViewModel auf neuen Wert updaten.</item>
    /// </list>
    /// Liefert true wenn erfolgreich, false wenn das Lot nicht (mehr) reservierbar war.
    /// </summary>
    public async Task<bool> ApplyBlReservationAsync(SummaryPartViewModel partVm, BlInventoryLot lot)
    {
        if (_blInventory == null) return false;

        // 1) BL-Lot reservieren. Lock + ReservedQuantity++.
        var reserved = await _blInventory.ReserveAsync(lot.LotId, 1);
        if (!reserved) return false;

        // 2/3/4) Atomare Update der wartenden Figur + ScanEvent + Komplett-Check.
        try
        {
            await using var ctx = await _ctxFactory.CreateDbContextAsync();
            var dbPart = await ctx.TrackedMinifigParts
                .Include(p => p.TrackedMinifig)
                    .ThenInclude(m => m.RequiredParts)
                .FirstOrDefaultAsync(p => p.Id == partVm.Id);
            if (dbPart == null)
            {
                // Race-Edge: Teil existiert nicht mehr. Reservierung wieder freigeben.
                await _blInventory.ReleaseAsync(lot.LotId, 1);
                return false;
            }

            dbPart.QuantityReservedFromBl += 1;

            // Komplett-Check ueber Effective (physisch + BL).
            bool minifigCompleted = false;
            var allComplete = dbPart.TrackedMinifig.RequiredParts.All(p =>
                (p.Id == dbPart.Id
                    ? dbPart.QuantityCollected + dbPart.QuantityReservedFromBl
                    : p.QuantityCollected + p.QuantityReservedFromBl) >= p.QuantityNeeded);
            if (allComplete && dbPart.TrackedMinifig.Status != TrackedMinifigStatus.Complete)
            {
                dbPart.TrackedMinifig.Status = TrackedMinifigStatus.Complete;
                dbPart.TrackedMinifig.CompletedAt = DateTime.UtcNow;
                minifigCompleted = true;
            }

            var conditionLabel = lot.Condition == "N" ? "Neu" : "Gebraucht";
            // UndoData traegt die Verbindung (Part, Lot) damit der spaetere
            // Release weiss welches Lot zurueckzugeben ist.
            var undoSnapshot = new HBSort.Core.Services.UndoSnapshotBlReservation
            {
                TrackedMinifigPartId = dbPart.Id,
                LotId = lot.LotId
            };
            ctx.ScanEvents.Add(new ScanEvent
            {
                Timestamp = DateTime.UtcNow,
                Type = ScanType.BlInventoryReservation,
                RecognizedId = lot.LotId.ToString(),
                ResultDescription = minifigCompleted
                    ? $"BL-Reservierung: {dbPart.PartName} ({conditionLabel}) fuer '{dbPart.TrackedMinifig.Name}' - Figur komplett"
                    : $"BL-Reservierung: {dbPart.PartName} ({conditionLabel}) fuer '{dbPart.TrackedMinifig.Name}'",
                UndoData = System.Text.Json.JsonSerializer.Serialize(undoSnapshot),
                WasUndone = false
            });

            await ctx.SaveChangesAsync();

            // 5) UI auf neuen Stand.
            partVm.QuantityReservedFromBl = dbPart.QuantityReservedFromBl;
            if (minifigCompleted)
            {
                Status = TrackedMinifigStatus.Complete;
                CompletedParts = dbPart.TrackedMinifig.RequiredParts.Count(p => p.IsEffectivelyComplete);
                OnPropertyChanged(nameof(Status));
                OnPropertyChanged(nameof(IsWaiting));
                OnPropertyChanged(nameof(IsComplete));
                OnPropertyChanged(nameof(CompletedParts));
                OnPropertyChanged(nameof(ProgressLabel));
                OnPropertyChanged(nameof(ProgressFraction));
            }
            _persistence?.RaiseDataChanged();

            Log.Information(
                "BL-Reserve angewendet: Lot {LotId} -> Part {PartId} ({Reserved}/{Need}){Done}",
                lot.LotId, dbPart.Id, dbPart.QuantityReservedFromBl, dbPart.QuantityNeeded,
                minifigCompleted ? " => FIGUR KOMPLETT" : "");

            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ApplyBlReservation: DB-Update fehlgeschlagen - releasing Lot {LotId}", lot.LotId);
            // Bei DB-Fehler: BL-Reservierung wieder freigeben damit kein Geist-Lock bleibt.
            try { await _blInventory.ReleaseAsync(lot.LotId, 1); }
            catch (Exception relEx) { Log.Warning(relEx, "Release nach DB-Fehler ebenfalls geworfen"); }
            return false;
        }
    }

    /// <summary>
    /// v0.1.24-beta.8 Phase 3 (Fix 4): Alle BL-Reservierungen fuer ein Teil
    /// zuruecknehmen (LIFO ueber die ScanEvents). Wird vom Uncheck-Pfad
    /// aufgerufen wenn das Teil <c>QuantityReservedFromBl &gt; 0</c> hat.
    /// Schritte pro Reservierung:
    /// <list type="number">
    ///   <item><see cref="IBlInventoryService.ReleaseAsync"/>(lotId) - Shop-Bestand wieder freigeben.</item>
    ///   <item>Originalen ScanEvent als WasUndone+UndoneAt markieren.</item>
    ///   <item>Neuen BlInventoryRelease-ScanEvent fuer Audit-Trail.</item>
    /// </list>
    /// Am Ende: <c>part.QuantityReservedFromBl = 0</c>; falls die Figur
    /// vorher als Complete markiert war und es jetzt nicht mehr ist:
    /// zurueck auf Waiting + CompletedAt=null.
    /// </summary>
    public async Task<int> ReleaseAllBlReservationsAsync(SummaryPartViewModel partVm)
    {
        if (_blInventory == null || partVm.QuantityReservedFromBl <= 0) return 0;

        await using var ctx = await _ctxFactory.CreateDbContextAsync();
        var dbPart = await ctx.TrackedMinifigParts
            .Include(p => p.TrackedMinifig)
                .ThenInclude(m => m.RequiredParts)
            .FirstOrDefaultAsync(p => p.Id == partVm.Id);
        if (dbPart == null) return 0;

        // ScanEvents fuer Reservierungen dieses Teils (nicht-undone) finden.
        // Materialisierung in-memory weil UndoData JSON ist und SQLite das
        // nicht effizient filtern kann. Typisch sind < 5 Reservierungen pro Teil.
        var reservations = await ctx.ScanEvents
            .Where(e => e.Type == ScanType.BlInventoryReservation && !e.WasUndone)
            .ToListAsync();

        var matching = new List<(ScanEvent Event, HBSort.Core.Services.UndoSnapshotBlReservation Snap)>();
        foreach (var ev in reservations)
        {
            if (string.IsNullOrEmpty(ev.UndoData)) continue;
            try
            {
                var snap = System.Text.Json.JsonSerializer
                    .Deserialize<HBSort.Core.Services.UndoSnapshotBlReservation>(ev.UndoData);
                if (snap != null && snap.TrackedMinifigPartId == dbPart.Id)
                    matching.Add((ev, snap));
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "ScanEvent {Id} UndoData unparsable - skipping", ev.Id);
            }
        }

        if (matching.Count == 0)
        {
            // Dateninkonsistenz: Part sagt "X reserviert", aber kein passender
            // ScanEvent. Defensiv: einfach das Feld zuruecksetzen ohne Lot-
            // Release, sonst bleibt der User in einem nicht-aufloesbaren
            // Zustand stecken.
            Log.Warning("ReleaseAllBlReservations: Part {Id} hat QuantityReservedFromBl={Q} aber keine passenden ScanEvents - setze Feld auf 0",
                dbPart.Id, dbPart.QuantityReservedFromBl);
            dbPart.QuantityReservedFromBl = 0;
            await ctx.SaveChangesAsync();
            partVm.QuantityReservedFromBl = 0;
            return 0;
        }

        // LIFO: neueste Reservierung zuerst freigeben (intuitive Undo-Reihenfolge).
        int released = 0;
        bool wasComplete = dbPart.TrackedMinifig.Status == TrackedMinifigStatus.Complete;
        var now = DateTime.UtcNow;

        foreach (var (ev, snap) in matching.OrderByDescending(m => m.Event.Timestamp))
        {
            try
            {
                var ok = await _blInventory.ReleaseAsync(snap.LotId, 1);
                if (!ok)
                {
                    Log.Warning("ReleaseAllBlReservations: Lot {LotId} konnte nicht released werden - ScanEvent bleibt offen",
                        snap.LotId);
                    continue;
                }
                ev.WasUndone = true;
                ev.UndoneAt = now;

                ctx.ScanEvents.Add(new ScanEvent
                {
                    Timestamp = now,
                    Type = ScanType.BlInventoryRelease,
                    RecognizedId = snap.LotId.ToString(),
                    ResultDescription = $"BL-Reservierung aufgehoben: {dbPart.PartName} (Lot {snap.LotId}) " +
                                        $"fuer '{dbPart.TrackedMinifig.Name}'",
                    WasUndone = false
                });
                released++;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Release fuer Lot {LotId} geworfen", snap.LotId);
            }
        }

        // Part- und Figur-Stand anpassen.
        dbPart.QuantityReservedFromBl = Math.Max(0, dbPart.QuantityReservedFromBl - released);

        // Status zurueckrollen wenn die Figur durch das Release nicht mehr komplett ist.
        bool isStillComplete = dbPart.TrackedMinifig.RequiredParts.All(p =>
            (p.Id == dbPart.Id
                ? dbPart.QuantityCollected + dbPart.QuantityReservedFromBl
                : p.QuantityCollected + p.QuantityReservedFromBl) >= p.QuantityNeeded);
        if (wasComplete && !isStillComplete)
        {
            dbPart.TrackedMinifig.Status = TrackedMinifigStatus.Waiting;
            dbPart.TrackedMinifig.CompletedAt = null;
        }

        await ctx.SaveChangesAsync();

        // UI-Stand spiegeln.
        partVm.QuantityReservedFromBl = dbPart.QuantityReservedFromBl;
        if (wasComplete && !isStillComplete)
        {
            Status = TrackedMinifigStatus.Waiting;
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(IsWaiting));
            OnPropertyChanged(nameof(IsComplete));
        }
        CompletedParts = dbPart.TrackedMinifig.RequiredParts.Count(p => p.IsEffectivelyComplete);
        OnPropertyChanged(nameof(CompletedParts));
        OnPropertyChanged(nameof(ProgressLabel));
        OnPropertyChanged(nameof(ProgressFraction));

        _persistence?.RaiseDataChanged();
        Log.Information("ReleaseAllBlReservations: Part {Id} - {Count} Reservierungen freigegeben", dbPart.Id, released);
        return released;
    }

    private static Brush ParseRgbBrush(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return Brushes.Gray;
        var clean = hex.TrimStart('#');
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

    /// <summary>
    /// Verschiebt die Figur in ein anderes Fach.
    /// v0.1.23 (A9, S20-Refactor): routet ueber
    /// <see cref="IMinifigPersistenceService.MoveSelectionAsync"/> statt
    /// inline-DB-Write. Strict-Mode, RecalcKind, Undo-Snapshot und
    /// FreedAt-Reset sind zentral im Service. Test-Pfade ohne Persistence-
    /// Service fallen auf das alte inline-Verhalten zurueck.
    /// </summary>
    public async Task<bool> MoveToAsync(int newBinId)
    {
        if (_persistence != null)
        {
            try
            {
                var (mCount, _) = await _persistence.MoveSelectionAsync(
                    new[] { MinifigId }, Array.Empty<int>(), newBinId);
                Log.Information(
                    "Minifigur Id={Id} via MoveSelectionAsync in Fach {Bin} verschoben ({Count} Eintraege)",
                    MinifigId, newBinId, mCount);
                return mCount > 0;
            }
            catch (InvalidBinKindException strict)
            {
                Log.Warning(strict,
                    "MoveToAsync: Strict-Mode-Verletzung beim Verschieben von Figur {Id} nach Bin {Bin}",
                    MinifigId, newBinId);
                throw;
            }
        }

        // Legacy-Pfad fuer Tests ohne Persistence-Service.
        await using var ctx = await _ctxFactory.CreateDbContextAsync();
        var m = await ctx.TrackedMinifigs.FirstOrDefaultAsync(x => x.Id == MinifigId);
        if (m == null) return false;

        var bin = await ctx.StorageBins.FirstOrDefaultAsync(b => b.Id == newBinId);
        if (bin == null)
            throw new InvalidOperationException($"Lagerfach {newBinId} existiert nicht.");
        if (bin.FreedAt != null)
        {
            Log.Information("Fach '{Label}' war als frei markiert (seit {FreedAt}) - wird durch verschobene Figur wieder belegt",
                bin.Label, bin.FreedAt);
            bin.FreedAt = null;
        }

        var oldBinId = m.StorageBinId;
        var moveSnapshot = new HBSort.Core.Services.UndoSnapshotMove
        {
            MinifigId = m.Id,
            OldStorageBinId = oldBinId,
            NewStorageBinId = newBinId
        };
        ctx.ScanEvents.Add(new HBSort.Core.Models.ScanEvent
        {
            Timestamp = DateTime.UtcNow,
            Type = HBSort.Core.Models.ScanType.Move,
            RecognizedId = m.BricklinkId ?? m.FigNum,
            ResultDescription = $"Figur '{m.Name}' verschoben in Fach '{bin.Label}'",
            WasUndone = false,
            UndoData = System.Text.Json.JsonSerializer.Serialize(moveSnapshot)
        });

        m.StorageBinId = newBinId;
        await ctx.SaveChangesAsync();
        Log.Information("Minifigur '{Name}' (Id={Id}) in Fach {Bin} verschoben (Legacy-Pfad)",
            m.Name, m.Id, newBinId);
        return true;
    }
}

/// <summary>Eine Teile-Zeile im Summary-Dialog.</summary>
public partial class SummaryPartViewModel : ObservableObject
{
    public int Id { get; }
    public string PartName { get; }
    public string PartNumber { get; }
    public int ColorId { get; }
    public string ColorName { get; }
    public int QuantityNeeded { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompleted))]
    [NotifyPropertyChangedFor(nameof(QuantityLabel))]
    [NotifyPropertyChangedFor(nameof(EffectiveCollected))]
    [NotifyPropertyChangedFor(nameof(IsMissing))]
    [NotifyPropertyChangedFor(nameof(ShowBlBadge))]
    private int _quantityCollected;

    /// <summary>
    /// v0.1.24-beta.8 Phase 3: Menge die im BL-Shop reserviert wurde
    /// (liegt physisch noch im Store). Wird beim Reserve-Klick erhoeht.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompleted))]
    [NotifyPropertyChangedFor(nameof(QuantityLabel))]
    [NotifyPropertyChangedFor(nameof(EffectiveCollected))]
    [NotifyPropertyChangedFor(nameof(IsMissing))]
    [NotifyPropertyChangedFor(nameof(HasBlReservation))]
    [NotifyPropertyChangedFor(nameof(ShowBlBadge))]
    private int _quantityReservedFromBl;

    /// <summary>BL-Bild des Teils (URL/Pfad) - wird vom Parent-VM async befuellt.</summary>
    [ObservableProperty]
    private string? _imageUrl;

    /// <summary>Color-Swatch - wird vom Parent-VM async aus bl_colors befuellt.</summary>
    [ObservableProperty]
    private Brush _swatchBrush = Brushes.Gray;

    /// <summary>
    /// v0.1.24-beta.8 Phase 3: BL-Inventar-Verfuegbarkeit fuer dieses Teil.
    /// Wird async vom Parent-VM nach Load befuellt. Null = noch nicht
    /// geprueft. Empty (HasAny=false) = geprueft, nichts gefunden.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBlBadge))]
    private BlAvailabilityInfo? _blAvailability;

    /// <summary>Badge nur sichtbar wenn Teil effektiv fehlt UND BL was anbietet.</summary>
    public bool ShowBlBadge => IsMissing && BlAvailability != null && BlAvailability.HasAny;

    /// <summary>
    /// Effektiv beschafft (physisch + BL-reserviert) — Basis fuer
    /// IsCompleted und QuantityLabel-Darstellung.
    /// </summary>
    public int EffectiveCollected => QuantityCollected + QuantityReservedFromBl;
    public bool IsCompleted => EffectiveCollected >= QuantityNeeded;
    public bool IsMissing => EffectiveCollected < QuantityNeeded;
    public bool HasBlReservation => QuantityReservedFromBl > 0;

    /// <summary>"3/5" oder "3 (+1 BL)/5" wenn Reservierung vorhanden.</summary>
    public string QuantityLabel => QuantityReservedFromBl > 0
        ? $"{QuantityCollected} (+{QuantityReservedFromBl} BL)/{QuantityNeeded}"
        : $"{QuantityCollected}/{QuantityNeeded}";

    public SummaryPartViewModel(TrackedMinifigPart p)
    {
        Id = p.Id;
        PartName = p.PartName;
        PartNumber = p.PartNumber;
        ColorId = p.ColorId;
        ColorName = p.ColorName;
        QuantityNeeded = p.QuantityNeeded;
        _quantityCollected = p.QuantityCollected;
        _quantityReservedFromBl = p.QuantityReservedFromBl;
    }
}

/// <summary>
/// v0.1.24-beta.8 Phase 3: BL-Inventar-Verfuegbarkeit pro Teil.
/// Wird async vom MinifigSummaryViewModel berechnet (FindLotsForPartAsync
/// liefert die Lots). Aufgeteilt nach Condition damit der Reservierungs-
/// Dialog "Neu/Gebraucht"-Buttons getrennt anbieten kann.
/// </summary>
public sealed class BlAvailabilityInfo
{
    public int NewQty { get; init; }
    public int UsedQty { get; init; }
    public IReadOnlyList<BlInventoryLot> NewLots { get; init; } = Array.Empty<BlInventoryLot>();
    public IReadOnlyList<BlInventoryLot> UsedLots { get; init; } = Array.Empty<BlInventoryLot>();

    public bool HasAny => NewQty + UsedQty > 0;
    public bool HasNew => NewQty > 0;
    public bool HasUsed => UsedQty > 0;

    public string BadgeText => "BL-Shop";
    public string BadgeTooltip => $"Im BL-Shop verfuegbar: {NewQty}× Neu, {UsedQty}× Gebraucht. Klicken um zu reservieren.";
}
