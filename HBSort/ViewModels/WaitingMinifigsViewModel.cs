using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HBSort.Core.Database;
using HBSort.Core.Models;
using HBSort.Core.Services;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace HBSort.ViewModels;

/// <summary>
/// ViewModel fuer die Lagerfach-Uebersicht (R2,C2 Quadrant).
///
/// Zeigt alle Faecher mit Filter "Alle / Nur belegt" (Default belegt). Pro Fach
/// ein klappbares Item mit Label + Status + "Inhalt"-Button und einer Inline-Liste
/// der wartenden Figuren (max 5).
///
/// Refresht automatisch wenn der MinifigPersistenceService DataChanged feuert
/// (Persistierung, Aufgeben, Verschieben, Part-Zuordnung).
/// </summary>
public partial class WaitingMinifigsViewModel : ObservableObject
{
    private readonly IDbContextFactory<UserDataContext> _ctxFactory;

    /// <summary>Alle Faecher (gefiltert nach FilterMode) als BinOverviewItems.</summary>
    public ObservableCollection<BinOverviewItemViewModel> Bins { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilterAll))]
    [NotifyPropertyChangedFor(nameof(IsFilterOccupiedOnly))]
    private BinOverviewFilter _filterMode = BinOverviewFilter.OccupiedOnly;

    public bool IsFilterAll => FilterMode == BinOverviewFilter.All;
    public bool IsFilterOccupiedOnly => FilterMode == BinOverviewFilter.OccupiedOnly;

    [ObservableProperty] private string _summaryText = string.Empty;
    [ObservableProperty] private bool _isEmpty = true;

    public WaitingMinifigsViewModel(
        IDbContextFactory<UserDataContext> ctxFactory,
        IMinifigPersistenceService persistenceService)
    {
        _ctxFactory = ctxFactory;

        // Auf Aenderungen am Bestand reagieren – Refresh auf UI-Thread.
        persistenceService.DataChanged += (_, _) =>
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
                dispatcher.BeginInvoke(() => _ = RefreshAsync());
            else
                _ = RefreshAsync();
        };

        _ = RefreshAsync();
    }

    partial void OnFilterModeChanged(BinOverviewFilter value) => _ = RefreshAsync();

    public void SetFilter(BinOverviewFilter mode) => FilterMode = mode;

    /// <summary>Lieast alle Faecher inkl. WAITING-Figuren + Counts aus der DB.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            await using var ctx = await _ctxFactory.CreateDbContextAsync();

            // Alle Faecher mit Counts in einem Rutsch
            var allBins = await ctx.StorageBins.AsNoTracking()
                .OrderBy(b => b.Label)
                .ToListAsync();

            // Map BinId -> Counts
            var minifigCounts = await ctx.TrackedMinifigs.AsNoTracking()
                .Where(m => m.StorageBinId != null)
                .GroupBy(m => m.StorageBinId!.Value)
                .Select(g => new { BinId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.BinId, x => x.Count);

            var floatCounts = await ctx.FloatingParts.AsNoTracking()
                .GroupBy(p => p.StorageBinId)
                .Select(g => new { BinId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.BinId, x => x.Count);

            // Pro Fach: WAITING-Figuren-Liste (max 5) + zugehoerige RequiredParts
            // fuer den Fortschritt
            var waiting = await ctx.TrackedMinifigs.AsNoTracking()
                .Where(m => m.StorageBinId != null
                         && m.Status == TrackedMinifigStatus.Waiting)
                .Include(m => m.RequiredParts)
                .Include(m => m.StorageBin)
                .OrderBy(m => m.Name)
                .ToListAsync();
            var waitingByBin = waiting
                .GroupBy(m => m.StorageBinId!.Value)
                .ToDictionary(g => g.Key, g => g.ToList());

            // Phase 6: zusaetzlich Komplette Figuren pro Fach (analog zu Waiting).
            var complete = await ctx.TrackedMinifigs.AsNoTracking()
                .Where(m => m.StorageBinId != null
                         && m.Status == TrackedMinifigStatus.Complete)
                .Include(m => m.RequiredParts)
                .Include(m => m.StorageBin)
                .OrderByDescending(m => m.CompletedAt)
                .ToListAsync();
            var completeByBin = complete
                .GroupBy(m => m.StorageBinId!.Value)
                .ToDictionary(g => g.Key, g => g.ToList());

            // VMs bauen, dabei nach Filter filtern
            // Bestehende IsExpanded-Werte erhalten beim Refresh, damit nichts zuklappt.
            var prevExpanded = Bins.ToDictionary(b => b.Id, b => b.IsExpanded);

            Bins.Clear();
            foreach (var b in allBins)
            {
                var mfg = minifigCounts.TryGetValue(b.Id, out var mc) ? mc : 0;
                var fp = floatCounts.TryGetValue(b.Id, out var fc) ? fc : 0;
                var isFree = mfg == 0 && fp == 0;

                if (FilterMode == BinOverviewFilter.OccupiedOnly && isFree) continue;

                waitingByBin.TryGetValue(b.Id, out var waitingList);
                completeByBin.TryGetValue(b.Id, out var completeList);
                var item = new BinOverviewItemViewModel(b, mfg, fp,
                    waitingList ?? new List<TrackedMinifig>(),
                    completeList ?? new List<TrackedMinifig>())
                {
                    IsExpanded = prevExpanded.TryGetValue(b.Id, out var exp) ? exp : !isFree
                };
                Bins.Add(item);
            }

            IsEmpty = Bins.Count == 0;

            // Phase 6: Footer zeigt Fach-Anzahl + Total-Counts der Figuren.
            var totalWaiting = waiting.Count;
            var totalComplete = complete.Count;
            var binText = FilterMode == BinOverviewFilter.OccupiedOnly
                ? $"{Bins.Count} belegte(s) Fach(/Faecher)"
                : $"{Bins.Count} Fach(/Faecher) gesamt";
            SummaryText = totalComplete > 0
                ? $"{binText} • Wartende: {totalWaiting} • Komplett: {totalComplete}"
                : $"{binText} • Wartende: {totalWaiting}";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "BinOverview Refresh fehlgeschlagen");
        }
    }
}

