using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HBSort.Core.Models;
using HBSort.Core.Models.Bricklink;
using HBSort.Core.Models.Exceptions;
using HBSort.Core.Services;
using HBSort.Services;
using Serilog;

namespace HBSort.ViewModels;

/// <summary>
/// v0.1.24-beta.7 Phase 2: Tab "BrickLink Inventar".
/// Zeigt den lokalen Spiegel des BL-Store-Inventars (BlInventoryLots) als
/// gefilterte/sortierte DataGrid-Liste. Sync-Knopf ruft denselben Service-
/// Pfad wie der Settings-Tab (IBlInventoryService.SyncInventoryAsync) -
/// kein doppelter Codepfad. Nach Sync (egal woher ausgeloest) refresht
/// die Liste via InventoryChanged-Event.
///
/// <para>
/// Nachschaerfung (beta.7): LoadAsync reichert die Zeilen mit Catalog-
/// Name + Color-Name aus bl_cache.db an (Bulk-Lookup), und lazy
/// IPartImageProvider-basierte Thumbnails kommen via Background-Task ohne
/// den initialen Tab-Wechsel zu blockieren.
/// </para>
/// </summary>
public partial class BlInventoryViewModel : ObservableObject, IDisposable
{
    private readonly IBlInventoryService _inventory;
    private readonly IBlCacheRepository _blCache;
    private readonly IPartImageProvider _imageProvider;
    private readonly INotificationService _notifications;
    private readonly EventHandler _onInventoryChanged;

    // Lazy-Image-Loading: nur sichtbare Zeilen triggern ueber den
    // Thumbnail_DataContextChanged-Handler im Code-Behind. Concurrency-Drossel
    // 3 parallele Calls damit der BL-Image-CDN bei ~10k Rows nicht ueberrannt
    // wird. Der eigentliche Cache + 30-Tage-TTL lebt im IPartImageProvider /
    // IPersistentImageCache.
    private readonly SemaphoreSlim _thumbnailSemaphore = new(3);

    // CTS fuer in-flight-Thumbnail-Loads. Wird beim Re-Load zurueckgesetzt
    // damit alte Requests auf eine alte Items-Snapshot nicht mehr in die
    // neue Liste schreiben.
    private CancellationTokenSource? _imageLoadCts;

    /// <summary>Alle geladenen Lots (Quelle fuer ItemsView).</summary>
    public ObservableCollection<BlInventoryRow> Items { get; } = new();

    /// <summary>Gefiltert + sortiert via ICollectionView. Die View ist im XAML gebunden.</summary>
    public ICollectionView ItemsView { get; }

    // ---- Filter-Properties ----

    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>"" = Alle, "P" = Part, "M" = Minifig, "S" = Set.</summary>
    [ObservableProperty]
    private string _filterByItemType = string.Empty;

    /// <summary>"" = Alle, "N" = Neu, "U" = Gebraucht.</summary>
    [ObservableProperty]
    private string _filterByCondition = string.Empty;

    // ---- State ----

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isSyncing;

    [ObservableProperty]
    private bool _isEmpty = true;

    [ObservableProperty]
    private string _footerText = string.Empty;

    [ObservableProperty]
    private string _syncStatusText = string.Empty;

    /// <summary>
    /// Aktuell markierte Zeile - steuert das Detail-Panel rechts. Null = Panel
    /// ausgeblendet (Empty-State im XAML via DataTrigger).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedRow))]
    private BlInventoryRow? _selectedRow;

    public bool HasSelectedRow => SelectedRow != null;

    public IReadOnlyList<BlInventoryFilterOption> ItemTypeOptions { get; } = new[]
    {
        new BlInventoryFilterOption("Alle Typen", string.Empty),
        new BlInventoryFilterOption("Part", "P"),
        new BlInventoryFilterOption("Minifig", "M"),
        new BlInventoryFilterOption("Set", "S")
    };

