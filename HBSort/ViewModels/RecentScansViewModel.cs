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
/// "Letzte Scans"-Tab. Zeigt die juengsten 50 ScanEvents chronologisch
/// absteigend. Refresht automatisch bei DataChanged.
/// </summary>
public partial class RecentScansViewModel : ObservableObject
{
    private const int MaxItems = 50;

    private readonly IDbContextFactory<UserDataContext> _ctxFactory;

    public ObservableCollection<ScanEventDisplay> Items { get; } = new();

    public RecentScansViewModel(
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
            var raw = await ctx.ScanEvents.AsNoTracking()
                .OrderByDescending(e => e.Timestamp)
                .Take(MaxItems)
                .ToListAsync();

            Items.Clear();
            foreach (var e in raw)
            {
                var local = e.Timestamp.ToLocalTime();
                Items.Add(new ScanEventDisplay
                {
                    Timestamp = local,
                    TimeLabel = local.ToString("HH:mm"),
                    DateLabel = local.ToString("dd.MM."),
                    Description = e.ResultDescription,
                    TypeLabel = e.Type == ScanType.MinifigScan ? "Figur" : "Teil",
                    ConfidenceLabel = e.Confidence.HasValue
                        ? $"{(e.Confidence.Value * 100):F0}%"
                        : string.Empty
                });
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "RecentScans Refresh fehlgeschlagen");
        }
    }
}

/// <summary>Anzeige-Datenklasse fuer eine Zeile in der Letzte-Scans-Liste.</summary>
public class ScanEventDisplay
{
    public DateTime Timestamp { get; init; }
    public string TimeLabel { get; init; } = string.Empty;
    public string DateLabel { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string TypeLabel { get; init; } = string.Empty;
    public string ConfidenceLabel { get; init; } = string.Empty;
}
