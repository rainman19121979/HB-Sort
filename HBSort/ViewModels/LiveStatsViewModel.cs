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
/// Live-Stats-Tab im variablen Feld unten rechts.
/// Aggregiert "Heute" (DailyStats) + "Letzte 7 Tage" + aktueller Bestand.
/// Refresht automatisch bei jedem DataChanged-Event.
/// </summary>
public partial class LiveStatsViewModel : ObservableObject
{
    private readonly IDbContextFactory<UserDataContext> _ctxFactory;

    [ObservableProperty] private int _scansToday;
    [ObservableProperty] private int _completedToday;
    [ObservableProperty] private int _dismantledToday;
    [ObservableProperty] private int _scansLast7Days;
    [ObservableProperty] private int _completedLast7Days;
    [ObservableProperty] private int _currentWaitingCount;
    [ObservableProperty] private int _currentCompleteCount;
    [ObservableProperty] private int _currentFloatingTotal;
    [ObservableProperty] private string _streakText = string.Empty;

    public LiveStatsViewModel(
        IDbContextFactory<UserDataContext> ctxFactory,
        IMinifigPersistenceService persistence)
    {
        _ctxFactory = ctxFactory;
        persistence.DataChanged += (_, _) =>
        {
            var disp = Application.Current?.Dispatcher;
            if (disp != null && !disp.CheckAccess())
                disp.BeginInvoke(() => _ = RefreshAsync());
            else
                _ = RefreshAsync();
        };
        _ = RefreshAsync();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            await using var ctx = await _ctxFactory.CreateDbContextAsync();
            var today = DateTime.Today;
            var weekAgo = today.AddDays(-6);

            var todayStat = await ctx.DailyStats.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Date == today);
            ScansToday      = todayStat?.ScanCount ?? 0;
            CompletedToday  = todayStat?.MinifigsCompletedCount ?? 0;
            DismantledToday = todayStat?.MinifigsDismantledCount ?? 0;

            var weekStats = await ctx.DailyStats.AsNoTracking()
                .Where(s => s.Date >= weekAgo && s.Date <= today)
                .ToListAsync();
            ScansLast7Days     = weekStats.Sum(s => s.ScanCount);
            CompletedLast7Days = weekStats.Sum(s => s.MinifigsCompletedCount);

            CurrentWaitingCount = await ctx.TrackedMinifigs.AsNoTracking()
                .CountAsync(m => m.Status == TrackedMinifigStatus.Waiting);
            CurrentCompleteCount = await ctx.TrackedMinifigs.AsNoTracking()
                .CountAsync(m => m.Status == TrackedMinifigStatus.Complete);
            CurrentFloatingTotal = await ctx.FloatingParts.AsNoTracking()
                .SumAsync(f => (int?)f.Quantity) ?? 0;

            // Streak: aufeinanderfolgende Tage rueckwaerts mit ScanCount > 0.
            // Wir holen einen 30-Tage-Slice damit auch laengere Streaks abgedeckt sind.
            var streakStart = today.AddDays(-29);
            var streakStats = await ctx.DailyStats.AsNoTracking()
                .Where(s => s.Date >= streakStart && s.Date <= today)
                .ToListAsync();
            var streakMap = streakStats.ToDictionary(s => s.Date);

            var streakDays = 0;
            for (int d = 0; d < 30; d++)
            {
                var date = today.AddDays(-d);
                if (!streakMap.TryGetValue(date, out var stat) || stat.ScanCount == 0) break;
                streakDays++;
            }

            StreakText = streakDays switch
            {
                0 => "Heute neu starten!",
                1 => "1 Tag in Folge aktiv",
                _ => $"{streakDays} Tage in Folge aktiv"
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "LiveStats Refresh fehlgeschlagen");
        }
    }
}
