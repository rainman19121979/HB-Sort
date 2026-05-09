using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using HBSort.Core.Database;
using HBSort.Core.Models;
using HBSort.Core.Services;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace HBSort.ViewModels;

/// <summary>
/// Wizard-VM fuer das Aufgeben einer Figur. Zeigt pro Required-Part eine
/// Checkbox + Ziel-Fach-Auswahl. Default: alle Teile ankreuzen, Ziel-Fach
/// = aktuelles Fach der Figur (oder erstes freies).
/// </summary>
public partial class DismantleWizardViewModel : ObservableObject
{
    private readonly IDbContextFactory<UserDataContext> _ctxFactory;
    private readonly IStorageBinService _binService;
    private readonly IMinifigPersistenceService _persistence;
    private readonly IPartImageProvider? _imageProvider;
    private readonly IBlCatalogService? _catalog;
    private readonly IPartLookupService? _partLookup;

    public int TrackedMinifigId { get; }

    [ObservableProperty] private string _minifigName = string.Empty;
    [ObservableProperty] private string _bricklinkId = string.Empty;
    [ObservableProperty] private bool _isLoading;

    public ObservableCollection<DismantlePartItemViewModel> Parts { get; } = new();
    public ObservableCollection<StorageBin> AvailableBins { get; } = new();

    /// <summary>Default-Bin fuer "Auf alle anwenden".</summary>
    [ObservableProperty]
    private StorageBin? _defaultBin;