    public IReadOnlyList<BlInventoryFilterOption> ConditionOptions { get; } = new[]
    {
        new BlInventoryFilterOption("Alle Zustaende", string.Empty),
        new BlInventoryFilterOption("Neu", "N"),
        new BlInventoryFilterOption("Gebraucht", "U")
    };

    public BlInventoryViewModel(
        IBlInventoryService inventory,
        IBlCacheRepository blCache,
        IPartImageProvider imageProvider,
        INotificationService notifications)
    {
        _inventory = inventory;
        _blCache = blCache;
        _imageProvider = imageProvider;
        _notifications = notifications;

        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = FilterPredicate;
        ItemsView.SortDescriptions.Add(new SortDescription(nameof(BlInventoryRow.ItemType), ListSortDirection.Ascending));
        ItemsView.SortDescriptions.Add(new SortDescription(nameof(BlInventoryRow.ItemNo), ListSortDirection.Ascending));
        ItemsView.SortDescriptions.Add(new SortDescription(nameof(BlInventoryRow.ColorName), ListSortDirection.Ascending));

        _onInventoryChanged = (_, _) =>
        {
            var disp = Application.Current?.Dispatcher;
            if (disp != null && !disp.CheckAccess())
                disp.BeginInvoke(() => _ = LoadAsync());
            else
                _ = LoadAsync();
        };
        _inventory.InventoryChanged += _onInventoryChanged;

        _ = LoadAsync();
    }

    public void Dispose()
    {
        _inventory.InventoryChanged -= _onInventoryChanged;
        _imageLoadCts?.Cancel();
        _imageLoadCts?.Dispose();
        _thumbnailSemaphore.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Liest Inventar aus DB, reichert mit Catalog-Name + Color-Name aus
    /// bl_cache.db an und kickt Lazy-Image-Loading.
    /// </summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var lots = await _inventory.GetInventoryAsync();

            // 1) Bulk-Lookup Catalog-Daten (Name + ImageUrl) aus bl_cache.db.
            //    Eine SQL-Query pro ItemType - schnell auch fuer 10k Lots.
            var keys = lots.Select(l => (l.ItemType, l.ItemNo)).Distinct().ToList();
            var summaries = await _blCache.GetItemSummariesAsync(keys);

            // 2) Color-Map aus bl_colors. GetAllColorsAsync ist cache-resident.
            var colors = await TryGetColorsAsync();
            var colorMap = colors.ToDictionary(c => c.ColorId, c => c.Name);

            // 3) Rows bauen mit Enrichment.
            Items.Clear();
            foreach (var lot in lots)
            {
                summaries.TryGetValue((lot.ItemType, lot.ItemNo), out var summary);
                string? cacheColorName = null;
                if (lot.ColorId.HasValue
                    && colorMap.TryGetValue(lot.ColorId.Value, out var cn))
                {
                    cacheColorName = cn;
                }
                Items.Add(BlInventoryRow.From(lot, summary, cacheColorName));
            }

            IsEmpty = lots.Count == 0;
            ItemsView.Refresh();
            UpdateFooter(lots);

            // 4) Lazy-Image-Loading: alten in-flight-CTS canceln damit Loads
            //    fuer die VORHERIGE Items-Snapshot nicht mehr auf das neue
            //    Item schreiben. Thumbnails fuer sichtbare Zeilen werden
            //    durch den Thumbnail_DataContextChanged-Handler getriggert
            //    sobald die jeweilige Zeile in den Viewport scrollt.
            _imageLoadCts?.Cancel();
            _imageLoadCts?.Dispose();
            _imageLoadCts = new CancellationTokenSource();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "BlInventory.LoadAsync fehlgeschlagen");
            FooterText = $"Fehler beim Laden: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// v0.1.24-beta.7 Nachschaerfung: lazy Thumbnail-Load per Zeile. Wird vom
    /// Code-Behind aufgerufen sobald eine Row im DataGrid einen
    /// <see cref="System.Windows.FrameworkElement.DataContextChanged"/> auf
    /// dem Thumbnail-Image bekommt (= Row ist im Viewport aufgetaucht).
    ///
    /// <para>Garantien:</para>
    /// <list type="bullet">
    ///   <item>Pro Row maximal ein Provider-Call (Doppel-Trigger durch Row-
    ///   Recycling werden via <see cref="BlInventoryRow.TryClaimImageLoadSlot"/>
    ///   abgefangen).</item>
    ///   <item>Bei bereits gesetzter <c>ImageUrl</c> sofortiger No-Op.</item>
    ///   <item>Max 3 parallele Requests (Semaphore) — wenn der User durch
    ///   die Liste scrollt, baut sich keine Queue von 50+ gleichzeitigen
    ///   Downloads auf.</item>
    ///   <item>30-Tage-Cache + Disk-Hits liefert <see cref="IPartImageProvider"/>
    ///   transparent — wir machen hier nichts cache-spezifisches.</item>
    /// </list>
    /// </summary>
    public async Task EnsureThumbnailAsync(BlInventoryRow row)
    {
        if (row == null) return;
        if (!string.IsNullOrEmpty(row.ImageUrl)) return;
        if (!row.TryClaimImageLoadSlot()) return;

        var ct = _imageLoadCts?.Token ?? CancellationToken.None;
        var dispatcher = Application.Current?.Dispatcher;

        try
        {
            await _thumbnailSemaphore.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Tab wurde geschlossen oder Re-Load ist gestartet - sauber raus,
            // Slot bleibt geclaimt damit ein spaeterer Re-Trigger den
            // neueren Lauf nutzt.
            return;
        }
        try
        {
            if (ct.IsCancellationRequested) return;
            var url = await _imageProvider.GetImageFileByBlAsync(
                row.ItemType, row.ItemNo, row.ColorId, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested || string.IsNullOrEmpty(url)) return;

            if (dispatcher != null)
                await dispatcher.InvokeAsync(() => row.ImageUrl = url);
            else
                row.ImageUrl = url;
        }
        catch (OperationCanceledException) { /* siehe oben */ }
        catch (Exception ex)
        {
            Log.Debug(ex,
                "BlInventory: Thumbnail fuer {Type}/{No}/{Color} nicht ladbar",
                row.ItemType, row.ItemNo, row.ColorId);
        }
        finally
        {
            _thumbnailSemaphore.Release();
        }
    }

