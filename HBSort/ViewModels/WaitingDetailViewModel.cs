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
/// PROMPT 11: Wartende-Detail-Tab. Liste aller wartenden Figuren mit den
/// FEHLENDEN Teilen pro Figur, damit man auf einen Blick sieht "ach,
/// mir fehlt noch X fuer Box 3".
/// </summary>
public partial class WaitingDetailViewModel : ObservableObject
{
    private readonly IDbContextFactory<UserDataContext> _ctxFactory;

    public ObservableCollection<WaitingFigureWithMissing> Items { get; } = new();

    [ObservableProperty]
    private string _summaryText = string.Empty;

    public WaitingDetailViewModel(
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

            var waiting = await ctx.TrackedMinifigs.AsNoTracking()
                .Where(m => m.Status == TrackedMinifigStatus.Waiting)
                .Include(m => m.RequiredParts)
                .Include(m => m.StorageBin)
                .OrderBy(m => m.Name)
                .ToListAsync();

            Items.Clear();
            foreach (var m in waiting)
            {
                var missing = m.RequiredParts
                    .Where(p => p.QuantityCollected < p.QuantityNeeded)
                    .Select(p => new MissingPartInfo
                    {
                        PartNumber = p.PartNumber,
                        ColorName = p.ColorName,
                        PartName = p.PartName,
                        Missing = p.QuantityNeeded - p.QuantityCollected
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
