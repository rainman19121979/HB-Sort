using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using HBSort.Core.Models;
using HBSort.Core.Services;
using HBSort.Helpers;
using Serilog;

namespace HBSort.ViewModels;

/// <summary>
/// v0.1.24-beta.1 Phase 2a (Konzept 4.1.2 Rev. 3): 2-stufiger Wizard fuer
/// "Diese Figur anlegen" - ersetzt den alten <c>CollectMinifigSelectionViewModel</c>.
///
/// Stufe 1: WAS wird angelegt - Required-Parts in 3 Status-Gruppen
///   - Trigger (gerade gescannt, immer markiert)
///   - InStorage (Reverse-Match-Treffer, vor-markiert mit Bin-Label)
///   - Missing (kein Match, bleibt wartend ODER manuell als gesammelt markiert)
///
/// Stufe 2: WO soll die Figur hin - Lagerfach-Wahl, Bewegungs-Hinweis-Block,
///   Speichern.
///
/// Persist + Post-Save-Modal lebt im View-Codebehind (<c>CollectMinifigWizardDialog</c>)
/// via <see cref="ISortInstructionPresenter"/> - der Wizard selbst kennt keine
/// UI-Services.
/// </summary>
public partial class CollectMinifigWizardViewModel : ObservableObject
{
    private readonly IBlCatalogService _catalog;
    private readonly IPartLookupService _partLookup;
    private readonly IPartImageProvider _imageProvider;
    private readonly IStorageBinService? _binService;
    private readonly int _maxWaitingLimit;

    // ----- Stage -----

    /// <summary>1 oder 2. Default 1 (Wizard startet immer auf Stufe 1).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStageOne))]
    [NotifyPropertyChangedFor(nameof(IsStageTwo))]
    private int _currentStage = 1;

    public bool IsStageOne => CurrentStage == 1;
    public bool IsStageTwo => CurrentStage == 2;

    // ----- Figur-Header -----

    [ObservableProperty] private string _minifigName = string.Empty;
    [ObservableProperty] private string _bricklinkId = string.Empty;
    [ObservableProperty] private string? _imageUrl;
    [ObservableProperty] private bool _isLoading;

    // ----- Parts (3 Status-Gruppen) -----

    /// <summary>Alle Required-Parts inkl. Status (Trigger/InStorage/Missing).</summary>
    public ObservableCollection<CollectWizardPartItem> Parts { get; } = new();

    public IEnumerable<CollectWizardPartItem> TriggerParts =>
        Parts.Where(p => p.Status == WizardPartStatus.Trigger);

    public IEnumerable<CollectWizardPartItem> InStorageParts =>
        Parts.Where(p => p.Status == WizardPartStatus.InStorage);

    public IEnumerable<CollectWizardPartItem> MissingParts =>
        Parts.Where(p => p.Status == WizardPartStatus.Missing);

    public int TriggerPartCount => TriggerParts.Count();
    public int InStoragePartCount => InStorageParts.Count();
    public int MissingPartCount => MissingParts.Count();

    public bool HasTriggerParts => TriggerPartCount > 0;
    public bool HasInStorageParts => InStoragePartCount > 0;
    public bool HasMissingParts => MissingPartCount > 0;

    // ----- Lagerfach -----

