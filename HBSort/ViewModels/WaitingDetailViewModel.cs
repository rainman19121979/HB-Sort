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
/// Wartende-Detail-Tab. Liste aller wartenden Figuren mit den FEHLENDEN
/// Teilen pro Figur, damit man auf einen Blick sieht "ach, mir fehlt
/// noch X fuer Box 3".
/// </summary>
public partial class WaitingDetailViewModel : ObservableObject, IDisposable
{
    private readonly IDbContextFactory<UserDataContext> _ctxFactory;

    // Audit K-2: Subscription als Field, damit Dispose() abmelden kann.
    private readonly IMinifigPersistenceService _persistence;
    private readonly EventHandler _onDataChanged;

    public ObservableCollection<WaitingFigureWithMissing> Items { get; } = new();

    [ObservableProperty]
    private string _summaryText = string.Empty;

    public WaitingDetailViewModel(
        IDbContextFactory<UserDataContext> ctxFactory,
        IMinifigPersistenceService persistence)
    {
        _ctxFactory = ctxFactory;
        _persistence = persistence;

        _onDataChanged = (_, _) =>
        {
            var disp = Application.Current?.Dispatcher;
            if (disp != null && !disp.CheckAccess())
                disp.BeginInvoke(() => _ = RefreshAsync());
            else
                _ = RefreshAsync();
        };
        _persistence.DataChanged += _onDataChanged;
        _ = RefreshAsync();
    }

    /// <summary>Audit K-2: Unsubscribe beim ServiceProvider-Dispose.</summary>
    public void Dispose()
    {
        _persistence.DataChanged -= _onDataChanged;
        GC.SuppressFinalize(this);
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            await using var ctx = await _ctxFactory.CreateDbContextAsync();

            var waiting = await ctx.TrackedMinifigs.AsNoTracking()
                .Where(m => m.Status == TrackedMinifigStatus.Waiting)
                .Include(m => m.RequiredParts)
                .Include(m => m.StorageBin)
                .OrderBy(m => m.Name)
                .ToListAsync();

            Items.Clear();
            foreach (var m in waiting)
            {
                // v0.1.24-beta.8 Phase 3: "noch fehlend" inkludiert nicht
                // mehr Teile die im BL-Shop reserviert sind - die sind
                // effektiv beschafft, nur physisch noch nicht da.
                var missing = m.RequiredParts
                    .Where(p => p.EffectivelyMissing > 0)
                    .Select(p => new MissingPartInfo
                    {
                        PartNumber = p.PartNumber,
                        ColorName = p.ColorName,
                        PartName = p.PartName,
                        Missing = p.EffectivelyMissing
                    })
                    .ToList();

                if (missing.Count == 0) continue; // bereits komplett -> ueberspringen

                Items.Add(new WaitingFigureWithMissing
                {
                    MinifigId = m.Id,
                    Name = m.Name,
                    BricklinkId = m.BricklinkId ?? m.FigNum,
                    BinLabel = m.StorageBin?.Label ?? "(kein Fach)",
                    ImageUrl = m.LocalImagePath ?? m.ImageUrl,
                    MissingParts = missing,
                    MissingCount = missing.Count
                });
            }

            SummaryText = Items.Count switch
            {
                0 => "Keine wartenden Figuren mit fehlenden Teilen.",
                1 => "1 Figur wartet auf Teile",
                _ => $"{Items.Count} Figuren warten auf Teile"
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "WaitingDetail Refresh fehlgeschlagen");
            SummaryText = $"Fehler: {ex.Message}";
        }
    }
}

/// <summary>Eine wartende Figur mit ihren fehlenden Teilen.</summary>
public class WaitingFigureWithMissing
{
    public int MinifigId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string BricklinkId { get; init; } = string.Empty;
    public string BinLabel { get; init; } = string.Empty;
    public string? ImageUrl { get; init; }
    public List<MissingPartInfo> MissingParts { get; init; } = new();
    public int MissingCount { get; init; }
}

/// <summary>Ein einzelnes fehlendes Teil einer wartenden Figur.</summary>
public class MissingPartInfo
{
    public string PartNumber { get; init; } = string.Empty;
    public string ColorName { get; init; } = string.Empty;
    public string PartName { get; init; } = string.Empty;
    public int Missing { get; init; }

    public string Label => $"{Missing}x {PartName} ({ColorName}) [BL:{PartNumber}]";
}
