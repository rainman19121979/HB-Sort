using System.IO;
using System.Windows;
using HBSort.Core.Services;
using HBSort.Services;
using Serilog;

namespace HBSort.Views;

/// <summary>
/// Code-Behind fuer den Wanted-List-Export-Dialog. Pure UI-Logik:
/// Modus + Ordner waehlen, dann den IWantedListExportService aufrufen
/// und die XML-Datei(en) schreiben.
/// </summary>
public partial class WantedListExportDialog : Window
{
    private readonly IWantedListExportService _exportService;
    private readonly ISettingsService _settings;
    private readonly INotificationService _notifications;

    public WantedListExportDialog(
        IWantedListExportService exportService,
        ISettingsService settings,
        INotificationService notifications)
    {
        InitializeComponent();
        _exportService = exportService;
        _settings = settings;
        _notifications = notifications;

        // Default-Ordner: erst WantedListExportFolder, fallback BsxExportFolder,
        // sonst Documents/HBSort-Export. Bewusst weicher Fallback damit der User
        // nichts setzen muss um zu starten.
        var defaultFolder =
            _settings.Current.WantedListExportFolder
            ?? _settings.Current.BsxExportFolder
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "HBSort-Export");
        FolderBox.Text = defaultFolder;

        SummaryText.Text = "Exportiert die fehlenden Teile aller wartenden Figuren " +
                           "im BrickLink-Wanted-List-XML-Format.";
        StatusText.Text = "Bereit. Bitte Modus und Ordner pruefen, dann auf 'Exportieren' klicken.";
    }

    /// <summary>Ordner-Picker, wie im BSX-Dialog auch.</summary>
    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Wanted-List-Ordner waehlen"
        };
        if (!string.IsNullOrWhiteSpace(FolderBox.Text)
            && Directory.Exists(FolderBox.Text))
        {
            dlg.InitialDirectory = FolderBox.Text;
        }
        if (dlg.ShowDialog(this) == true)
        {
            FolderBox.Text = dlg.FolderName;
        }
    }

    /// <summary>
    /// Export-Button. Holt die XML(s) vom Service, schreibt sie ins Dateisystem
    /// und persistiert den gewaehlten Ordner als neuen Default.
    /// </summary>
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        ExportButton.IsEnabled = false;
        try
        {
            var folder = FolderBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(folder))
            {
                StatusText.Text = "Bitte einen Speicher-Ordner waehlen.";
                return;
            }

            // Ordner anlegen falls noch nicht vorhanden - der Picker laesst
            // theoretisch auch nicht-existente Pfade zu (Tipp-Eingabe etc.).
            Directory.CreateDirectory(folder);

            int writtenCount;
            string firstPath;

            if (ModePerMinifig.IsChecked == true)
            {
                var files = await _exportService.GeneratePerMinifigAsync();
                if (files.Count == 0)
                {
                    StatusText.Text = "Keine wartenden Figuren mit fehlenden Teilen gefunden.";
                    return;
                }

                foreach (var f in files)
                {
                    var path = Path.Combine(folder, f.FileName);
                    await File.WriteAllTextAsync(path, f.Xml);
                }
                writtenCount = files.Count;
                firstPath = Path.Combine(folder, files[0].FileName);
            }
            else
            {
                // Combined-Mode (Default).
                var xml = await _exportService.GenerateCombinedAsync();
                var fileName = $"HBSort-Wanted-{DateTime.Now:yyyy-MM-dd-HHmm}.xml";
                var path = Path.Combine(folder, fileName);
                await File.WriteAllTextAsync(path, xml);
                writtenCount = 1;
                firstPath = path;
            }

            // Persist gewaehlten Ordner als neuen Default.
            _settings.Current.WantedListExportFolder = folder;
            await _settings.SaveAsync();

            _notifications.ShowSuccess(writtenCount == 1
                ? $"Wanted-List exportiert: {Path.GetFileName(firstPath)}"
                : $"{writtenCount} Wanted-List-Dateien exportiert");
            Log.Information("Wanted-List-Export: {Count} Datei(en) nach {Folder}",
                writtenCount, folder);

            DialogResult = true;
            Close();
        }
        catch (InvalidOperationException ex)
        {
            // Erwarteter Fehler (keine fehlenden Teile etc.) - freundlich anzeigen.
            StatusText.Text = ex.Message;
            ExportButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Wanted-List-Export fehlgeschlagen");
            StatusText.Text = $"Fehler: {ex.Message}";
            ExportButton.IsEnabled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