    /// <summary>
    /// Typ-gefilterte Liste fuer wartende Figuren (analog v0.1.22-beta.3).
    /// v0.1.24-beta.1 Phase 2b: <see cref="BinDisplayItem"/> mit Belegungs-
    /// Suffix ("Box 005 (2 wartend)") — Konsumenten ueber
    /// <c>SelectedBin.Bin.Id</c> / <c>SelectedBin.Bin.Label</c>.
    /// </summary>
    public ObservableCollection<BinDisplayItem> AvailableBins { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MovementCount))]
    [NotifyPropertyChangedFor(nameof(MovementHint))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private BinDisplayItem? _selectedBin;

    /// <summary>
    /// v0.1.22-beta.1 Block D-Banner: sichtbar wenn Suggest=null UND alle
    /// Bins als belegt gelten. Reine UI-Anzeige.
    /// </summary>
    [ObservableProperty]
    private bool _isAllBinsFullBannerVisible;

    /// <summary>
    /// Konzept 4.1.2 Stufe 2: dauerhaft sichtbarer Hinweis-Block - User
    /// sieht WAS er speichert BEVOR das Post-Save-Modal kommt.
    /// "+1" fuer die Figur selbst (immer eine Bewegung in das Ziel-Bin).
    /// </summary>
    public int MovementCount => Parts.Count(p =>
        p.Status == WizardPartStatus.Trigger
        || p.Status == WizardPartStatus.InStorage
        || (p.Status == WizardPartStatus.Missing && p.IsManuallyClaimed))
        + 1;

    public string MovementHint =>
        $"{MovementCount} Bewegungen werden ausgefuehrt";

    /// <summary>Save-Button nur aktiv wenn ein Bin gewaehlt ist.</summary>
    public bool CanSave => SelectedBin != null;

    // ----- Ctor -----

    public CollectMinifigWizardViewModel(
        IBlCatalogService catalog,
        IPartLookupService partLookup,
        IPartImageProvider imageProvider,
        IStorageBinService? binService = null,
        int maxWaitingLimit = 1)
    {
        _catalog = catalog;
        _partLookup = partLookup;
        _imageProvider = imageProvider;
        _binService = binService;
        _maxWaitingLimit = Math.Max(1, maxWaitingLimit);
    }

    // ----- Stage-Navigation -----

    public void GoToStage2()
    {
        if (CurrentStage == 1) CurrentStage = 2;
    }

    public void GoToStage1()
    {
        if (CurrentStage == 2) CurrentStage = 1;
    }

    // ----- Loading -----

    /// <summary>
    /// Laedt Required-Parts, klassifiziert sie in 3 Status-Gruppen und
    /// fuellt die Bin-Liste + Default-Suggest. Logik 1:1 aus dem alten
    /// <c>CollectMinifigSelectionViewModel.LoadAsync</c> uebernommen, nur
    /// die Klassifikation ist neu (Status statt IsKept/IsTrigger/IsCanBe...).
    /// </summary>
    public async Task LoadAsync(
        string blMinifigId,
        string triggerPartNo, int triggerColorId,
        CancellationToken ct = default)
    {
        var swTotal = Stopwatch.StartNew();
        IsLoading = true;
        try
        {
            BricklinkId = blMinifigId;

            await ReloadAvailableBinsAsync(ct);

            var item = await _catalog.GetMinifigDetailsAsync(blMinifigId, ct);
            MinifigName = item?.Name ?? blMinifigId;

            try
            {
                ImageUrl = await _imageProvider.GetImageFileByBlAsync("M", blMinifigId, null);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "CollectMinifigWizard: Bild fuer {Bl} nicht ladbar", blMinifigId);
            }

            var subsets = await _catalog.GetMinifigPartsAsync(blMinifigId, ct);
            var allColors = await _catalog.GetAllColorsAsync(ct);
            var colorMap = allColors.ToDictionary(c => c.ColorId);

            // Bulk-Lookup statt N+1 (v0.1.22-beta.1 Block C).
            Dictionary<(string PartNo, int ColorId), List<FloatingPartLocation>> floatingByKey;
            try
            {
                var keys = subsets.Select(s => (s.ItemNo, s.ColorId)).ToList();
                floatingByKey = await _partLookup.FindFloatingLocationsForManyAsync(keys, ct);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "CollectMinifigWizard: Bulk-Reverse-Match fehlgeschlagen");
                floatingByKey = new();
            }

            // Vor Parts.Clear das alte PropertyChanged-Subscribe aufraeumen
            // (kein Memory-Leak wenn LoadAsync mehrfach gerufen wird).
            foreach (var old in Parts) old.PropertyChanged -= OnPartPropertyChanged;
            Parts.Clear();

            foreach (var s in subsets)
            {
                ct.ThrowIfCancellationRequested();
                colorMap.TryGetValue(s.ColorId, out var color);

                WizardPartStatus status;
                string? foundInBinLabel = null;

                var isTrigger = s.ItemNo.Equals(triggerPartNo, StringComparison.OrdinalIgnoreCase)
                                && s.ColorId == triggerColorId;
                if (isTrigger)
                {
                    status = WizardPartStatus.Trigger;
                }
                else if (floatingByKey.TryGetValue((s.ItemNo, s.ColorId), out var locations)
                         && locations.Count > 0)
                {
                    status = WizardPartStatus.InStorage;
                    foundInBinLabel = locations[0].StorageBinLabel;
                }
                else
                {
                    status = WizardPartStatus.Missing;
                }

                var part = new CollectWizardPartItem
                {
                    PartNumber = s.ItemNo,
                    ColorId = s.ColorId,
                    PartName = string.IsNullOrWhiteSpace(s.ItemNo) ? "(unbekannt)" : s.ItemNo,
                    ColorName = color?.Name ?? $"Color {s.ColorId}",
                    QuantityNeeded = s.Quantity,
                    Status = status,
                    FoundInBinLabel = foundInBinLabel
                };

                // Item beobachtet sich selbst — Toggle IsManuallyClaimed triggert
                // MovementCount-Recompute am VM.
                part.PropertyChanged += OnPartPropertyChanged;
                Parts.Add(part);

                _ = LoadPartImageAsync(part);
            }

            // Computed-Counts + Has*-Flags refresh nach Bulk-Befuellung.
            RaisePartGroupsChanged();
            OnPropertyChanged(nameof(MovementCount));
            OnPropertyChanged(nameof(MovementHint));
        }
        finally
        {
            IsLoading = false;
            swTotal.Stop();
            Log.Information(
                "[PROFILE] CollectMinifigWizard.LoadAsync {BlId}: total={Total}ms, parts={Parts}",
                BricklinkId, swTotal.ElapsedMilliseconds, Parts.Count);
        }
    }

    /// <summary>
    /// Reload-Hook fuer Strict-Mode-Verletzung beim Save (Konzept O9):
    /// Wizard bleibt offen, Bin-Liste wird neu gezogen, User waehlt erneut.
    /// </summary>
    public async Task ReloadAvailableBinsAsync(CancellationToken ct = default)
    {
        if (_binService == null) return;

        // v0.1.24-beta.1 Phase 2b: WithCounts-Variante fuer Combobox-Suffix.
        var allBins = await _binService.GetEligibleBinsWithCountsAsync(
            BinTargetKind.WaitingMinifigTarget, ct: ct);
        AvailableBins.Clear();
        foreach (var b in allBins)
            AvailableBins.Add(new BinDisplayItem(b.Bin, BinDisplayFormatter.FormatBinDisplayText(b)));

        var suggested = await _binService.SuggestBinForWaitingMinifigAsync(
            _maxWaitingLimit, ct);
        SelectedBin = suggested != null
            ? AvailableBins.FirstOrDefault(b => b.Id == suggested.Id)
            : null;

        // Banner: alle Items pruefen — BinDisplayItem.Bin.FreedAt fuer
        // die "alle Bins als belegt"-Logik. AvailableBins ist getypt
        // als BinDisplayItem; via .Bin Zugriff aufs Original-Field.
        IsAllBinsFullBannerVisible = suggested == null
            && AvailableBins.Count > 0
            && AvailableBins.All(b => b.Bin.FreedAt == null);
    }

    // ----- Hilfsmethoden -----

    private void OnPartPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CollectWizardPartItem.IsManuallyClaimed))
        {
            OnPropertyChanged(nameof(MovementCount));
            OnPropertyChanged(nameof(MovementHint));
        }
    }

    private void RaisePartGroupsChanged()
    {
        OnPropertyChanged(nameof(TriggerParts));
        OnPropertyChanged(nameof(InStorageParts));
        OnPropertyChanged(nameof(MissingParts));
        OnPropertyChanged(nameof(TriggerPartCount));
        OnPropertyChanged(nameof(InStoragePartCount));
        OnPropertyChanged(nameof(MissingPartCount));
        OnPropertyChanged(nameof(HasTriggerParts));
        OnPropertyChanged(nameof(HasInStorageParts));
        OnPropertyChanged(nameof(HasMissingParts));
    }

    private async Task LoadPartImageAsync(CollectWizardPartItem part)
    {
        try
        {
            var url = await _imageProvider.GetImageFileByBlAsync("P", part.PartNumber, part.ColorId);
            if (!string.IsNullOrEmpty(url))
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    part.ImageUrl = url;
                });
            }
        }
        catch
        {
            // Bilder optional - kein User-Hinweis bei Fehler.
        }
    }

    // ----- Service-Aufruf-Daten + SortInstruction-Build -----

    /// <summary>
    /// (PartNo, ColorId)-Liste fuer den Reverse-Match-Konsum. Inkludiert
    /// Trigger + InStorage (= alle "schon-da"-Quellen). Manuell-Markierte
    /// landen NICHT hier - die haben einen separaten Pfad ohne FloatingPart-
    /// Effekt.
    /// </summary>
    public IReadOnlyList<(string PartNo, int ColorId)> SelectedPartsForReverseMatch =>
        Parts.Where(p => p.Status == WizardPartStatus.Trigger
                      || p.Status == WizardPartStatus.InStorage)
             .Select(p => (p.PartNumber, p.ColorId))
             .ToList();

    /// <summary>
    /// (PartNo, ColorId)-Liste der vom User in der Missing-Gruppe als
    /// "manuell vorhanden" markierten Teile (Konzept 6.3).
    /// </summary>
    public IReadOnlyList<(string PartNo, int ColorId)> ManuellClaimedParts =>
        Parts.Where(p => p.Status == WizardPartStatus.Missing && p.IsManuallyClaimed)
             .Select(p => (p.PartNumber, p.ColorId))
             .ToList();

    /// <summary>
    /// Baut die <see cref="SortInstruction"/> fuer das Post-Save-Modal nach
    /// erfolgreichem Persist. Pattern aus dem PartLookup-/Sortier-Tab-Pfad
    /// (ScanViewModel ~1693): Take-Sektionen pro Quell-Bin, Put-Sektion mit
    /// der Figur, optionaler PlusHint bei manuell-markierten Teilen.
    ///
    /// <paramref name="consumed"/>: vom Service zurueckgelieferte
    /// FloatingPart-Konsumvorgaenge. Gruppiert nach SourceBinLabel.
    /// <paramref name="targetBinLabel"/>: Ziel-Bin der neuen Figur.
    /// <paramref name="isFullyComplete"/>: schaltet HeaderText auf
    /// "Figur komplett angelegt" statt "Operation erfolgreich".
    /// </summary>
    public SortInstruction BuildSortInstructionForSave(
        IEnumerable<ConsumedFloatingPartInfo> consumed,
        string targetBinLabel,
        bool isFullyComplete)
    {
        var instruction = new SortInstruction
        {
            HeaderText = isFullyComplete
                ? $"Figur '{MinifigName}' komplett angelegt"
                : "Operation erfolgreich"
        };

        // Take-Sektionen: pro Quell-Bin gruppiert.
        foreach (var byBin in consumed.GroupBy(c => c.SourceBinLabel))
        {
            var section = new SortSection { BinLabel = byBin.Key };
            foreach (var c in byBin)
            {
                section.Items.Add(new SortItemLine
                {
                    Label = c.PartName,
                    Detail = $"{c.BlPartNo} - {c.ColorName}",
                    QuantityText = $"{c.Quantity}x",
                    ImageUrl = c.ImageUrl
                });
            }
            instruction.Take.Add(section);
        }

        // Put-Sektion: die Figur kommt ins Ziel-Bin.
        instruction.Put.Add(new SortSection
        {
            BinLabel = targetBinLabel,
            Items = new List<SortItemLine>
            {
                new()
                {
                    Label = $"Figur '{MinifigName}'",
                    Detail = BricklinkId ?? string.Empty,
                    QuantityText = "1x",
                    ImageUrl = ImageUrl
                }
            }
        });

        // PlusHint bei manuell-markierten Teilen - User hat zwar selbst
        // gesagt "ich habe die Teile", aber Audit-Hinweis im Modal.
        var manuellCount = ManuellClaimedParts.Count;
        if (manuellCount > 0)
        {
            instruction.PlusHint = manuellCount == 1
                ? "Plus: 1 Teil manuell als 'vorhanden' markiert (kein Lager-Verbrauch)."
                : $"Plus: {manuellCount} Teile manuell als 'vorhanden' markiert (kein Lager-Verbrauch).";
        }

        return instruction;
    }
}

