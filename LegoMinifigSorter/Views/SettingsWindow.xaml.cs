using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using LegoMinifigSorter.Core.Services;
using LegoMinifigSorter.Services;
using LegoMinifigSorter.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace LegoMinifigSorter.Views;

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
}
