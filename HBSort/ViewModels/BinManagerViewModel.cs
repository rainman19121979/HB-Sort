using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HBSort.Core.Services;
using HBSort.Services;
using Microsoft.EntityFrameworkCore;
using HBSort.Core.Database;
using Serilog;

namespace HBSort.ViewModels;

/// <summary>
/// ViewModel fuer den Settings-Tab "Lagerfaecher".
/// Liefert die Liste aller Faecher inkl. Belegung, plus Commands fuer
/// Anlegen / Umbenennen / Leeren / Loeschen.
///
/// Die Belegungs-Counts werden mit einem einzigen Query (per Bin-Id) ermittelt,
/// damit die Liste auch bei vielen Faechern zuegig laedt.
/// </summary>
public partial class BinManagerViewModel : ObservableObject
{
    private readonly IStorageBinService _binService;
    private readonly IDbContextFactory<UserDataContext> _ctxFactory;
    private readonly INotificationService _notifications;

    /// <summary>Alle Faecher (gefiltert + sortiert via View).</summary>
    public ObservableCollection<BinRowViewModel> Bins { get; } = new();

    /// <summary>Gruppierungs-/Sortier-View ueber Bins fuer das DataGrid.</summary>
    public ICollectionView BinsView { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilterAll))]
    [NotifyPropertyChangedFor(nameof(IsFilterFreeOnly))]
    [NotifyPropertyChangedFor(nameof(IsFilterOccupiedOnly))]
    private BinFilterMode _filterMode = BinFilterMode.All;

    public bool IsFilterAll => FilterMode == BinFilterMode.All;
    public bool IsFilterFreeOnly => FilterMode == BinFilterMode.FreeOnly;
    public bool IsFilterOccupiedOnly => FilterMode == BinFilterMode.OccupiedOnly;

    /// <summary>Sortier-Reihenfolge (per Dropdown).</summary>
    [ObservableProperty] private BinSortMode _sortMode = BinSortMode.LabelAsc;

    /// <summary>Anzeige-Text "X von Y Faechern" fuer den Header.</summary>
    [ObservableProperty] private string _summaryText = string.Empty;

    [ObservableProperty] private bool _isLoading;

    public BinManagerViewModel(
        IStorageBinService binService,
        IDbContextFactory<UserDataContext> ctxFactory,
        INotificationService notifications)
    {
        _binService = binService;
        _ctxFactory = ctxFactory;
        _notifications = notifications;

        BinsView = CollectionViewSource.GetDefaultView(Bins);
        BinsView.Filter = BinFilter;
        ApplySort();

        // Initial laden
        _ = ReloadAsync();
    }

    partial void OnFilterModeChanged(BinFilterMode value)
    {
        BinsView.Refresh();
        UpdateSummary();
    }

    partial void OnSortModeChanged(BinSortMode value)
    {
        ApplySort();
    }

    private bool BinFilter(object obj)
    {
        if (obj is not BinRowViewModel row) return false;
        return FilterMode switch
        {
            BinFilterMode.FreeOnly => row.IsFree,
            BinFilterMode.OccupiedOnly => !row.IsFree,
            _ => true
        };
    }

    private void ApplySort()
    {
        BinsView.SortDescriptions.Clear();
        switch (SortMode)
        {
            case BinSortMode.LabelAsc:
                BinsView.SortDescriptions.Add(new SortDescription(nameof(BinRowViewModel.Label), ListSortDirection.Ascending));
                break;
            case BinSortMode.LabelDesc:
                BinsView.SortDescriptions.Add(new SortDescription(nameof(BinRowViewModel.Label), ListSortDirection.Descending));
                break;
            case BinSortMode.CreatedNewest:
                BinsView.SortDescriptions.Add(new SortDescription(nameof(BinRowViewModel.CreatedAt), ListSortDirection.Descending));
                break;
            case BinSortMode.CreatedOldest:
                BinsView.SortDescriptions.Add(new SortDescription(nameof(BinRowViewModel.CreatedAt), ListSortDirection.Ascending));
                break;
            case BinSortMode.OccupiedFirst:
                // "Belegt zuerst" = IsFree=false an den Anfang. SortDescription auf bool: false < true,
                // also fuehrt Ascending dazu, dass Belegte (IsFree=false) oben stehen.
                BinsView.SortDescriptions.Add(new SortDescription(nameof(BinRowViewModel.IsFree), ListSortDirection.Ascending));
                BinsView.SortDescriptions.Add(new SortDescription(nameof(BinRowViewModel.Label), ListSortDirection.Ascending));
                break;
        }
    }

    /// <summary>
    /// Laed alle Faecher inkl. Belegungs-Counts neu aus der DB.
    /// Wird beim Oeffnen des Tabs und nach jeder schreibenden Aktion aufgerufen.
    /// </summary>
    [RelayCommand]
    public async Task ReloadAsync()
    {
        IsLoading = true;
        try
        {
            var bins = await _binService.GetAllAsync();

            // Belegungs-Counts in EINEM Schwung aus der DB holen, statt pro Bin
            // einen Query abzusetzen.
            await using var ctx = await _ctxFactory.CreateDbContextAsync();
            // Konsistent mit BinDetailDialog: alle dem Fach zugeordneten Figuren
            // zaehlen (egal welcher Status). Dismantled-Figuren werden in
            // MinifigSummaryViewModel.GiveUpAsync ohnehin vom Fach geloest, also
            // sind hier in der Praxis WAITING + COMPLETE + SOLD relevant.
            var minifigCounts = await ctx.TrackedMinifigs.AsNoTracking()
                .Where(m => m.StorageBinId != null)
                .GroupBy(m => m.StorageBinId!.Value)
                .Select(g => new { BinId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.BinId, x => x.Count);

            var partCounts = await ctx.FloatingParts.AsNoTracking()
                .GroupBy(p => p.StorageBinId)
                .Select(g => new { BinId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.BinId, x => x.Count);

            Bins.Clear();
            foreach (var bin in bins)
            {
                var mfg = minifigCounts.TryGetValue(bin.Id, out var m) ? m : 0;
                var fp = partCounts.TryGetValue(bin.Id, out var p) ? p : 0;
                Bins.Add(new BinRowViewModel(bin, mfg, fp));
            }
            UpdateSummary();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "BinManager Reload fehlgeschlagen");
            _notifications.ShowError($"Fehler beim Laden der Faecher: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void UpdateSummary()
    {
        var total = Bins.Count;
        var free = Bins.Count(b => b.IsFree);
        var visible = BinsView.Cast<object>().Count();
        SummaryText = total == visible
            ? $"{total} Faecher ({free} frei, {total - free} belegt)"
            : $"{visible} von {total} Faechern sichtbar ({free} frei, {total - free} belegt)";
    }

    /// <summary>Filter-Setzer fuer die Radio-Buttons.</summary>
    public void SetFilter(BinFilterMode mode) => FilterMode = mode;
}

/// <summary>Filter-Modi fuer die Lagerfach-Liste.</summary>
public enum BinFilterMode
{
    All,
    FreeOnly,
    OccupiedOnly
}

/// <summary>Sortier-Modi fuer die Lagerfach-Liste.</summary>
public enum BinSortMode
{
    LabelAsc,
    LabelDesc,
    CreatedNewest,
    CreatedOldest,
    OccupiedFirst
}
