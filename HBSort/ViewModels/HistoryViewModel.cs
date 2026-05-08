using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HBSort.Core.Models;
using HBSort.Core.Services;
using HBSort.Services;
using Serilog;

namespace HBSort.ViewModels;

/// <summary>
/// UX X.29 (v0.1.16): ViewModel fuer den Verlauf-Tab.
/// Zeigt die letzten ScanEvents mit Filter (Datum, Typ, Suche) und
/// einem "Rueckgaengig"-Button pro undo-faehigem Eintrag.
/// </summary>
public partial class HistoryViewModel : ObservableObject
{
    private readonly IUndoService _undoService;
    private readonly INotificationService _notifications;

    public ObservableCollection<HistoryRowViewModel> Items { get; } = new();
    public ICollectionView ItemsView { get; }

    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Filter nach Aktions-Typ. "Alle" = kein Filter.</summary>
    [ObservableProperty]
    private string _selectedTypeFilter = "Alle";

    public List<string> AvailableTypeFilters { get; } = new()
    {
        "Alle",
        "Geloescht",
        "Verschoben",
        "Komplettiert",
        "Lagerfach freigegeben",
        "Lagerfach angelegt",
        "Scan",
        "Sonstiges"
    };

    [ObservableProperty]
    private DateTime? _filterFromDate;

    [ObservableProperty]
    private DateTime? _filterToDate;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusText = string.Empty;

    public HistoryViewModel(IUndoService undoService, INotificationService notifications)
    {
        _undoService = undoService;
        _notifications = notifications;

        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = RowFilter;
    }

    partial void OnSearchTextChanged(string value) => ItemsView.Refresh();
    partial void OnSelectedTypeFilterChanged(string value) => ItemsView.Refresh();
    partial void OnFilterFromDateChanged(DateTime? value) => ItemsView.Refresh();
    partial void OnFilterToDateChanged(DateTime? value) => ItemsView.Refresh();

    private bool RowFilter(object obj)
    {
        if (obj is not HistoryRowViewModel r) return false;

        if (FilterFromDate.HasValue && r.Timestamp < FilterFromDate.Value) return false;
        if (FilterToDate.HasValue && r.Timestamp > FilterToDate.Value.AddDays(1)) return false;

        if (SelectedTypeFilter != "Alle"
            && !string.Equals(MapType(r.Type), SelectedTypeFilter, StringComparison.Ordinal))
            return false;

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var s = SearchText.Trim();
            if (!r.Description.Contains(s, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private static string MapType(ScanType t) => t switch
    {
        ScanType.Delete => "Geloescht",
        ScanType.Move => "Verschoben",
        ScanType.Complete => "Komplettiert",
        ScanType.BinFreed => "Lagerfach freigegeben",
        ScanType.BinCreated => "Lagerfach angelegt",
        ScanType.MinifigScan or ScanType.PartScan => "Scan",
        _ => "Sonstiges"
    };

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var entries = await _undoService.GetHistoryAsync(limit: 200);
            Items.Clear();
            foreach (var e in entries)
                Items.Add(new HistoryRowViewModel(e));
            StatusText = $"{entries.Count} Eintraege geladen";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "History RefreshAsync fehlgeschlagen");
            StatusText = "Fehler beim Laden des Verlaufs.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public void ClearFilters()
    {
        SearchText = string.Empty;
        SelectedTypeFilter = "Alle";
        FilterFromDate = null;
        FilterToDate = null;
    }

    /// <summary>Wird vom Code-Behind aufgerufen wenn der Rueckgaengig-Button klickt.</summary>
    public async Task<bool> UndoRowAsync(HistoryRowViewModel row)
    {
        var result = await _undoService.UndoAsync(row.Id);
        if (result.Success)
        {
            _notifications.ShowSuccess($"Rueckgaengig: {row.Description}");
            await RefreshAsync();
            return true;
        }
        _notifications.ShowError(
            "Rueckgaengig fehlgeschlagen: " + (result.ErrorMessage ?? "unbekannter Fehler"));
        return false;
    }
}

/// <summary>UX X.29 (v0.1.16): eine Zeile in der Verlauf-DataGrid.</summary>
public class HistoryRowViewModel
{
    public int Id { get; }
    public DateTime Timestamp { get; }
    public ScanType Type { get; }
    public string TypeDisplay { get; }
    public string Description { get; }
    public bool CanUndo { get; }
    public bool WasUndone { get; }
    public string TimestampDisplay { get; }
    public string StatusDisplay { get; }

    public HistoryRowViewModel(HistoryEntry e)
    {
        Id = e.Id;
        Timestamp = e.Timestamp;
        Type = e.Type;
        Description = e.Description;
        CanUndo = e.CanUndo;
        WasUndone = e.WasUndone;
        TimestampDisplay = e.Timestamp.ToLocalTime()
            .ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture);
        TypeDisplay = MapTypeDisplay(e.Type);
        StatusDisplay = e.WasUndone
            ? $"Rueckgaengig {(e.UndoneAt?.ToLocalTime().ToString("dd.MM. HH:mm", CultureInfo.InvariantCulture) ?? "")}"
            : (CanUndo ? "Rueckgaengigmachbar" : string.Empty);
    }

    private static string MapTypeDisplay(ScanType t) => t switch
    {
        ScanType.Delete => "Geloescht",
        ScanType.Move => "Verschoben",
        ScanType.Complete => "Komplettiert",
        ScanType.BinFreed => "Fach freigegeben",
        ScanType.BinCreated => "Fach angelegt",
        ScanType.MinifigScan => "Figur-Scan",
        ScanType.PartScan => "Teil-Scan",
        ScanType.FloatingPartTransfer => "Teil aus Fach uebernommen",
        ScanType.FloatingPartExported => "Teil exportiert",
        ScanType.UndoApplied => "Rueckgaengig",
        ScanType.Import => "BL-Import",
        _ => t.ToString()
    };
}