/// <summary>
/// v0.1.24-beta.1 Phase 2a (Konzept 4.1.2): Status-Klassifikation pro
/// Required-Part im Wizard.
/// </summary>
public enum WizardPartStatus
{
    /// <summary>Das gerade gescannte Trigger-Teil - immer markiert, nicht abwaehlbar.</summary>
    Trigger,
    /// <summary>Im Floating-Pool gefunden - Reverse-Match-Konsum.</summary>
    InStorage,
    /// <summary>Nicht gefunden - bleibt wartend, kann manuell als "vorhanden" markiert werden.</summary>
    Missing
}

/// <summary>
/// v0.1.24-beta.1 Phase 2a (Konzept 4.1.2): eine Zeile im Wizard-Item-
/// Layout. Ersetzt das alte <c>CollectPartItem</c> - die 3 Status-Gruppen
/// ersetzen die alten IsKept/IsTrigger/IsCanBeReverseMatched-Booleans.
/// </summary>
public partial class CollectWizardPartItem : ObservableObject
{
    public string PartNumber { get; init; } = string.Empty;
    public int ColorId { get; init; }
    public string PartName { get; init; } = string.Empty;
    public string ColorName { get; init; } = string.Empty;
    public int QuantityNeeded { get; init; }
    public WizardPartStatus Status { get; init; }