/// <summary>
/// Ein Lagerfach in der Uebersichts-Liste. Klappbar; bei expanded werden bis zu
/// 5 wartende Figuren als Mini-Cards gezeigt.
/// </summary>
public partial class BinOverviewItemViewModel : ObservableObject
{
    private const int MaxWaitingShown = 5;

    public int Id { get; }
    public string Label { get; }
    public int MinifigCount { get; }
    public int FloatingPartCount { get; }

    /// <summary>"Frei" wenn weder Figuren noch FloatingParts.</summary>
    public bool IsFree => MinifigCount == 0 && FloatingPartCount == 0;

    public string StatusText => IsFree
        ? "Frei"
        : $"Belegt ({MinifigCount} Fig, {FloatingPartCount} Teile)";

    /// <summary>Bis zu 5 wartende Figuren fuer Inline-Anzeige.</summary>
    public ObservableCollection<WaitingMinifigViewModel> WaitingMinifigs { get; } = new();

    /// <summary>
    /// Phase 6: bis zu 5 komplette Figuren in diesem Fach. Eigene Sektion in
    /// der UI, mit gruener Markierung. Bei 0 Eintraegen blendet die View die
    /// Sektion via HasCompletes-Binding aus.
    /// </summary>
    public ObservableCollection<WaitingMinifigViewModel> CompleteMinifigs { get; } = new();

    /// <summary>True wenn mind. eine komplette Figur in diesem Fach liegt.</summary>
    public bool HasCompletes => CompleteMinifigs.Count > 0;

    /// <summary>True wenn mind. eine wartende Figur in diesem Fach liegt.</summary>
    public bool HasWaitings => WaitingMinifigs.Count > 0;

    /// <summary>Wenn mehr als 5 wartende Figuren existieren – Hinweis-Zahl.</summary>
    public int MoreCount { get; }
    public bool HasMore => MoreCount > 0;
    public string MoreLabel => $"+ {MoreCount} weitere wartende Figur(en)";

    /// <summary>Wenn mehr als 5 komplette Figuren existieren – Hinweis-Zahl.</summary>
    public int MoreCompleteCount { get; }
    public bool HasMoreCompletes => MoreCompleteCount > 0;
    public string MoreCompleteLabel => $"+ {MoreCompleteCount} weitere komplette Figur(en)";

    /// <summary>
    /// Header-Text der Komplett-Sektion (mit Anzahl), z.B. "✓ Komplett (3)".
    /// </summary>
    public string CompleteHeader => $"✓ Komplett ({CompleteMinifigs.Count}{(MoreCompleteCount > 0 ? "+" : "")})";

    /// <summary>Header-Text der Waiting-Sektion (mit Anzahl), z.B. "Wartend (2)".</summary>
    public string WaitingHeader => $"Wartend ({WaitingMinifigs.Count}{(MoreCount > 0 ? "+" : "")})";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandIcon))]
    private bool _isExpanded;

    public string ExpandIcon => IsExpanded ? "▼" : "▶";

    public BinOverviewItemViewModel(StorageBin bin, int minifigCount, int floatingPartCount,
        IReadOnlyList<TrackedMinifig> waitingMinifigs,
        IReadOnlyList<TrackedMinifig> completeMinifigs)
    {
        Id = bin.Id;
        Label = bin.Label;
        MinifigCount = minifigCount;
        FloatingPartCount = floatingPartCount;

        var take = waitingMinifigs.Take(MaxWaitingShown).ToList();
        foreach (var m in take) WaitingMinifigs.Add(new WaitingMinifigViewModel(m));
        MoreCount = Math.Max(0, waitingMinifigs.Count - take.Count);

        var takeC = completeMinifigs.Take(MaxWaitingShown).ToList();
        foreach (var m in takeC) CompleteMinifigs.Add(new WaitingMinifigViewModel(m));
        MoreCompleteCount = Math.Max(0, completeMinifigs.Count - takeC.Count);
    }

    public void Toggle() => IsExpanded = !IsExpanded;
}

/// <summary>Eine Mini-Karte fuer eine wartende Figur (innerhalb eines Bin-Items).</summary>
public partial class WaitingMinifigViewModel : ObservableObject
{
    public int Id { get; }
    public string Name { get; }
    public string BricklinkId { get; }
    public string BinLabel { get; }
    public int? StorageBinId { get; }
    public string? ImageUrl { get; }

    public int TotalParts { get; }
    public int CompletedParts { get; }
    public double ProgressFraction => TotalParts == 0 ? 0 : (double)CompletedParts / TotalParts;
    public string ProgressLabel => $"{CompletedParts}/{TotalParts} Teile";

    public WaitingMinifigViewModel(TrackedMinifig m)
    {
        Id = m.Id;
        Name = m.Name;
        BricklinkId = m.BricklinkId ?? m.FigNum;
        StorageBinId = m.StorageBinId;
        BinLabel = m.StorageBin?.Label ?? "(kein Fach)";
        ImageUrl = m.LocalImagePath ?? m.ImageUrl;
        TotalParts = m.RequiredParts.Count;
        CompletedParts = m.RequiredParts.Count(p => p.QuantityCollected >= p.QuantityNeeded);
    }
}

public enum BinOverviewFilter
{
    All,
    OccupiedOnly
}
