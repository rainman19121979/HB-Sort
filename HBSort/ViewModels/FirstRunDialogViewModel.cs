using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HBSort.Core.Services;
using Serilog;

namespace HBSort.ViewModels;

/// <summary>
/// UX X.34 v0.1.20-beta.2: ViewModel fuer den First-Run-Onboarding-Dialog.
///
/// Zwei Pflicht-Schritte:
/// 1. BL-Catalog laden (per BulkImportService) - PFLICHT damit HBSort
///    ueberhaupt Stammdaten zu gescannten Items hat.
/// 2. Lagerfaecher anlegen - User klickt "Lagerfach-Verwaltung" -> Settings
///    oeffnet, User legt manuell Bins an, schliesst Settings, kommt zurueck
///    in den Dialog. Nach erfolgreichem Anlegen schaltet das StatusIcon auf
///    gruen.
///
/// Der "Loslegen"-Button ist disabled solange kein Catalog existiert
/// (Lagerfaecher sind nice-to-have aber nicht zwingend - User kann auch
/// in einer laufenden Session ein erstes Bin anlegen).
/// </summary>
public partial class FirstRunDialogViewModel : ObservableObject
{
    private readonly IBlBulkImportService _bulkImport;
    private readonly IFirstRunService _firstRun;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CatalogStatusIcon))]
    [NotifyPropertyChangedFor(nameof(CatalogButtonEnabled))]
    [NotifyPropertyChangedFor(nameof(StartButtonEnabled))]
    private bool _hasCatalog;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BinsStatusIcon))]
    private bool _hasBins;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CatalogButtonEnabled))]
    private bool _catalogLoading;

    [ObservableProperty] private double _catalogProgress;
    [ObservableProperty] private string _catalogStatusText = string.Empty;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private bool _hasError;

    public FirstRunDialogViewModel(
        IBlBulkImportService bulkImport,
        IFirstRunService firstRun,
        FirstRunStatus initialStatus)
    {
        _bulkImport = bulkImport;
        _firstRun = firstRun;
        ApplyStatus(initialStatus);
    }

    /// <summary>Visueller Marker pro Schritt: gruener Haken oder Warn-Symbol.</summary>
    public string CatalogStatusIcon => HasCatalog ? "✓" : "!";
    public string BinsStatusIcon    => HasBins    ? "✓" : "!";

    /// <summary>Catalog-Button nur klickbar wenn noch kein Catalog UND kein laufender Import.</summary>
    public bool CatalogButtonEnabled => !HasCatalog && !CatalogLoading;

    /// <summary>
    /// "Loslegen"-Button: nur aktiv wenn Catalog vorhanden. Bins sind bewusst
    /// optional - User kann sie spaeter im Settings-Tab "Lagerfaecher" anlegen.
    /// </summary>
    public bool StartButtonEnabled => HasCatalog;

    /// <summary>
    /// Wird vom Code-Behind nach Settings-Dialog-Schliessen aufgerufen,
    /// damit das Bin-Status-Icon aktualisiert wird.
    /// </summary>
    public async Task RefreshStatusAsync()
    {
        var status = await _firstRun.CheckStatusAsync();
        ApplyStatus(status);
    }

    private void ApplyStatus(FirstRunStatus status)
    {
        HasCatalog = status is FirstRunStatus.Complete or FirstRunStatus.NeedsBins;
        HasBins    = status is FirstRunStatus.Complete or FirstRunStatus.NeedsCatalog;
    }

    [RelayCommand]
    private async Task LoadCatalogAsync()
    {
        CatalogLoading = true;
        HasError = false;
        ErrorMessage = string.Empty;
        CatalogStatusText = "Lade BrickStore-Datenbank...";
        CatalogProgress = 0;

        var progress = new Progress<BlBulkImportProgress>(p =>
        {
            CatalogStatusText = $"{p.Phase}: {p.CurrentItem}";
            CatalogProgress = p.Total > 0
                ? Math.Min(100, (double)p.Current / p.Total * 100)
                : 0;
        });

        try
        {
            // Heavy-Operation auf Threadpool damit der Dialog responsiv bleibt
            // (analog zum Settings-Auto-Import-Pfad).
            var result = await Task.Run(() => _bulkImport.ImportFromGitHubAsync(
                previousEtag: null, previousContentHash: null,
                progress: progress, ct: default));

            CatalogProgress = 100;
            CatalogStatusText = result.Skipped
                ? "BrickStore-Datenbank ist bereits aktuell."
                : $"Erfolgreich: {result.ItemsImported:N0} Items in {result.Duration.TotalSeconds:F0}s.";
            HasCatalog = true;

            Log.Information("First-Run: BL-Catalog-Import erfolgreich ({Items} Items, skipped={Skipped})",
                result.ItemsImported, result.Skipped);
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = $"Import fehlgeschlagen: {ex.Message}. Du kannst spaeter in den Einstellungen unter BrickLink > Catalog-Daten erneut importieren.";
            Log.Warning(ex, "First-Run: BL-Catalog-Import fehlgeschlagen");
        }
        finally
        {
            CatalogLoading = false;
        }
    }
}