    /// <summary>
    /// Wenn der User eine Zeile selektiert, deren Thumbnail noch nicht
    /// geladen wurde (z.B. Detail-Panel-Bild bei einer noch nicht
    /// gerenderten Row), erzwingen wir den Lazy-Load explizit.
    /// </summary>
    partial void OnSelectedRowChanged(BlInventoryRow? value)
    {
        if (value != null) _ = EnsureThumbnailAsync(value);
    }

    private async Task<List<BlColor>> TryGetColorsAsync()
    {
        try
        {
            return await _blCache.GetAllColorsAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "BlInventory: GetAllColorsAsync fehlgeschlagen - Farben werden aus dem Sync-Cache angezeigt");
            return new List<BlColor>();
        }
    }

    /// <summary>
    /// Sync-Knopf im Tab. Identische Service-Logik wie Settings-Tab —
    /// IBlInventoryService.SyncInventoryAsync ist die eine Codequelle.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSync))]
    public async Task SyncAsync()
    {
        IsSyncing = true;
        SyncCommand.NotifyCanExecuteChanged();
        SyncStatusText = "Synchronisiere...";
        try
        {
            var count = await _inventory.SyncInventoryAsync();
            SyncStatusText = $"{count} Lots synchronisiert (UTC {DateTime.UtcNow:HH:mm:ss})";
            _notifications.ShowSuccess($"{count} Lots aus dem BrickLink-Store synchronisiert.");
        }
        catch (BricklinkAuthException auth)
        {
            Log.Warning(auth, "BlInventory-Sync: Auth fehlt/falsch");
            SyncStatusText = $"Auth-Fehler: {auth.Message}";
            _notifications.ShowError(
                "BrickLink-Tokens fehlen oder sind ungueltig. Bitte in den Einstellungen pruefen.");
        }
        catch (BricklinkRateLimitException rl)
        {
            Log.Warning(rl, "BlInventory-Sync: Rate-Limit");
            SyncStatusText = $"Rate-Limit: {rl.Message}";
            _notifications.ShowError(rl.Message);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "BlInventory-Sync fehlgeschlagen");
            SyncStatusText = $"Fehler: {ex.Message}";
            _notifications.ShowError($"Inventar-Sync fehlgeschlagen: {ex.Message}");
        }
        finally
        {
            IsSyncing = false;
            SyncCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanSync() => !IsSyncing;

    // ---- Filter ----

    private bool FilterPredicate(object obj)
    {
        if (obj is not BlInventoryRow r) return false;

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var q = SearchText.Trim();
            // Beschreibung erweitert: ItemNo + DescriptionDisplay (Catalog-Name
            // wenn vorhanden, sonst BL-Description-Fallback). Color matcht
            // optional - User-Suche "red" trifft.
            var hit = r.ItemNo.Contains(q, StringComparison.OrdinalIgnoreCase)
                   || r.DescriptionDisplay.Contains(q, StringComparison.OrdinalIgnoreCase)
                   || r.ColorName.Contains(q, StringComparison.OrdinalIgnoreCase);
            if (!hit) return false;
        }

        if (!string.IsNullOrEmpty(FilterByItemType) && r.ItemType != FilterByItemType) return false;
        if (!string.IsNullOrEmpty(FilterByCondition) && r.Condition != FilterByCondition) return false;
        return true;
    }

    partial void OnSearchTextChanged(string value) => ItemsView.Refresh();
    partial void OnFilterByItemTypeChanged(string value) => ItemsView.Refresh();
    partial void OnFilterByConditionChanged(string value) => ItemsView.Refresh();

    [RelayCommand]
    public void ResetFilters()
    {
        SearchText = string.Empty;
        FilterByItemType = string.Empty;
        FilterByCondition = string.Empty;
    }

    private void UpdateFooter(IReadOnlyList<BlInventoryLot> lots)
    {
        if (lots.Count == 0)
        {
            FooterText = string.Empty;
            return;
        }

        var totalLots = lots.Count;
        var totalAvailable = lots.Sum(l => Math.Max(0, l.Quantity - l.ReservedQuantity));
        var lastSync = lots.Max(l => l.LastSyncedAt).ToLocalTime();
        FooterText =
            $"{totalLots:N0} Lots · {totalAvailable:N0} Teile verfuegbar · " +
            $"Letzter Sync: {lastSync:dd.MM.yyyy HH:mm}";
    }
}