    public DismantleWizardViewModel(
        int trackedMinifigId,
        IDbContextFactory<UserDataContext> ctxFactory,
        IStorageBinService binService,
        IMinifigPersistenceService persistence,
        IPartImageProvider? imageProvider = null,
        IBlCatalogService? catalog = null,
        IPartLookupService? partLookup = null)
    {
        TrackedMinifigId = trackedMinifigId;
        _ctxFactory = ctxFactory;
        _binService = binService;
        _persistence = persistence;
        _imageProvider = imageProvider;
        _catalog = catalog;
        _partLookup = partLookup;
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            // Figur + Required-Parts + aktuelles Fach holen
            await using var ctx = await _ctxFactory.CreateDbContextAsync();
            var m = await ctx.TrackedMinifigs.AsNoTracking()
                .Include(x => x.RequiredParts)
                .Include(x => x.StorageBin)
                .FirstOrDefaultAsync(x => x.Id == TrackedMinifigId);
            if (m == null) return;

            MinifigName = m.Name;
            BricklinkId = m.BricklinkId ?? m.FigNum;

            // Lagerfaecher fuer Combobox laden
            var bins = await _binService.GetAllAsync();
            AvailableBins.Clear();
            foreach (var b in bins) AvailableBins.Add(b);

            // Default-Bin: aktuelles Fach der Figur, sonst erstes freies
            StorageBin? targetBin = m.StorageBinId.HasValue
                ? AvailableBins.FirstOrDefault(b => b.Id == m.StorageBinId.Value)
                : null;
            if (targetBin == null)
            {
                var firstFree = await _binService.GetNextFreeAsync();
                if (firstFree != null)
                    targetBin = AvailableBins.FirstOrDefault(b => b.Id == firstFree.Id);
            }
            DefaultBin = targetBin ?? AvailableBins.FirstOrDefault();

            Parts.Clear();

            // UX X.32 Bug-Fix v0.1.19-beta.3: Wizard-Session-State, damit
            // verschiedene Teile auf verschiedene frische Bins verteilt
            // werden. Stapel-Bins (Step 1: gleiche PartNo+ColorId schon im
            // Pool) bleiben als wiederverwendbar - mehrere Teile vom gleichen
            // Typ landen im gleichen Stapel.
            var usedBinsInThisSession = new HashSet<int>();
            var lastPickWasStackBin = false;
            int? lastNonStackBinId = null;

            foreach (var p in m.RequiredParts.OrderBy(p => p.PartName))
            {
                var partVm = new DismantlePartItemViewModel(p)
                {
                    // Smart-Default: nur tatsaechlich gesammelte Teile vor-aktivieren.
                    // Nicht-gesammelte Teile sind im Wizard deaktiviert (User kann
                    // bei Bedarf manuell ankreuzen, dann werden QuantityNeeded uebernommen).
                    IsKept = p.QuantityCollected > 0,
                };

                // UX X.32 Block A (v0.1.19) + Bug-Fix v0.1.19-beta.2/beta.3:
                // pro Teil INDIVIDUELLER Default-Bin via PickPerPartBin
                // (mit Wizard-Session-State + excludeMinifigId).
                StorageBin? perPartBin = null;
                lastPickWasStackBin = false;
                lastNonStackBinId = null;
                try
                {
                    var (bin, wasStack) = await PickPerPartBinAsync(
                        p.PartNumber, p.ColorId,
                        usedBinsInThisSession,
                        excludeMinifigId: TrackedMinifigId);
                    perPartBin = bin;
                    lastPickWasStackBin = wasStack;
                    if (perPartBin != null && !wasStack)
                    {
                        usedBinsInThisSession.Add(perPartBin.Id);
                        lastNonStackBinId = perPartBin.Id;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex,
                        "PickPerPartBin fehlgeschlagen fuer {Part}/{Color}, Fallback DefaultBin",
                        p.PartNumber, p.ColorId);
                }
                partVm.TargetBin = perPartBin ?? DefaultBin;

                // Smart-Sammeln: ist das gleiche Teil schon irgendwo als Einzelteil
                // gelagert? Dann das Fach mit der hoechsten Menge vor-auswaehlen
                // damit Bestaende beim Speichern zusammengefuehrt werden.
                if (_partLookup != null)
                {
                    try
                    {
                        Log.Information("SmartHint-Lookup: PartNo={PartNo}, ColorId={ColorId}",
                            p.PartNumber, p.ColorId);

                        var locations = await _partLookup.FindFloatingLocationsAsync(
                            p.PartNumber, p.ColorId);

                        Log.Information("SmartHint-Lookup result: {Count} Locations gefunden", locations.Count);
                        foreach (var loc in locations)
                        {
                            Log.Information("  -> Bin {Id} '{Label}': {Qty} Stueck",
                                loc.StorageBinId, loc.StorageBinLabel, loc.TotalQuantity);
                        }

                        if (locations.Count > 0)
                        {
                            var best = locations[0]; // sortiert nach Menge absteigend
                            var bin = AvailableBins.FirstOrDefault(b => b.Id == best.StorageBinId);
                            if (bin != null)
                            {
                                // UX X.32 Bug-Fix v0.1.19-beta.3: SmartHint
                                // schaltet auf einen Stapel-Bin um. Falls der
                                // vorige Pick ein FRISCHES Bin war (in usedBins
                                // eingetragen), nehmen wir's wieder raus -
                                // sonst blockiert es das naechste Teil
                                // unnoetig.
                                if (!lastPickWasStackBin && lastNonStackBinId.HasValue)
                                {
                                    usedBinsInThisSession.Remove(lastNonStackBinId.Value);
                                    lastNonStackBinId = null;
                                }
                                partVm.TargetBin = bin;
                                partVm.SmartHint = $"+{best.TotalQuantity} schon dort";
                                lastPickWasStackBin = true;
                            }
                            else
                            {
                                Log.Warning("SmartHint: Bin Id={BinId} nicht in AvailableBins gefunden",
                                    best.StorageBinId);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Debug(ex, "SmartHint-Lookup fuer {Part}/{Color} fehlgeschlagen",
                            p.PartNumber, p.ColorId);
                    }
                }

                Parts.Add(partVm);
            }

            // Bilder + Color-Swatches im Hintergrund (best-effort).
            if (_imageProvider != null && _catalog != null)
            {
                _ = LoadPartImagesAndSwatchesAsync();
            }

            // UX X.25: pro Part die wartenden Figuren laden die das Teil noch
            // brauchen. Eigene Figur ausschliessen (kann sich nicht selbst
            // zuordnen). Best-effort - bei Fehler bleibt WaitingMatches leer
            // und der RadioButton-Pfad wird einfach nicht angezeigt.
            if (_partLookup != null)
            {
                _ = LoadWaitingMatchesAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DismantleWizard Load fehlgeschlagen");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// UX X.25: laedt fuer jeden Required-Part die wartenden Figuren die das
    /// Teil noch brauchen. Eigene Figur (TrackedMinifigId) wird gefiltert -
    /// man kann sich nicht selbst zuordnen.
    /// </summary>
    private async Task LoadWaitingMatchesAsync()
    {
        if (_partLookup == null) return;

        // Snapshot der aktuellen Liste damit wir nicht in eine ObservableCollection-
        // Modification-Race kommen wenn LoadAsync nochmal laeuft.
        var partsSnapshot = Parts.ToList();
        foreach (var partVm in partsSnapshot)
        {
            try
            {
                var lookup = await _partLookup.LookupPartAsync(partVm.BlPartNo, partVm.BlColorId);
                // Eigene Figur filtern - sonst koennte man sich selbst Teile
                // zuordnen die man gerade zerlegt.
                var matches = lookup.WaitingMatches
                    .Where(m => m.TrackedMinifigId != TrackedMinifigId)
                    .ToList();

                if (matches.Count > 0)
                {
                    Application.Current?.Dispatcher.Invoke(() => partVm.SetMatches(matches));
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "UX X.25 LookupPartAsync fuer {Part}/{Color} fehlgeschlagen",
                    partVm.BlPartNo, partVm.BlColorId);
            }
        }
    }

    private async Task LoadPartImagesAndSwatchesAsync()
    {
        try
        {
            var allColors = _catalog == null ? new List<Core.Models.Bricklink.BlColor>()
                : await _catalog.GetAllColorsAsync();
            var colorMap = allColors.ToDictionary(c => c.ColorId);

            foreach (var p in Parts.ToList())
            {
                if (colorMap.TryGetValue(p.BlColorId, out var col) && col.Rgb != null)
                {
                    var brush = ParseRgbBrush(col.Rgb);
                    Application.Current?.Dispatcher.Invoke(() => p.SwatchBrush = brush);
                }

                if (_imageProvider != null)
                {
                    try
                    {
                        var url = await _imageProvider.GetImageFileByBlAsync("P", p.BlPartNo, p.BlColorId);
                        if (!string.IsNullOrEmpty(url))
                            Application.Current?.Dispatcher.Invoke(() => p.ImageUrl = url);
                    }
                    catch { /* best-effort */ }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Wizard LoadPartImagesAndSwatches geworfen");
        }
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

    /// <summary>Setzt alle Checkboxen auf true.</summary>
    public void SelectAll()
    {
        foreach (var p in Parts) p.IsKept = true;
    }

    /// <summary>Setzt alle Checkboxen auf false.</summary>
    public void DeselectAll()
    {
        foreach (var p in Parts) p.IsKept = false;
    }

    /// <summary>Wendet das DefaultBin auf alle Teile an (auch deaktivierte).</summary>
    public void ApplyDefaultBin()
    {
        if (DefaultBin == null) return;
        // Bin-Objekt aus AvailableBins (Reference-Equality fuer ComboBox)
        var bin = AvailableBins.FirstOrDefault(b => b.Id == DefaultBin.Id);
        if (bin == null) return;
        foreach (var p in Parts) p.TargetBin = bin;
    }

    /// <summary>
    /// UX X.32 Block B (v0.1.19): Pending-Mode-Indikator. TrackedMinifigId == 0
    /// signalisiert "noch nicht in DB" - Confirm legt FloatingParts direkt
    /// an statt eine vorhandene Figur zu zerlegen.
    /// </summary>
    public bool IsPendingMode => TrackedMinifigId == 0;

    /// <summary>
    /// UX X.32 Block B (v0.1.19): Direkt-Zerlegen-Pfad ohne dass die Figur
    /// in userdata.db angelegt wurde. Befuellt die Wizard-Liste aus der
    /// PendingMinifigViewModel - nur Teile mit QuantityCollected &gt; 0.
    /// </summary>
    public async Task LoadFromPendingAsync(PendingMinifigViewModel pending)
    {
        IsLoading = true;
        try
        {
            MinifigName = pending.Name;
            BricklinkId = pending.BricklinkId;

            var bins = await _binService.GetAllAsync();
            AvailableBins.Clear();
            foreach (var b in bins) AvailableBins.Add(b);

            // Default-Bin: erstes wirklich freies Fach (UX-X.6-konform).
            var firstFree = await _binService.SuggestBinForWaitingMinifigAsync();
            DefaultBin = firstFree != null
                ? AvailableBins.FirstOrDefault(b => b.Id == firstFree.Id)
                : AvailableBins.FirstOrDefault();

            Parts.Clear();

            // UX X.32 Bug-Fix v0.1.19-beta.3: Wizard-Session-State, damit
            // verschiedene Teile auf verschiedene frische Bins verteilt
            // werden (vorher: alle 4 Teile bekamen das gleiche "naechste freie"
            // Fach weil die DB sich waehrend des Wizard-Aufbaus nicht aendert).
            var usedBinsInThisSession = new HashSet<int>();

            foreach (var p in pending.Parts
                .Where(p => p.QuantityCollected > 0)
                .OrderBy(p => p.PartName))
            {
                var partVm = DismantlePartItemViewModel.FromPending(p);
                partVm.IsKept = true;

                StorageBin? perPartBin = null;
                try
                {
                    // Pending-Mode: keine Figur in DB, kein excludeMinifigId.
                    var (bin, wasStack) = await PickPerPartBinAsync(
                        p.BricklinkPartNo, p.BricklinkColorId,
                        usedBinsInThisSession,
                        excludeMinifigId: null);
                    perPartBin = bin;
                    if (perPartBin != null && !wasStack)
                        usedBinsInThisSession.Add(perPartBin.Id);

                    Log.Information("PendingDismantle Bin-Pick: Part={Part}/{Color}, Bin={Bin}, Stack={Stack}, used=[{Used}]",
                        p.BricklinkPartNo, p.BricklinkColorId,
                        perPartBin?.Label ?? "(null)", wasStack,
                        string.Join(",", usedBinsInThisSession));
                }
                catch (Exception ex)
                {
                    Log.Warning(ex,
                        "PendingMode PickPerPartBin fehlgeschlagen fuer {Part}/{Color}",
                        p.BricklinkPartNo, p.BricklinkColorId);
                }
                partVm.TargetBin = perPartBin ?? DefaultBin;

                Parts.Add(partVm);
            }

            // Bilder + Color-Swatches (best-effort, gleiche Logik wie LoadAsync).
            if (_imageProvider != null && _catalog != null)
            {
                _ = LoadPartImagesAndSwatchesAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DismantleWizard LoadFromPending fehlgeschlagen");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// UX X.32 Block B (v0.1.19): Sammel-Popup-Items pro angelegtem
    /// FloatingPart. Wird vom UI-Layer (ScanViewModel / MinifigSummaryDialog)
    /// nach erfolgreichem ConfirmAsync gelesen.
    /// </summary>
    public List<BinInstructionItem> LastBinInstructionItems { get; private set; } = new();

    /// <summary>
    /// UX X.32 Bug-Fix v0.1.19-beta.3: Verteilt Teile innerhalb EINER Wizard-
    /// Session auf verschiedene Bins. Hintergrund: ohne diesen In-Memory-State
    /// liefert <see cref="IStorageBinService.SuggestBinForFloatingPartAsync"/>
    /// fuer JEDES Teil das gleiche "naechste freie Fach", weil sich die DB
    /// waehrend des Wizard-Aufbaus nicht aendert. Folge: alle Teile bekommen
    /// den gleichen Default-Bin (das war der User-Bug-Repro mit Box20-01).
    ///
    /// Step 1 (bestehender FloatingPart-Stapel mit gleicher PartNo+ColorId)
    /// liefert IMMER das Stapel-Fach - mehrere Teile von gleichem Typ landen
    /// im gleichen Stapel, das ist korrektes Verhalten. Steps 2-4 (frische
    /// Bins) skippen Bins die in <paramref name="usedBinsInThisSession"/>
    /// schon vergeben wurden.
    ///
    /// <paramref name="excludeMinifigId"/>: Fach der zu zerlegenden Figur
    /// zaehlt als frei (Pending-Mode null, Normal-Mode TrackedMinifigId).
    /// </summary>
    /// <returns>
    /// Tupel (Bin, WasStackBin). WasStackBin=true heisst Step 1 hat gegriffen
    /// - Aufrufer soll das Bin NICHT in usedBins aufnehmen. WasStackBin=false
    /// heisst frisches Fach - Aufrufer soll es zu usedBins adden, damit das
    /// naechste Teil ein anderes Fach bekommt.
    /// </returns>
    private async Task<(StorageBin? Bin, bool WasStackBin)> PickPerPartBinAsync(
        string blPartNo, int blColorId,
        HashSet<int> usedBinsInThisSession,
        int? excludeMinifigId)
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync();

        // Step 1: bestehender Stapel mit gleicher PartNo+ColorId.
        // FIFO nach AddedAt - mehrere Teile gleichen Typs landen im gleichen
        // Stapel, das wollen wir explizit (kein usedBin-Skip).
        var existingFp = await ctx.FloatingParts
            .AsNoTracking()
            .Where(p => p.PartNumber == blPartNo && p.ColorId == blColorId)
            .OrderBy(p => p.AddedAt)
            .FirstOrDefaultAsync();
        if (existingFp != null)
        {
            var stackBin = AvailableBins.FirstOrDefault(b => b.Id == existingFp.StorageBinId);
            if (stackBin != null) return (stackBin, WasStackBin: true);
        }

        var hasExclude = excludeMinifigId.HasValue;

        // Step 2: wirklich freies Fach (keine Minifigs ausser excluded, keine
        // Floating), nicht bereits in dieser Session vergeben.
        var trulyFree = await ctx.StorageBins
            .AsNoTracking()
            .Where(b => !usedBinsInThisSession.Contains(b.Id)
                     && !b.TrackedMinifigs.Any(m => !hasExclude || m.Id != excludeMinifigId!.Value)
                     && !b.FloatingParts.Any())
            .OrderBy(b => b.Label)
            .FirstOrDefaultAsync();
        if (trulyFree != null)
        {
            var bin = AvailableBins.FirstOrDefault(b => b.Id == trulyFree.Id);
            if (bin != null) return (bin, WasStackBin: false);
        }

        // Step 3: Fach ohne Minifigs (Floating erlaubt - Mix), nicht in usedBins.
        var noMinifigOnly = await ctx.StorageBins
            .AsNoTracking()
            .Where(b => !usedBinsInThisSession.Contains(b.Id)
                     && !b.TrackedMinifigs.Any(m => !hasExclude || m.Id != excludeMinifigId!.Value))
            .OrderBy(b => b.Label)
            .FirstOrDefaultAsync();
        if (noMinifigOnly != null)
        {
            var bin = AvailableBins.FirstOrDefault(b => b.Id == noMinifigOnly.Id);
            if (bin != null) return (bin, WasStackBin: false);
        }

        // Step 4: am wenigsten belegtes Fach, nicht in usedBins.
        var fallback = await ctx.StorageBins
            .AsNoTracking()
            .Where(b => !usedBinsInThisSession.Contains(b.Id))
            .OrderBy(b => b.TrackedMinifigs.Count(m =>
                m.Status == TrackedMinifigStatus.Complete
                && (!hasExclude || m.Id != excludeMinifigId!.Value)))
            .ThenBy(b => b.FloatingParts.Count)
            .ThenBy(b => b.Label)
            .FirstOrDefaultAsync();
        if (fallback != null)
        {
            var bin = AvailableBins.FirstOrDefault(b => b.Id == fallback.Id);
            if (bin != null) return (bin, WasStackBin: false);
        }

        // Letzter Fallback: alle Bins schon in usedBins - nimm halt das erste
        // verfuegbare. Sollte praktisch nie passieren (mehr Teile als Bins).
        return (AvailableBins.FirstOrDefault(), WasStackBin: false);
    }

    /// <summary>Fuehrt den Aufgeben-Vorgang aus und gibt das Service-Resultat zurueck.</summary>
    public async Task<DismantleResult> ConfirmAsync()
    {
        // UX X.32 Block B (v0.1.19): Pending-Mode -> direkter FloatingPart-
        // Insert ohne ueber DismantleAsync zu gehen (es gibt keine Figur in
        // der DB die geloescht werden muesste).
        if (IsPendingMode)
        {
            return await ConfirmPendingModeAsync();
        }

        var choices = Parts.Select(p =>
        {
            // UX X.25: Mode-basiert TargetBinId ODER AssignToTrackedMinifigPartId.
            // - PutInBin (Default): TargetBinId aus Bin-Combo
            // - AssignToWaiting: SelectedMatch.TrackedMinifigPartId
            // Bei IsKept=false werden beide null gesetzt (Validierung im Service ist OK).
            var assignTo = p.IsKept && p.IsAssignToWaitingMode && p.SelectedMatch != null
                ? p.SelectedMatch.TrackedMinifigPartId
                : (int?)null;
            var targetBin = p.IsKept && p.IsPutInBinMode
                ? p.TargetBin?.Id
                : (int?)null;

            return new DismantlePartChoice
            {
                TrackedMinifigPartId = p.Id,
                IsKept = p.IsKept,
                TargetBinId = targetBin,
                AssignToTrackedMinifigPartId = assignTo
            };
        }).ToList();
        return await _persistence.DismantleAsync(TrackedMinifigId, choices);
    }

    /// <summary>
    /// UX X.32 Block B (v0.1.19): Pending-Mode-Persist - die Figur ist NICHT
    /// in der DB, also nur die markierten Teile direkt als FloatingParts
    /// einlagern. Bei vorhandenem Eintrag (gleiches Bin + PartNo + ColorId)
    /// wird die Quantity additiv erhoeht (Stapel-Wachstum). Bin.FreedAt wird
    /// zurueckgesetzt falls als frei markiert (UX-X.6-Konvention).
    /// </summary>
    private async Task<DismantleResult> ConfirmPendingModeAsync()
    {
        var keep = Parts.Where(p => p.IsKept).ToList();
        if (keep.Count == 0)
        {
            return new DismantleResult { Success = true, CreatedFloatingParts = 0 };
        }

        var instructionItems = new List<BinInstructionItem>();
        var createdCount = 0;
        var totalQty = 0;

        await using var ctx = await _ctxFactory.CreateDbContextAsync();
        var now = DateTime.UtcNow;

        foreach (var item in keep)
        {
            if (item.TargetBin == null) continue; // ohne Ziel ueberspringen

            var qty = item.QuantityCollected > 0 ? item.QuantityCollected : item.QuantityNeeded;
            if (qty <= 0) continue;

            // Bin-FreedAt zuruecksetzen falls als frei markiert (UX-X.6).
            var bin = await ctx.StorageBins.FirstOrDefaultAsync(b => b.Id == item.TargetBin.Id);
            if (bin == null) continue;
            if (bin.FreedAt != null)
            {
                Log.Information("Pending-Dismantle: Fach '{Label}' war frei seit {FreedAt} - wird wieder belegt",
                    bin.Label, bin.FreedAt);
                bin.FreedAt = null;
            }

            // Bestehender FloatingPart in Ziel-Bin? -> Quantity additiv.
            var existing = await ctx.FloatingParts.FirstOrDefaultAsync(fp =>
                fp.PartNumber == item.BlPartNo
                && fp.ColorId == item.BlColorId
                && fp.StorageBinId == item.TargetBin.Id);

            if (existing != null)
            {
                existing.Quantity += qty;
            }
            else
            {
                ctx.FloatingParts.Add(new FloatingPart
                {
                    PartNumber = item.BlPartNo,
                    ColorId = item.BlColorId,
                    PartName = item.PartName,
                    ColorName = item.ColorName,
                    Quantity = qty,
                    StorageBinId = item.TargetBin.Id,
                    AddedAt = now
                });
            }

            createdCount++;
            totalQty += qty;
            instructionItems.Add(new BinInstructionItem
            {
                ItemLabel = $"{item.PartName} ({item.BlPartNo}) - {item.ColorName}",
                QuantityText = $"{qty} Stueck",
                BinLabel = item.TargetBin.Label,
                ImageUrl = item.ImageUrl
            });
        }

        await ctx.SaveChangesAsync();
        LastBinInstructionItems = instructionItems;

        Log.Information("Pending-Dismantle: {Count} FloatingPart-Eintraege angelegt/aktualisiert ({Total} Stueck)",
            createdCount, totalQty);

        return new DismantleResult
        {
            Success = true,
            CreatedFloatingParts = createdCount,
            TotalPartsTransferred = totalQty
        };
    }
}

/// <summary>UX X.25: Modus pro Teil im DismantleWizard.</summary>
public enum DismantlePartMode
{
    /// <summary>Standard: Teil wird als FloatingPart in das gewaehlte Fach gelegt.</summary>
    PutInBin,
    /// <summary>UX X.25: Teil wird einer wartenden Figur direkt zugeordnet.</summary>
    AssignToWaiting
}

/// <summary>Eine Zeile im Wizard: ein Required-Part + Auswahl + Ziel-Fach.</summary>
public partial class DismantlePartItemViewModel : ObservableObject
{
    public int Id { get; }
    public string PartName { get; }
    public string BlPartNo { get; }
    public string ColorName { get; }
    public int BlColorId { get; }
    public int QuantityNeeded { get; }
    public int QuantityCollected { get; }

    /// <summary>True wenn QuantityCollected > 0 (Teil ist tatsaechlich da).</summary>
    public bool WasCollected => QuantityCollected > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    private bool _isKept;

    [ObservableProperty]
    private StorageBin? _targetBin;

    /// <summary>BL-Bild des Teils - wird vom Parent-VM async befuellt.</summary>
    [ObservableProperty]
    private string? _imageUrl;

    /// <summary>Color-Swatch - wird vom Parent-VM async aus bl_colors befuellt.</summary>
    [ObservableProperty]
    private Brush _swatchBrush = Brushes.Gray;

    /// <summary>
    /// Smart-Hint neben der Bin-ComboBox. Beispiel: "+3 schon dort" wenn das Teil
    /// im vorgewaehlten Fach bereits als Einzelteil liegt. Null = kein Hinweis.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSmartHint))]
    private string? _smartHint;

    public bool HasSmartHint => !string.IsNullOrEmpty(SmartHint);

    // ====================================================================
    // UX X.25: Direkt-Zuordnung zu wartender Figur
    // ====================================================================

    /// <summary>
    /// Modus pro Teil: in Lager legen (Default) oder einer wartenden Figur
    /// zuordnen (nur sichtbar wenn HasMatches=true).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAssignToWaitingMode))]
    [NotifyPropertyChangedFor(nameof(IsPutInBinMode))]
    private DismantlePartMode _mode = DismantlePartMode.PutInBin;

    public bool IsPutInBinMode => Mode == DismantlePartMode.PutInBin;
    public bool IsAssignToWaitingMode => Mode == DismantlePartMode.AssignToWaiting;

    /// <summary>
    /// Eindeutiger RadioButton-GroupName pro Item, sonst wuerden alle
    /// RadioButtons im ItemsControl in einer einzigen WPF-Gruppe landen
    /// (RadioButton.GroupName ist global pro Window).
    /// </summary>
    public string ModeGroupName => $"PartMode_{Id}";

    /// <summary>
    /// Wartende Figuren die dieses Teil noch brauchen (aus IPartLookupService.
    /// LookupPartAsync). Leer = der "zuordnen"-RadioButton wird nicht angezeigt.
    /// </summary>
    public ObservableCollection<HBSort.Core.Services.WaitingMinifigMatch> WaitingMatches { get; } = new();

    /// <summary>
    /// Aktuell gewaehlter Match aus WaitingMatches (Default: erster Eintrag).
    /// Bei N Matches: User waehlt aus dem Dropdown. Bei 1 Match: implizit gesetzt.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SingleMatchDisplay))]
    private HBSort.Core.Services.WaitingMinifigMatch? _selectedMatch;

    /// <summary>True wenn mind. ein Match existiert -> RadioButton-Reihe wird sichtbar.</summary>
    public bool HasMatches => WaitingMatches.Count > 0;

    /// <summary>True wenn genau 1 Match (Anzeige als Text statt Dropdown).</summary>
    public bool HasSingleMatch => WaitingMatches.Count == 1;

    /// <summary>True wenn 2+ Matches (Anzeige als Dropdown).</summary>
    public bool HasMultipleMatches => WaitingMatches.Count > 1;

    /// <summary>Display-Text fuer den 1-Match-Fall: "cty0685 (6/7) [Box 003]".</summary>
    public string SingleMatchDisplay
    {
        get
        {
            var m = SelectedMatch ?? WaitingMatches.FirstOrDefault();
            if (m == null) return string.Empty;
            var bin = string.IsNullOrWhiteSpace(m.StorageBinLabel)
                ? string.Empty
                : $" [{m.StorageBinLabel}]";
            return $"{m.MinifigName} ({m.QuantityCollected + 1}/{m.QuantityNeeded}){bin}";
        }
    }

    public string StatusLabel => IsKept ? "(uebernommen)" : "(verworfen)";
    public string EffectiveQtyLabel => WasCollected
        ? $"x{QuantityCollected} (gesammelt)"
        : $"x{QuantityNeeded} (NICHT gesammelt)";
    public string DisplayLine => $"{PartName} ({BlPartNo}) - {ColorName}, {EffectiveQtyLabel}";

    public DismantlePartItemViewModel(TrackedMinifigPart p)
    {
        Id = p.Id;
        PartName = p.PartName;
        BlPartNo = p.PartNumber;
        ColorName = p.ColorName;
        BlColorId = p.ColorId;
        QuantityNeeded = p.QuantityNeeded;
        QuantityCollected = p.QuantityCollected;
    }

    /// <summary>
    /// UX X.32 Block B (v0.1.19): Privater Konstruktor fuer den Pending-Mode -
    /// statt einer DB-PK kommen die Werte aus PendingPartViewModel. Id bleibt
    /// 0 (nicht persistiert), PartName/Color/Quantity werden uebernommen.
    /// </summary>
    private DismantlePartItemViewModel(
        string blPartNo, int blColorId, string partName, string colorName,
        int quantityNeeded, int quantityCollected, string? imageUrl)
    {
        Id = 0;
        PartName = partName;
        BlPartNo = blPartNo;
        ColorName = colorName;
        BlColorId = blColorId;
        QuantityNeeded = quantityNeeded;
        QuantityCollected = quantityCollected;
        ImageUrl = imageUrl;
    }

    /// <summary>
    /// UX X.32 Block B (v0.1.19): Factory fuer den Pending-Mode (Direkt-Zerlegen
    /// einer noch nicht persistierten Figur). Quelle ist PendingPartViewModel
    /// statt TrackedMinifigPart.
    /// </summary>
    public static DismantlePartItemViewModel FromPending(PendingPartViewModel p)
    {
        return new DismantlePartItemViewModel(
            blPartNo: p.BricklinkPartNo,
            blColorId: p.BricklinkColorId,
            partName: p.PartName,
            colorName: p.ColorName,
            quantityNeeded: p.Quantity,
            quantityCollected: p.QuantityCollected,
            imageUrl: p.ImageUrl);
    }

    /// <summary>
    /// UX X.25: wird vom Wizard-VM nach LookupPartAsync aufgerufen.
    /// Setzt WaitingMatches + waehlt den ersten Match als Default.
    /// </summary>
    public void SetMatches(IEnumerable<HBSort.Core.Services.WaitingMinifigMatch> matches)
    {
        WaitingMatches.Clear();
        foreach (var m in matches) WaitingMatches.Add(m);
        SelectedMatch = WaitingMatches.FirstOrDefault();
        OnPropertyChanged(nameof(HasMatches));
        OnPropertyChanged(nameof(HasSingleMatch));
        OnPropertyChanged(nameof(HasMultipleMatches));
        OnPropertyChanged(nameof(SingleMatchDisplay));
    }
}
