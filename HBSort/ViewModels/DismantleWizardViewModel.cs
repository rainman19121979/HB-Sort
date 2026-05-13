using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using HBSort.Core.Database;
using HBSort.Core.Models;
using HBSort.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly ISettingsService? _settingsService;
    private readonly ICategoryBinMappingService? _categoryMapping;

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
        IPartLookupService? partLookup = null,
        ISettingsService? settingsService = null,
        ICategoryBinMappingService? categoryMapping = null)
    {
        TrackedMinifigId = trackedMinifigId;
        _ctxFactory = ctxFactory;
        _binService = binService;
        _persistence = persistence;
        _imageProvider = imageProvider;
        _catalog = catalog;
        _settingsService = settingsService;
        _partLookup = partLookup;
        _categoryMapping = categoryMapping;
    }

    public async Task LoadAsync()
    {
        // v0.1.22-beta.1 Block B: Profiling Gesamtzeit + PerPartBin- und
        // FindFloatingLocations-Phasen. Beide Schleifen rufen pro Required-
        // Part eigene DB-Lookups (N+1).
        var swTotal = Stopwatch.StartNew();
        var swPickBin = new Stopwatch();
        var swSmartHint = new Stopwatch();
        int pickBinCalls = 0;
        int smartHintCalls = 0;
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

            // v0.1.22-beta.1 Block E: Helper-Refactor. Lagerfach-Liste
            // einmalig laden - identische Logik in LoadFromPendingAsync.
            await ReloadAvailableBinsAsync();

            // Default-Bin: aktuelles Fach der Figur, sonst erstes wirklich
            // freies Fach.
            // UX X.33 v0.1.19-beta.7 Drift-Fix: vorher GetNextFreeAsync (alt,
            // ignoriert Complete-Figuren entgegen UX-X.6-Konvention). Jetzt
            // SuggestBinForWaitingMinifigAsync mit MaxWaitingFiguresPerBin -
            // konsistent zum Sortier-Tab.
            StorageBin? targetBin = m.StorageBinId.HasValue
                ? AvailableBins.FirstOrDefault(b => b.Id == m.StorageBinId.Value)
                : null;
            if (targetBin == null)
            {
                // Settings-Service bevorzugt aus dem Konstruktor; falls null
                // (z.B. in Tests) auf Default 1 (strikte Trennung) zurueckfallen.
                var maxWaiting = _settingsService?.Current.MaxWaitingFiguresPerBin ?? 1;
                var firstFree = await _binService.SuggestBinForWaitingMinifigAsync(maxWaiting);
                if (firstFree != null)
                    targetBin = AvailableBins.FirstOrDefault(b => b.Id == firstFree.Id);
            }
            DefaultBin = targetBin ?? AvailableBins.FirstOrDefault();

            Parts.Clear();

            // v0.1.22-beta.1 Block C: SmartHint-Bulk-Pre-Fetch statt N+1.
            // Eine einzige Query gegen FloatingParts fuer alle Required-Parts
            // statt eine pro Iteration. PickPerPartBin bleibt pro-Item, weil
            // die Logik dort kategorie-/mapping-abhaengig ist und in einem
            // Bulk-Pfad mehr Aufwand als Gewinn waere.
            Dictionary<(string PartNo, int ColorId), List<FloatingPartLocation>> floatingByKey;
            if (_partLookup != null)
            {
                try
                {
                    swSmartHint.Start();
                    var keys = m.RequiredParts
                        .Select(p => (p.PartNumber, p.ColorId)).ToList();
                    floatingByKey = await _partLookup.FindFloatingLocationsForManyAsync(keys);
                    smartHintCalls = 1; // 1 Bulk-Call statt N
                    swSmartHint.Stop();
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "SmartHint-Bulk-Lookup fehlgeschlagen - per-part-Path bleibt aus");
                    floatingByKey = new();
                }
            }
            else
            {
                floatingByKey = new();
            }

            // UX X.33 v0.1.19-beta.7 Block M: Wizard-Session-State entfaellt -
            // PickPerPartBinAsync ist jetzt deterministisch (Mapping + Stapel
            // + truly-free), kein "naechstes freies Fach"-Wackelwert mehr.
            foreach (var p in m.RequiredParts.OrderBy(p => p.PartName))
            {
                var partVm = new DismantlePartItemViewModel(p)
                {
                    // Smart-Default: nur tatsaechlich gesammelte Teile vor-aktivieren.
                    // Nicht-gesammelte Teile sind im Wizard deaktiviert (User kann
                    // bei Bedarf manuell ankreuzen, dann werden QuantityNeeded uebernommen).
                    IsKept = p.QuantityCollected > 0,
                };

                StorageBin? perPartBin = null;
                try
                {
                    swPickBin.Start();
                    pickBinCalls++;
                    perPartBin = await PickPerPartBinAsync(
                        p.PartNumber, p.ColorId,
                        excludeMinifigId: TrackedMinifigId,
                        partName: p.PartName);
                    swPickBin.Stop();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex,
                        "PickPerPartBin fehlgeschlagen fuer {Part}/{Color}",
                        p.PartNumber, p.ColorId);
                }
                // UX X.33 Block N: kein Default-Bin-Fallback mehr - wenn der
                // Service null liefert, bleibt TargetBin=null und das UI zeigt
                // einen Volle-Faecher-Hinweis pro Teil. ConfirmAsync blockt
                // dann das Speichern.
                partVm.TargetBin = perPartBin;
                partVm.NoFreeBinWarning = perPartBin == null;

                // v0.1.22-beta.1 Block C/E: SmartHint via Helper. Liest aus
                // dem Bulk-Dict, ueberschreibt TargetBin/SmartHint/NoFree-
                // BinWarning bei Treffer (Stapel-Wachstum wins).
                ApplySmartHintIfAny(partVm, p.PartNumber, p.ColorId, floatingByKey);

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
            swTotal.Stop();
            Log.Information(
                "[PROFILE] DismantleWizard.LoadAsync minifig={Id}: total={Total}ms, " +
                "pickBin={Pb}ms ueber {PbCalls}, smartHint={Sh}ms ueber {ShCalls}, parts={Parts}",
                TrackedMinifigId, swTotal.ElapsedMilliseconds,
                swPickBin.ElapsedMilliseconds, pickBinCalls,
                swSmartHint.ElapsedMilliseconds, smartHintCalls,
                Parts.Count);
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

    /// <summary>
    /// v0.1.22-beta.1 Block E: extrahierter Helper. Laedt alle Bins
    /// neu und ersetzt den AvailableBins-Inhalt. Wird von LoadAsync und
    /// LoadFromPendingAsync identisch gebraucht.
    ///
    /// v0.1.22-beta.3 (2026-05-13): typ-gefilterte Liste (FloatingTarget).
    /// Beim Zerlegen werden Required-Parts zu FloatingParts - die Combobox
    /// darf nur Floating-passende Bins anbieten (Empty / FloatingOnly).
    /// Wartend-Bins mit fremden Figuren werden ausgeschlossen.
    ///
    /// Pragmatisch ohne Pro-Part-Reverse-Match-Filter: bei einer Figur
    /// die zerlegt wird ist der Reverse-Match auf die eigenen RequiredParts
    /// irrelevant, und ein Pro-Part-Filter waere N-mal GetEligibleBinsAsync
    /// (kostspielig). Falls Praxis-Test zeigt dass Wartend-Bins mit
    /// passendem RequiredPart trotzdem als Ziel sinnvoll waeren: spaetere
    /// Iteration mit Bulk-Lookup-Variante.
    /// </summary>
    private async Task ReloadAvailableBinsAsync()
    {
        var excludeId = TrackedMinifigId > 0 ? (int?)TrackedMinifigId : null;
        var bins = await _binService.GetEligibleBinsAsync(
            BinTargetKind.FloatingTarget,
            excludeMinifigId: excludeId);
        AvailableBins.Clear();
        foreach (var b in bins) AvailableBins.Add(b);
    }

    /// <summary>
    /// v0.1.22-beta.1 Block E: extrahierter Helper. Wertet das Bulk-
    /// floatingByKey-Dict (siehe Block C) fuer ein Part-VM aus und setzt
    /// TargetBin/SmartHint/NoFreeBinWarning bei Treffer. No-op wenn das
    /// Teil im Dict nicht vorkommt oder das Bin nicht in AvailableBins ist.
    /// </summary>
    private void ApplySmartHintIfAny(
        DismantlePartItemViewModel partVm,
        string blPartNo, int blColorId,
        IReadOnlyDictionary<(string PartNo, int ColorId), List<FloatingPartLocation>> floatingByKey)
    {
        if (!floatingByKey.TryGetValue((blPartNo, blColorId), out var locations)
            || locations.Count == 0) return;

        var best = locations[0]; // sortiert nach Menge absteigend
        var bin = AvailableBins.FirstOrDefault(b => b.Id == best.StorageBinId);
        if (bin == null)
        {
            Log.Warning("SmartHint: Bin Id={BinId} nicht in AvailableBins gefunden",
                best.StorageBinId);
            return;
        }
        // SmartHint ueberschreibt vorigen TargetBin (Stapel-Wachstum wins).
        partVm.TargetBin = bin;
        partVm.SmartHint = $"+{best.TotalQuantity} schon dort";
        partVm.NoFreeBinWarning = false;
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
        // v0.1.22-beta.1 Block B: Profiling.
        var swTotal = Stopwatch.StartNew();
        var swPickBin = new Stopwatch();
        int pickBinCalls = 0;
        IsLoading = true;
        try
        {
            MinifigName = pending.Name;
            BricklinkId = pending.BricklinkId;

            // v0.1.22-beta.1 Block E: Helper-Refactor (identisch zu LoadAsync).
            await ReloadAvailableBinsAsync();

            // Default-Bin: vom Suggest-Service mit MaxWaitingFiguresPerBin-
            // Limit (Drift-Fix UX X.33: vorher rief LoadFromPendingAsync den
            // Service ohne Limit-Param auf, Service-Default 1 griff statt der
            // User-Einstellung).
            var maxWaiting = _settingsService?.Current.MaxWaitingFiguresPerBin ?? 1;
            var firstFree = await _binService.SuggestBinForWaitingMinifigAsync(maxWaiting);
            DefaultBin = firstFree != null
                ? AvailableBins.FirstOrDefault(b => b.Id == firstFree.Id)
                : AvailableBins.FirstOrDefault();

            Parts.Clear();

            // UX X.33 v0.1.19-beta.7 Block M: kein Wizard-Session-State mehr.
            // PickPerPartBinAsync ist deterministisch (Mapping + Stapel + truly-free).
            foreach (var p in pending.Parts
                .Where(p => p.QuantityCollected > 0)
                .OrderBy(p => p.PartName))
            {
                var partVm = DismantlePartItemViewModel.FromPending(p);
                partVm.IsKept = true;

                StorageBin? perPartBin = null;
                try
                {
                    swPickBin.Start();
                    pickBinCalls++;
                    // Pending-Mode: keine Figur in DB, kein excludeMinifigId.
                    perPartBin = await PickPerPartBinAsync(
                        p.BricklinkPartNo, p.BricklinkColorId,
                        excludeMinifigId: null,
                        partName: p.PartName);
                    swPickBin.Stop();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex,
                        "PendingMode PickPerPartBin fehlgeschlagen fuer {Part}/{Color}",
                        p.BricklinkPartNo, p.BricklinkColorId);
                }
                // UX X.33 Block N: kein DefaultBin-Fallback - wenn null,
                // bleibt TargetBin=null, Banner zeigt Volle-Faecher-Hinweis.
                partVm.TargetBin = perPartBin;
                partVm.NoFreeBinWarning = perPartBin == null;

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
            swTotal.Stop();
            Log.Information(
                "[PROFILE] DismantleWizard.LoadFromPendingAsync {Bl}: total={Total}ms, " +
                "pickBin={Pb}ms ueber {PbCalls}, parts={Parts}",
                BricklinkId, swTotal.ElapsedMilliseconds,
                swPickBin.ElapsedMilliseconds, pickBinCalls, Parts.Count);
        }
    }

    /// <summary>
    /// UX X.32 Block B (v0.1.19): Sammel-Popup-Items pro angelegtem
    /// FloatingPart. Wird vom UI-Layer (ScanViewModel / MinifigSummaryDialog)
    /// nach erfolgreichem ConfirmAsync gelesen.
    /// </summary>
    public List<BinInstructionItem> LastBinInstructionItems { get; private set; } = new();

    /// <summary>
    /// UX X.33 v0.1.19-beta.7 Block N: Per-Teil-Bin-Vorschlag im Wizard.
    /// Brickognize-Kategorie wird aus dem PartName per Praefix-Match gegen
    /// SeenBrickognizeCategories abgeleitet (Heuristik B). Match-Beispiele:
    ///   "Minifigure, Head Black Eyebrows..." -> "Minifigure, Head"
    ///   "Minifigure, Headgear Helmet ..."    -> "Minifigure, Headgear"
    /// Edge-Cases ohne Komma im PartName (z.B. "Minifigure Air Tanks")
    /// fallen auf "Unbekannt" zurueck - User kann manuell mappen oder das
    /// Teil landet via Default-Regel in einem getrennten Bin.
    ///
    /// Die Reihenfolge (Stapel -> User-Mapping -> Default-Regel) lebt im
    /// Service-Aufruf. Hier nur EIN Aufruf, keine Doppel-Logik mehr.
    ///
    /// Liefert null wenn kein passendes Fach frei ist - der Aufrufer
    /// (LoadAsync / LoadFromPendingAsync) zeigt dann den Volle-Faecher-
    /// Hinweis pro Teil.
    /// </summary>
    private async Task<StorageBin?> PickPerPartBinAsync(
        string blPartNo, int blColorId,
        int? excludeMinifigId,
        string? partName = null)
    {
        var category = _categoryMapping?.DeriveCategoryFromPartName(partName) ?? string.Empty;

        var userMapping = _settingsService?.Current.CategoryToBinMapping;

        var suggested = await _binService.SuggestBinForFloatingPartAsync(
            blPartNo, blColorId,
            category,
            excludeMinifigId: excludeMinifigId,
            userMapping: userMapping);

        if (suggested == null)
            return null; // Aufrufer zeigt Volle-Faecher-Hinweis pro Teil.

        return AvailableBins.FirstOrDefault(b => b.Id == suggested.Id) ?? suggested;
    }

    /// <summary>
    /// UX X.33 Block N (v0.1.19-beta.7): pre-flight-Check ob alle "Behalten"-
    /// Teile mit PutInBin-Mode auch ein Ziel-Fach haben. Liefert Liste der
    /// betroffenen PartNamen wenn nicht - der UI-Layer zeigt einen blocking
    /// Dialog und ConfirmAsync wird gar nicht erst gerufen.
    /// </summary>
    public List<string> GetPartsWithoutTargetBin()
    {
        return Parts
            .Where(p => p.IsKept
                     && p.Mode == DismantlePartMode.PutInBin
                     && p.TargetBin == null)
            .Select(p => p.PartName)
            .ToList();
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
    /// einlagern.
    ///
    /// v0.1.23 (A9, S13-Refactor): laeuft jetzt komplett ueber
    /// <see cref="IPartLookupService.AddPartToFloatingAsync"/> statt
    /// inline-DB-Writes im VM. Damit greift Strict-Mode, RecalcKind und
    /// FreedAt-Reset zentral im Service. Nur die Summary-ScanEvent fuer
    /// "Direkt-Zerlegen" wird hier noch als ein Eintrag pro Pending-Confirm
    /// geschrieben (Audit-Trail).
    /// </summary>
    private async Task<DismantleResult> ConfirmPendingModeAsync()
    {
        var keep = Parts.Where(p => p.IsKept).ToList();
        if (keep.Count == 0)
        {
            return new DismantleResult { Success = true, CreatedFloatingParts = 0 };
        }

        if (_partLookup == null)
        {
            // Test-Pfade ohne PartLookup-Service: Legacy-Inline-DB-Write.
            // Production ist _partLookup immer gesetzt (App.xaml.cs DI).
            return await ConfirmPendingModeLegacyAsync(keep);
        }

        var instructionItems = new List<BinInstructionItem>();
        var createdCount = 0;
        var totalQty = 0;

        foreach (var item in keep)
        {
            if (item.TargetBin == null) continue; // ohne Ziel ueberspringen

            var qty = item.QuantityCollected > 0 ? item.QuantityCollected : item.QuantityNeeded;
            if (qty <= 0) continue;

            // Pre-Tag-Fix v0.1.19-beta.7: BrickognizeCategory ueber Heuristik
            // damit Default-Regel "max 1 PartId pro Kategorie pro Bin" greift.
            var derivedCategory = _categoryMapping?.DeriveCategoryFromPartName(item.PartName)
                ?? ICategoryBinMappingService.UnknownCategory;

            try
            {
                await _partLookup.AddPartToFloatingAsync(
                    item.BlPartNo, item.BlColorId,
                    item.PartName, item.ColorName,
                    qty, item.TargetBin.Id,
                    derivedCategory);
            }
            catch (InvalidBinKindException strict)
            {
                // Strict-Mode-Verletzung: Bin akzeptiert das Teil nicht (z.B.
                // weil dort nur Complete-Figuren liegen). Wir werfen weiter
                // damit der UI-Layer (Wizard-Dialog) den User informieren kann.
                Log.Warning(strict,
                    "ConfirmPendingModeAsync: Strict-Mode-Verletzung beim Lagern von {Part} in '{Bin}'",
                    item.BlPartNo, item.TargetBin.Label);
                throw;
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

        // v0.1.23 (UX X.33 Block B): Summary-ScanEvent fuer den Verlauf-Tab
        // damit der Direkt-Zerlegen-Pfad sichtbar bleibt - die einzelnen
        // FloatingPart-Eintraege schreibt AddPartToFloatingAsync schon.
        if (createdCount > 0)
        {
            await using var auditCtx = await _ctxFactory.CreateDbContextAsync();
            auditCtx.ScanEvents.Add(new ScanEvent
            {
                Timestamp = DateTime.UtcNow,
                Type = ScanType.MinifigScan,
                RecognizedId = BricklinkId,
                ResultDescription = $"Direkt-Zerlegen (Pending) - {createdCount} Einzelteil-Eintraege ({totalQty} Stueck)",
                WasUndone = false
            });
            await auditCtx.SaveChangesAsync();
        }

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

    /// <summary>
    /// v0.1.23 Legacy-Fallback: inline-DB-Write fuer Test-Pfade ohne
    /// PartLookupService. Spiegelt das Verhalten vor dem A9-Refactor.
    /// </summary>
    private async Task<DismantleResult> ConfirmPendingModeLegacyAsync(
        List<DismantlePartItemViewModel> keep)
    {
        var instructionItems = new List<BinInstructionItem>();
        var createdCount = 0;
        var totalQty = 0;

        await using var ctx = await _ctxFactory.CreateDbContextAsync();
        var now = DateTime.UtcNow;

        foreach (var item in keep)
        {
            if (item.TargetBin == null) continue;
            var qty = item.QuantityCollected > 0 ? item.QuantityCollected : item.QuantityNeeded;
            if (qty <= 0) continue;

            var bin = await ctx.StorageBins.FirstOrDefaultAsync(b => b.Id == item.TargetBin.Id);
            if (bin == null) continue;
            if (bin.FreedAt != null) bin.FreedAt = null;

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
                var derivedCategory = _categoryMapping?.DeriveCategoryFromPartName(item.PartName)
                    ?? ICategoryBinMappingService.UnknownCategory;
                ctx.FloatingParts.Add(new FloatingPart
                {
                    PartNumber = item.BlPartNo,
                    ColorId = item.BlColorId,
                    PartName = item.PartName,
                    ColorName = item.ColorName,
                    Quantity = qty,
                    StorageBinId = item.TargetBin.Id,
                    BrickognizeCategory = derivedCategory,
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

    // UX X.33 Block N: wenn der User manuell ein Bin in der Combobox waehlt,
    // wird der Volle-Faecher-Hinweis entfernt. Andersrum nicht: wenn er den
    // Bin per Code-Pfad auf null setzt, bleibt der Warn-Status wie er ist
    // (LoadAsync setzt das passend).
    partial void OnTargetBinChanged(StorageBin? value)
    {
        if (value != null) NoFreeBinWarning = false;
    }

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

    /// <summary>
    /// UX X.33 Block N (v0.1.19-beta.7): Volle-Faecher-Hinweis pro Teil.
    /// Wird gesetzt wenn der Suggest-Service kein Bin liefern konnte
    /// (alle Faecher mit kollidierender Kategorie belegt). User muss
    /// manuell ein Fach in der Combobox waehlen oder ein neues anlegen,
    /// sonst blockt ConfirmAsync das Speichern.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoFreeBinWarning))]
    private bool _noFreeBinWarning;

    public bool HasNoFreeBinWarning => NoFreeBinWarning;

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