/// <summary>Label/Value-Paar fuer die Filter-Comboboxen.</summary>
public sealed record BlInventoryFilterOption(string Label, string Value);

/// <summary>
/// Eine Zeile in der BL-Inventar-Tabelle. ObservableObject damit die
/// lazy nachgeladenen Thumbnails das DataGrid-Bild aktualisieren koennen.
/// Restliche Felder sind init-only - werden in <see cref="From"/> einmalig
/// gesetzt und aendern sich danach nicht.
/// </summary>
public partial class BlInventoryRow : ObservableObject
{
    public int LotId { get; init; }
    public string ItemNo { get; init; } = string.Empty;

    /// <summary>BL-Kurzform ("P"/"M"/"S") - benutzt vom Typ-Filter.</summary>
    public string ItemType { get; init; } = string.Empty;
    public string ItemTypeLabel { get; init; } = string.Empty;

    public int? ColorId { get; init; }

    /// <summary>Color-Name aus bl_colors-Cache (Fallback: aus dem Sync-Snapshot).</summary>
    public string ColorName { get; init; } = string.Empty;

    /// <summary>Vom BL-Catalog gecachter Item-Name (z.B. "Plate 1 x 1"). Optional.</summary>
    public string? CatalogName { get; init; }

    /// <summary>BL-Listing-Description aus dem Lot. Fallback wenn kein Catalog-Name.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Display-Text fuer die Beschreibungs-Spalte: Catalog-Name wenn vorhanden, sonst Description.</summary>
    public string DescriptionDisplay => !string.IsNullOrWhiteSpace(CatalogName)
        ? CatalogName!
        : Description;

