using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HBSort.Core.Services;
using Serilog;

namespace HBSort.ViewModels;

/// <summary>
/// UX X.34 v0.1.20-beta.2 / beta.4: ViewModel fuer den First-Run-Onboarding-
/// Dialog.
///
/// Ein interaktiver Pflicht-Schritt:
/// 1. BL-Catalog laden (per BulkImportService) - PFLICHT damit HBSort
///    ueberhaupt Stammdaten zu gescannten Items hat.
///
/// Plus reine Info-Hinweise (beta.4):
/// 2. Lagerfaecher anlegen - nur Text-Hinweis im Dialog. User legt sie nach
///    "Loslegen" in den Einstellungen an. Vorher (beta.2/.3) gab es einen
///    "Lagerfach-Verwaltung oeffnen"-Button der einen Modal-on-Modal-Stack
///    erzeugt hat - User-Feedback: weglassen, der Dialog wird klarer.
/// 3. BL-Tokens - optional, reiner Hinweis.
///
/// Der "Loslegen"-Button ist disabled solange kein Catalog existiert.
/// </summary>
public partial class FirstRunDialogViewModel : ObservableObject
{
    private readonly IBlBulkImportService _bulkImport;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CatalogStatusIcon))]
    [NotifyPropertyChangedFor(nameof(CatalogButtonEnabled))]
    [NotifyPropertyChangedFor(nameof(StartButtonEnabled))]
    private bool _hasCatalog;

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
        // firstRun-Param bleibt im Ctor fuer API-Kompatibilitaet falls
        // der Bin-Refresh-Pfad in einer spaeteren Iteration wiederkommt.
        // Initial-Status reicht aktuell aus.
        _ = firstRun;
        HasCatalog = initialStatus is FirstRunStatus.Complete or FirstRunStatus.NeedsBins;
    }

    /// <summary>Visueller Marker fuer Schritt 1: gruener Haken oder Warn-Symbol.</summary>
    public string CatalogStatusIcon => HasCatalog ? "✓" : "!";

    /// <summary>Catalog-Button nur klickbar wenn noch kein Catalog UND kein laufender Import.</summary>
    public bool CatalogButtonEnabled => !HasCatalog && !CatalogLoading;

    /// <summary>
    /// "Loslegen"-Button: nur aktiv wenn Catalog vorhanden. Lagerfaecher sind
    /// bewusst nicht Voraussetzung - User legt sie spaeter in den Einstellungen
    /// an (Welcome-Dialog kein blockierender Wizard).
    /// </summary>
    public bool StartButtonEnabled => HasCatalog;

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
