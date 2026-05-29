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
/// v0.1.24-beta.11: Verwaltung der persistent ignorierten Bauvorschlaege.
/// Wird vom Reset-Link im Bauvorschlags-Tab geoeffnet. Pro Zeile ein
/// "Wiederherstellen"-Button (entfernt diese eine Figur aus
/// IgnoredBuildSuggestions); unten "Alle wiederherstellen".
///
/// Anzeige holt Bild + Name aus dem bl_cache - bei Cache-Miss bleibt
/// der Name leer und das Bild faellt auf den Default zurueck (kein API-
/// Call, weil das Verwaltungs-Dialog kein guter Ort fuer BL-Quota-Verbrauch
/// ist).
/// </summary>
public partial class ManageIgnoredViewModel : ObservableObject
{
    private readonly IDbContextFactory<UserDataContext> _ctxFactory;
    private readonly IBlCacheRepository _blCache;
    private readonly IPartImageProvider _imageProvider;

    public ObservableCollection<IgnoredFigureRow> Items { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItems))]
    [NotifyPropertyChangedFor(nameof(EmptyText))]
    private int _itemCount;

    public bool HasItems => ItemCount > 0;
    public string EmptyText => "Keine ignorierten Figuren vorhanden.";

    /// <summary>True wenn der User mind. eine Aenderung gemacht hat - der Caller refresht dann.</summary>
    public bool HasChanges { get; private set; }

    public ManageIgnoredViewModel(
        IDbContextFactory<UserDataContext> ctxFactory,
        IBlCacheRepository blCache,
        IPartImageProvider imageProvider)
    {
        _ctxFactory = ctxFactory;
        _blCache = blCache;
        _imageProvider = imageProvider;
    }

    public async Task LoadAsync()
    {
        Items.Clear();
        try
        {
            await using var ctx = await _ctxFactory.CreateDbContextAsync();
            var ignored = await ctx.IgnoredBuildSuggestions
                .AsNoTracking()
                .OrderByDescending(i => i.IgnoredAt)
                .ToListAsync();

            foreach (var i in ignored)
            {
                var row = new IgnoredFigureRow
                {
                    BricklinkId = i.BricklinkId,
                    IgnoredAt = i.IgnoredAt,
                    Name = "(wird geladen…)"
                };
                Items.Add(row);
            }
            ItemCount = Items.Count;

            // Namen + Bilder im Hintergrund nachladen (cache-only, kein API-Call).
            _ = LoadDetailsAsync(ignored);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ManageIgnored: LoadAsync fehlgeschlagen");
        }
    }

    private async Task LoadDetailsAsync(IReadOnlyList<IgnoredBuildSuggestion> ignored)
    {
        foreach (var i in ignored)
        {
            var row = Items.FirstOrDefault(r => r.BricklinkId == i.BricklinkId);
            if (row == null) continue;

            string name = i.BricklinkId;
            string? imageUrl = null;
            try
            {
                var item = await _blCache.GetItemAsync("M", i.BricklinkId);
                if (item != null) name = item.Name;
            }
            catch (Exception ex)
            {
                // Cache-Miss ist erwartbar (kein API-Call hier) -> Default-Name.
                Log.Debug(ex, "ManageIgnored: Name aus Cache nicht ladbar ({Bl})", i.BricklinkId);
            }

            try
            {
                imageUrl = await _imageProvider.GetImageFileByBlAsync("M", i.BricklinkId, null);
            }
            catch (Exception ex)
            {
                // Kein Bild verfuegbar -> Default-Bild. Tolerierbar, nur Debug.
                Log.Debug(ex, "ManageIgnored: Bild nicht ladbar ({Bl})", i.BricklinkId);
            }

            var disp = Application.Current?.Dispatcher;
            if (disp != null && !disp.CheckAccess())
            {
                await disp.InvokeAsync(() =>
                {
                    row.Name = name;
                    row.ImageUrl = imageUrl;
                });
            }
            else
            {
                row.Name = name;
                row.ImageUrl = imageUrl;
            }
        }
    }

    [RelayCommand]
    public async Task RestoreAsync(IgnoredFigureRow? row)
    {
        if (row == null) return;
        try
        {
            await using var ctx = await _ctxFactory.CreateDbContextAsync();
            var entity = await ctx.IgnoredBuildSuggestions
                .FirstOrDefaultAsync(i => i.BricklinkId == row.BricklinkId);
            if (entity != null)
            {
                ctx.IgnoredBuildSuggestions.Remove(entity);
                await ctx.SaveChangesAsync();
            }
            Items.Remove(row);
            ItemCount = Items.Count;
            HasChanges = true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ManageIgnored: Restore fehlgeschlagen ({Bl})", row.BricklinkId);
        }
    }

    [RelayCommand]
    public async Task RestoreAllAsync()
    {
        try
        {
            await using var ctx = await _ctxFactory.CreateDbContextAsync();
            var all = await ctx.IgnoredBuildSuggestions.ToListAsync();
            if (all.Count == 0) return;
            ctx.IgnoredBuildSuggestions.RemoveRange(all);
            await ctx.SaveChangesAsync();
            Items.Clear();
            ItemCount = 0;
            HasChanges = true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ManageIgnored: RestoreAll fehlgeschlagen");
        }
    }
}

/// <summary>Eine Zeile im Verwaltungs-Dialog.</summary>
public partial class IgnoredFigureRow : ObservableObject
{
    public string BricklinkId { get; init; } = string.Empty;
    public DateTime IgnoredAt { get; init; }

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string? _imageUrl;
}