    public int Quantity { get; init; }
    public int ReservedQuantity { get; init; }
    public int Available => Math.Max(0, Quantity - ReservedQuantity);
    public string ReservedDisplay => ReservedQuantity > 0 ? ReservedQuantity.ToString() : string.Empty;

    public decimal UnitPrice { get; init; }
    public string UnitPriceDisplay { get; init; } = string.Empty;

    public string Condition { get; init; } = string.Empty;
    public string ConditionLabel { get; init; } = string.Empty;

    /// <summary>Zeitpunkt des letzten Sync (Lokalzeit-Anzeige im Detail-Panel).</summary>
    public DateTime LastSyncedAt { get; init; }
    public string LastSyncedAtDisplay => LastSyncedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm");

    /// <summary>Pfad zum lokalen Thumbnail. Wird lazy beim Sichtbar-Werden der Zeile gefuellt.</summary>
    [ObservableProperty]
    private string? _imageUrl;

    // 0 = noch nicht geclaimt, 1 = Load wurde getriggert (egal ob fertig).
    private int _imageLoadStarted;

    /// <summary>
    /// Atomarer Claim eines Image-Load-Slots. Liefert true beim ersten Aufruf
    /// (Aufrufer darf laden), false bei jedem weiteren (Doppel-Trigger durch
    /// Row-Recycling im virtualisierten DataGrid + Selection-Hook werden
    /// damit harmlos).
    /// </summary>
    public bool TryClaimImageLoadSlot()
        => System.Threading.Interlocked.CompareExchange(ref _imageLoadStarted, 1, 0) == 0;

    public static BlInventoryRow From(BlInventoryLot lot, BlItemSummary? summary, string? cacheColorName)
    {
        // Color-Name-Priority: bl_colors-Cache > BL-Sync-Snapshot > "".
        var colorName = !string.IsNullOrWhiteSpace(cacheColorName)
            ? cacheColorName!
            : (lot.ColorName ?? string.Empty);

        return new BlInventoryRow
        {
            LotId = lot.LotId,
            ItemNo = lot.ItemNo,
            ItemType = lot.ItemType,
            ItemTypeLabel = LabelForItemType(lot.ItemType),
            ColorId = lot.ColorId,
            ColorName = colorName,
            CatalogName = summary?.Name,
            Description = lot.Description ?? string.Empty,
            Quantity = lot.Quantity,
            ReservedQuantity = lot.ReservedQuantity,
            UnitPrice = lot.UnitPrice,
            UnitPriceDisplay = lot.UnitPrice.ToString("F4", CultureInfo.GetCultureInfo("de-DE")) + " EUR",
            Condition = lot.Condition,
            ConditionLabel = LabelForCondition(lot.Condition),
            LastSyncedAt = lot.LastSyncedAt
        };
    }

    private static string LabelForItemType(string t) => t switch
    {
        "P" => "Part",
        "M" => "Minifig",
        "S" => "Set",
        "B" => "Book",
        "G" => "Gear",
        "I" => "Instruction",
        "O" => "Original Box",
        _   => t
    };

    private static string LabelForCondition(string c) => c switch
    {
        "N" => "Neu",
        "U" => "Gebraucht",
        _   => c
    };
}
