using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using HBSort.Core.Database;
using HBSort.Core.Models;
using HBSort.Core.Models.Bricklink;
using HBSort.Core.Services;
using HBSort.Services;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace HBSort.ViewModels;

/// <summary>
/// "Wo kommt das Teil vor?"-Dialog (Phase 5). Zeigt alle Minifigs aus
/// <see cref="IBlCatalogService.GetSupersetsAsync"/>-Antwort, sortiert in 3 Kategorien:
///   1. Schon in Sammlung (TrackedMinifig.Status=Waiting)
///   2. Komplett gecached (mehrere bl_subsets-Eintraege)
///   3. Nur Name bekannt (1 bl_subsets-Eintrag, von dem aktuellen Lookup)
/// </summary>
public partial class SupersetsDialogViewModel : ObservableObject
{
    private readonly IDbContextFactory<UserDataContext> _ctxFactory;
    private readonly IBlCatalogService _catalog;
    private readonly IStorageBinService _binService;
    private readonly IPartLookupService _partLookup;
    private readonly INotificationService _notifications;
    private readonly IPartImageProvider _imageProvider;

    public string BlPartNo { get; }
    public int BlColorId { get; }
    public string PartName { get; }
    public string ColorName { get; }

    /// <summary>Header-Text "Yellow Head 3626pb0810 erscheint in N Minifiguren".</summary>
    [ObservableProperty]
    private string _headerText = string.Empty;

    /// <summary>Suchfeld; filtert die ItemsView live.</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>True waehrend Daten geladen werden.</summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>Aufgespannte Liste aller Treffer.</summary>
    public ObservableCollection<SupersetItemViewModel> Items { get; } = new();

    /// <summary>Gruppierte + gefilterte View ueber Items (gruppiert nach Kategorie).</summary>
    public ICollectionView GroupedView { get; }

    /// <summary>Wird true sobald der User mindestens eine Aktion (Zuordnen/Sammeln) durchgefuehrt hat.</summary>
    public bool AnyAssignedOrCollected { get; private set; }