    /// <summary>Nur bei InStorage befuellt - Label des Quell-Bins.</summary>
    public string? FoundInBinLabel { get; init; }

    /// <summary>
    /// Konzept 6.3 / Befund A3: User markiert ein Missing-Teil als "habe
    /// ich selbst da". Persist-Pfad zaehlt QuantityCollected hoch OHNE
    /// FloatingPart-Effekt. Nur fuer Status=Missing aktiv (Checkbox
    /// versteckt fuer Trigger/InStorage).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _isManuallyClaimed;

    [ObservableProperty]
    private string? _imageUrl;

    /// <summary>Checkbox nur fuer Missing-Items - andere haben festen Status.</summary>
    public bool IsCheckBoxVisible => Status == WizardPartStatus.Missing;

    /// <summary>UI-Status-Label - aendert sich live mit IsManuallyClaimed.</summary>
    public string StatusText => Status switch
    {
        WizardPartStatus.Trigger => "gerade gescannt",
        WizardPartStatus.InStorage => $"im Lager: {FoundInBinLabel}",
        WizardPartStatus.Missing => IsManuallyClaimed
            ? "manuell markiert"
            : "fehlt - bleibt wartend",
        _ => string.Empty
    };

    /// <summary>Detail-Zeile in Mono: "PartNo - ColorName".</summary>
    public string DetailLabel => $"{PartNumber} - {ColorName}";

    /// <summary>Quantity-Anzeige rechts ("x2" oder leer bei 1).</summary>
    public string QuantityLabel => QuantityNeeded > 1 ? $"x{QuantityNeeded}" : "x1";
}
