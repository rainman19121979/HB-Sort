using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using HBSort.Core.Services;
using HBSort.Services;
using HBSort.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace HBSort.Views;

/// <summary>
/// Code-Behind für das Einstellungen-Fenster.
/// Minimal gehalten: nur die Button-Clicks, die Logik steckt im ViewModel.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;

        // Versionstext im Info-Tab aus der Assembly-Version setzen.
        // SettingsViewModel kennt keine VersionText-Property, daher direkt im Code-Behind.
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        if (version != null)
        {
            AboutVersionText.Text = $"v{version.Major}.{version.Minor}.{version.Build}";
        }

        // Phase 6: Statistik-Daten initial laden (best-effort, blockt nicht).
        Loaded += async (_, _) => await _viewModel.LoadStatsAsync();
    }

    /// <summary>Speichern-Button: Settings übernehmen und Fenster schließen</summary>
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.SaveSettingsAsync();
        DialogResult = true; // Signalisiert dem Hauptfenster: "Änderungen übernommen"
        Close();
    }

    /// <summary>Abbrechen-Button: Fenster schließen ohne zu speichern</summary>
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    // Cache-Limit Radio-Buttons. Wir koennten die direkt an die Property binden,
    // aber RadioButton.IsChecked ist nur One-Way an IsLimitXyz – Click-Handler ist klarer.
    private void LimitRadio100_Click(object sender, RoutedEventArgs e)
        => _viewModel.SetCacheLimitCommand.Execute("100");

    private void LimitRadio1Gb_Click(object sender, RoutedEventArgs e)
        => _viewModel.SetCacheLimitCommand.Execute("1024");

    private void LimitRadio5Gb_Click(object sender, RoutedEventArgs e)
        => _viewModel.SetCacheLimitCommand.Execute("5120");

    private void LimitRadioUnlimited_Click(object sender, RoutedEventArgs e)
        => _viewModel.SetCacheLimitCommand.Execute("0");

    /// <summary>Oeffnet einen Hyperlink im Standard-Browser.</summary>
    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    // UI-Density Radio-Buttons – Click-Handler statt Two-Way-Binding,
    // damit wir die Apply-Logik (inkl. Persistierung) sofort triggern.
    private void DensityCompact_Click(object sender, RoutedEventArgs e)
        => _viewModel.ApplyUiDensityCommand.Execute("Compact");

    private void DensityNormal_Click(object sender, RoutedEventArgs e)
        => _viewModel.ApplyUiDensityCommand.Execute("Normal");

    private void DensityComfortable_Click(object sender, RoutedEventArgs e)
        => _viewModel.ApplyUiDensityCommand.Execute("Comfortable");

    // ====================================================================
    // Tab "Lagerfaecher" (Phase 4)
    // ====================================================================

    /// <summary>Filter-Radio: Alle.</summary>
    private void FilterAll_Click(object sender, RoutedEventArgs e)
        => _viewModel.BinManager.SetFilter(BinFilterMode.All);

    private void FilterFree_Click(object sender, RoutedEventArgs e)
        => _viewModel.BinManager.SetFilter(BinFilterMode.FreeOnly);

    private void FilterOccupied_Click(object sender, RoutedEventArgs e)
        => _viewModel.BinManager.SetFilter(BinFilterMode.OccupiedOnly);

    /// <summary>Oeffnet den Single-Create-Dialog. Bei Erfolg wird die Liste neu geladen.</summary>
    private async void BinCreateSingle_Click(object sender, RoutedEventArgs e)
    {
        var dialog = App.Services.GetRequiredService<BinCreateDialog>();
        dialog.Owner = this;
        if (dialog.ShowDialog() == true)
        {
            await _viewModel.BinManager.ReloadAsync();
        }
    }

    /// <summary>Oeffnet den Bulk-Create-Dialog. Bei Erfolg wird die Liste neu geladen.</summary>
    private async void BinCreateBulk_Click(object sender, RoutedEventArgs e)
    {
        var dialog = App.Services.GetRequiredService<BinBulkCreateDialog>();
        dialog.Owner = this;
        if (dialog.ShowDialog() == true)
        {
            await _viewModel.BinManager.ReloadAsync();
        }
    }

    /// <summary>Per-Row "Inhalt" – oeffnet BinDetailDialog mit Figuren + Floating-Parts.</summary>
    private void BinShowDetail_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not BinRowViewModel row) return;

        var binService = App.Services.GetRequiredService<IStorageBinService>();
        var catalog = App.Services.GetRequiredService<IBlCatalogService>();
        var imgProvider = App.Services.GetRequiredService<IPartImageProvider>();

        var vm = new BinDetailViewModel(row.Id, binService, catalog, imgProvider);
        var dialog = new Views.BinDetailDialog(vm) { Owner = this };
        dialog.ShowDialog();
    }

    /// <summary>Per-Row Umbenennen. Zeigt einen InputBox-aehnlichen Dialog.</summary>
    private async void BinRename_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not BinRowViewModel row) return;

        var newLabel = SimpleInputBox.Show(
            this, "Lagerfach umbenennen",
            $"Neuer Name fuer '{row.Label}':",
            row.Label);

        if (string.IsNullOrWhiteSpace(newLabel) || newLabel.Trim() == row.Label) return;

        var binService = App.Services.GetRequiredService<IStorageBinService>();
        var notif = App.Services.GetRequiredService<INotificationService>();
        try
        {
            var ok = await binService.RenameAsync(row.Id, newLabel.Trim());
            if (ok)
            {
                notif.ShowSuccess($"Umbenannt: '{row.Label}' → '{newLabel.Trim()}'");
                await _viewModel.BinManager.ReloadAsync();
            }
            else
            {
                notif.ShowError("Umbenennen fehlgeschlagen.");
            }
        }
        catch (System.Exception ex)
        {
            Log.Warning(ex, "Bin-Rename fehlgeschlagen");
            MessageBox.Show(ex.Message, "Umbenennen", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Per-Row Leeren. Zeigt Bestaetigungs-Dialog mit Inhalts-Info.</summary>
    private async void BinEmpty_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not BinRowViewModel row) return;

        if (row.IsFree)
        {
            MessageBox.Show("Das Fach ist bereits frei.", "Leeren",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Warnung: was passiert beim Leeren
        var msg = $"Fach '{row.Label}' enthaelt {row.MinifigCount} Figur(en) und " +
                  $"{row.FloatingPartCount} Einzelteil(e).\n\n" +
                  "Beim Leeren werden:\n" +
                  "  - Figuren vom Fach geloest (bleiben in der DB)\n" +
                  "  - Einzelteile geloescht\n\n" +
                  "Fortfahren?";
        var result = MessageBox.Show(msg, "Lagerfach leeren",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        var binService = App.Services.GetRequiredService<IStorageBinService>();
        var notif = App.Services.GetRequiredService<INotificationService>();
        try
        {
            var ok = await binService.EmptyAsync(row.Id);
            if (ok)
            {
                notif.ShowSuccess($"Fach '{row.Label}' geleert.");
                await _viewModel.BinManager.ReloadAsync();
            }
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "Bin-Empty fehlgeschlagen");
            MessageBox.Show(ex.Message, "Leeren", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Per-Row Loeschen. Schlaegt fehl wenn Fach belegt ist (Service prueft).</summary>
    private async void BinDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not BinRowViewModel row) return;

        if (!row.IsFree)
        {
            MessageBox.Show(
                $"Fach '{row.Label}' enthaelt noch Figuren oder Teile.\n" +
                "Bitte erst leeren.",
                "Loeschen nicht moeglich",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var msg = $"Fach '{row.Label}' wirklich endgueltig loeschen?";
        var result = MessageBox.Show(msg, "Lagerfach loeschen",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        var binService = App.Services.GetRequiredService<IStorageBinService>();
        var notif = App.Services.GetRequiredService<INotificationService>();
        try
        {
            var ok = await binService.DeleteAsync(row.Id);
            if (ok)
            {
                notif.ShowSuccess($"Fach '{row.Label}' geloescht.");
                await _viewModel.BinManager.ReloadAsync();
            }
        }
        catch (System.Exception ex)
        {
            Log.Warning(ex, "Bin-Delete fehlgeschlagen");
            MessageBox.Show(ex.Message, "Loeschen", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ====================================================================
    // Tab "BL-Catalog-Daten" (Phase 5.5: BrickStore-Bulk-Import)
    // ====================================================================

    /// <summary>"Von GitHub importieren": laedt downloads.zip + entpackt + importiert.</summary>
    private async void ImportFromGitHub_Click(object sender, RoutedEventArgs e)
    {
        var importer = App.Services.GetRequiredService<IBlBulkImportService>();
        var notif = App.Services.GetRequiredService<INotificationService>();
        await RunImportAsync(notif, importer.ImportFromGitHubAsync);
    }

    /// <summary>"Aus lokalem Ordner importieren": parst items/M.xml + M/*.xml direkt.</summary>
    private async void ImportFromFolder_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_viewModel.LocalImportFolder)
            || !System.IO.Directory.Exists(_viewModel.LocalImportFolder))
        {
            _viewModel.ImportResultText = "Bitte gueltigen Ordner waehlen.";
            return;
        }
        var folder = _viewModel.LocalImportFolder;
        var importer = App.Services.GetRequiredService<IBlBulkImportService>();
        var notif = App.Services.GetRequiredService<INotificationService>();
        await RunImportAsync(notif,
            (progress, ct) => importer.ImportFromFolderAsync(folder, progress, ct));
    }

    /// <summary>Ordner-Picker via Microsoft.Win32.OpenFolderDialog (.NET 8).</summary>
    private void BrowseImportFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "BrickStore-Daten-Ordner waehlen"
        };
        if (dlg.ShowDialog(this) == true)
        {
            _viewModel.LocalImportFolder = dlg.FolderName;
        }
    }

    /// <summary>Gemeinsamer Wrapper: Progress, Toast, Stats-Refresh, Fehler-Handling.</summary>
    private async Task RunImportAsync(
        INotificationService notif,
        Func<IProgress<Core.Services.BlBulkImportProgress>?, System.Threading.CancellationToken,
            Task<Core.Services.BlBulkImportResult>> importer)
    {
        _viewModel.IsImporting = true;
        _viewModel.ImportResultText = string.Empty;
        _viewModel.ImportProgress = 0;
        _viewModel.ImportStatus = "Starte Import...";

        var progress = new Progress<Core.Services.BlBulkImportProgress>(p =>
        {
            _viewModel.ImportStatus = $"{p.Phase}: {p.CurrentItem}";
            _viewModel.ImportProgress = p.Total > 0
                ? Math.Min(100, (double)p.Current / p.Total * 100)
                : 0;
        });

        try
        {
            // Schwere Operation auf den Threadpool – UI-Thread bleibt responsiv,
            // Progress-Reports kommen dank IProgress<T> auf dem UI-Thread an.
            var result = await Task.Run(() => importer(progress, default));
            _viewModel.ImportResultText =
                $"Import erfolgreich: {result.ItemsImported:N0} Items, " +
                $"{result.InventoriesImported:N0} Subsets in {result.Duration.TotalSeconds:F1}s." +
                (result.Errors.Count > 0 ? $" ({result.Errors.Count} Fehler)" : string.Empty);
            await _viewModel.RefreshBrickStoreStatsAsync();
            await _viewModel.RefreshBlCacheStatsAsync();
            notif.ShowSuccess("BrickStore-Import abgeschlossen.");
        }
        catch (System.Exception ex)
        {
            _viewModel.ImportResultText = $"Fehler: {ex.Message}";
            notif.ShowError($"Import fehlgeschlagen: {ex.Message}");
            Log.Error(ex, "BrickStore-Import fehlgeschlagen");
        }
        finally
        {
            _viewModel.IsImporting = false;
        }
    }
}