    public SupersetsDialogViewModel(
        string blPartNo, int blColorId, string partName, string colorName,
        IDbContextFactory<UserDataContext> ctxFactory,
        IBlCatalogService catalog,
        IStorageBinService binService,
        IPartLookupService partLookup,
        INotificationService notifications,
        IPartImageProvider imageProvider)
    {
        BlPartNo = blPartNo;
        BlColorId = blColorId;
        PartName = partName;
        ColorName = colorName;
        _ctxFactory = ctxFactory;
        _catalog = catalog;
        _binService = binService;
        _partLookup = partLookup;
        _notifications = notifications;
        _imageProvider = imageProvider;

        GroupedView = CollectionViewSource.GetDefaultView(Items);
        GroupedView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SupersetItemViewModel.GroupKey)));
        GroupedView.SortDescriptions.Add(new SortDescription(nameof(SupersetItemViewModel.GroupOrder), ListSortDirection.Ascending));
        GroupedView.SortDescriptions.Add(new SortDescription(nameof(SupersetItemViewModel.MinifigName), ListSortDirection.Ascending));
        GroupedView.Filter = FilterItem;
    }

    partial void OnSearchTextChanged(string value) => GroupedView.Refresh();

    private bool FilterItem(object obj)
    {
        if (obj is not SupersetItemViewModel item) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        var s = SearchText.Trim();
        return item.MinifigName.Contains(s, StringComparison.OrdinalIgnoreCase)
            || item.BlMinifigId.Contains(s, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Holt die Supersets-Liste (Cache + ggf. BL-Call) und kategorisiert.</summary>
    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            // 1) Supersets-Liste fuer (BL-Part, BL-Color)
            var supersets = await _catalog.GetSupersetsAsync(BlPartNo, BlColorId);

            // 2) Welche davon sind schon "wartende" Figuren in unserer DB?
            await using var dbCtx = await _ctxFactory.CreateDbContextAsync();
            var blIds = supersets.Select(s => s.ParentNo).Distinct().ToList();
            var trackedDict = await dbCtx.TrackedMinifigs.AsNoTracking()
                .Where(m => m.Status == TrackedMinifigStatus.Waiting
                            && blIds.Contains(m.BricklinkId!))
                .Include(m => m.RequiredParts)
                .Include(m => m.StorageBin)
                .ToDictionaryAsync(m => m.BricklinkId!);

            // 3) Pro Eintrag pruefen wie weit die Subset-Liste gecached ist
            //    (>=2 Eintraege => "vollstaendig", sonst "nur Name").
            Items.Clear();
            int waitingCount = 0, cachedCount = 0, nameOnlyCount = 0;
            foreach (var s in supersets.GroupBy(x => x.ParentNo).Select(g => g.First()))
            {
                var parentName = s.ParentNo; // wird mit Item-Cache angereichert
                var item = await _catalog.GetMinifigDetailsAsync(s.ParentNo);
                if (item != null) parentName = item.Name;

                var subsetsCount = (await _catalog.GetMinifigPartsAsync(s.ParentNo)).Count;

                SupersetCategory cat;
                if (trackedDict.TryGetValue(s.ParentNo, out var tracked))
                {
                    cat = SupersetCategory.AlreadyWaiting;
                    waitingCount++;
                }
                else if (subsetsCount > 1)
                {
                    cat = SupersetCategory.FullyCached;
                    cachedCount++;
                }
                else
                {
                    cat = SupersetCategory.NameOnly;
                    nameOnlyCount++;
                }

                Items.Add(new SupersetItemViewModel(this)
                {
                    BlMinifigId = s.ParentNo,
                    MinifigName = parentName,
                    Category = cat,
                    QuantityInMinifig = s.Quantity,
                    TriggerColorId = BlColorId,
                    TriggerPartNo = BlPartNo,
                    Tracked = tracked,
                    ImageUrl = item?.ImageUrl
                });
            }

            HeaderText = $"{ColorName} {PartName} ({BlPartNo}) erscheint in {Items.Count} Minifiguren " +
                         $"({waitingCount} wartend, {cachedCount} gecached, {nameOnlyCount} nur Name)";
            GroupedView.Refresh();

            // Bilder asynchron laden
            _ = LoadImagesAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "SupersetsDialog Load fehlgeschlagen");
            HeaderText = $"Fehler beim Laden: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadImagesAsync()
    {
        foreach (var item in Items.ToList())
        {
            try
            {
                var url = await _imageProvider.GetImageFileByBlAsync("M", item.BlMinifigId, null);
                if (!string.IsNullOrEmpty(url))
                {
                    System.Windows.Application.Current?.Dispatcher.Invoke(() => item.ImageUrl = url);
                }
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>Aktion: Trigger-Teil zu existierender wartender Figur zuordnen.</summary>
    public async Task AssignToWaitingAsync(SupersetItemViewModel item)
    {
        if (item.Tracked == null) return;
        // Find passenden TrackedMinifigPart
        var part = item.Tracked.RequiredParts.FirstOrDefault(p =>
            p.PartNumber == BlPartNo && p.ColorId == BlColorId
            && p.QuantityCollected < p.QuantityNeeded);
        if (part == null)
        {
            _notifications.ShowWarning($"Figur '{item.MinifigName}' braucht dieses Teil aktuell nicht mehr.");
            return;
        }
        try
        {
            var done = await _partLookup.AssignPartToMinifigAsync(part.Id);
            _notifications.ShowSuccess(done
                ? $"Teil zugeordnet – '{item.MinifigName}' jetzt KOMPLETT!"
                : $"Teil zu '{item.MinifigName}' zugeordnet.");
            AnyAssignedOrCollected = true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Assign aus Supersets-Dialog fehlgeschlagen");
            _notifications.ShowError($"Fehler: {ex.Message}");
        }
    }

    /// <summary>Aktion: neue Figur sammeln (mit Lagerfach-Auswahl).</summary>
    public async Task CollectMinifigAsync(SupersetItemViewModel item, int storageBinId)
    {
        try
        {
            var minifig = await _partLookup.CollectMinifigFromSupersetAsync(
                item.BlMinifigId, storageBinId, BlPartNo, BlColorId,
                item.QuantityInMinifig);
            var bin = await _binService.GetByIdAsync(storageBinId);
            _notifications.ShowSuccess(
                $"Figur '{minifig.Name}' angelegt in '{bin?.Label}', {item.QuantityInMinifig} Teil(e) gefunden.");
            AnyAssignedOrCollected = true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "CollectMinifig aus Supersets-Dialog fehlgeschlagen");
            _notifications.ShowError($"Fehler: {ex.Message}");
        }
    }

    /// <summary>Liefert alle Lagerfaecher (fuer den Sammeln-Auswahl-Dialog).</summary>
    public Task<List<StorageBin>> GetAllBinsAsync() => _binService.GetAllAsync();
    public Task<StorageBin?> GetNextFreeBinAsync() => _binService.GetNextFreeAsync();
}

/// <summary>Eine Zeile im SupersetsDialog (eine Minifigur).</summary>
public partial class SupersetItemViewModel : ObservableObject
{
    private readonly SupersetsDialogViewModel _parent;

    public string BlMinifigId { get; init; } = string.Empty;
    public string MinifigName { get; init; } = string.Empty;
    public SupersetCategory Category { get; init; }
    public int QuantityInMinifig { get; init; }
    public string TriggerPartNo { get; init; } = string.Empty;
    public int TriggerColorId { get; init; }
    public TrackedMinifig? Tracked { get; init; }

    [ObservableProperty]
    private string? _imageUrl;

    /// <summary>Gruppen-Label fuer den GroupHeader.</summary>
    public string GroupKey => Category switch
    {
        SupersetCategory.AlreadyWaiting => "⭐ SCHON IN DEINEN SAMMLUNGEN",
        SupersetCategory.FullyCached    => "✨ KOMPLETT GECACHED",
        _                                => "📦 NUR-NAMEN-BEKANNT"
    };

    /// <summary>Reihenfolge der Gruppen (1=Waiting, 2=Cached, 3=NameOnly).</summary>
    public int GroupOrder => (int)Category;

    public bool IsAlreadyWaiting => Category == SupersetCategory.AlreadyWaiting;
    public bool IsCollectable => Category != SupersetCategory.AlreadyWaiting;

    /// <summary>Status-Info fuer wartende Figur (Bin + Status).</summary>
    public string WaitingStatusText => Tracked != null
        ? $"({Tracked.StorageBin?.Label ?? "kein Fach"}, {Tracked.Status})"
        : string.Empty;

    public SupersetItemViewModel(SupersetsDialogViewModel parent)
    {
        _parent = parent;
    }

    public Task AssignAsync() => _parent.AssignToWaitingAsync(this);
    public Task CollectAsync(int storageBinId) => _parent.CollectMinifigAsync(this, storageBinId);
}

public enum SupersetCategory
{
    AlreadyWaiting = 1,
    FullyCached = 2,
    NameOnly = 3
}
