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

        SummaryText.Text = "Exportiert die fehlenden Teile deiner wartenden Figuren als " +
                           "BrickLink-Wanted-List (.xml). Eine Zeile pro Teil-Sorte (PartNo + " +
                           "Farbe). Direkt auf der BrickLink-Webseite hochladbar oder per " +
                           "Zwischenablage einfuegbar.";
        StatusText.Text = "Bereit. Du kannst die Wanted-List entweder als XML-Datei speichern " +
                          "oder den XML-Code direkt in die Zwischenablage kopieren um ihn auf " +
                          "der BrickLink-Webseite einzufuegen.";
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

                // UX X.32 v0.1.19-beta.6: BL-Wanted-XML, Endung .xml,
                // UTF-8 ohne BOM. WriteAllBytesAsync statt WriteAllTextAsync
                // weil File.WriteAllTextAsync system-default Encoding nutzt.
                var noBomEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                foreach (var f in files)
                {
                    var path = Path.Combine(folder, f.FileName);
                    await File.WriteAllBytesAsync(path, noBomEncoding.GetBytes(f.Xml));
                }
                writtenCount = files.Count;
                firstPath = Path.Combine(folder, files[0].FileName);
            }
            else
            {
                // Combined-Mode (Default).
                // UX X.32 v0.1.19-beta.6: BrickLink-Wanted-XML, Endung .xml.
                // BL-Web akzeptiert kein BSX. UTF-8 ohne BOM.
                var xml = await _exportService.GenerateCombinedAsync();
                var fileName = $"HBSort-Wanted-Fehlteile-{DateTime.Now:yyyy-MM-dd-HHmm}.xml";
                var path = Path.Combine(folder, fileName);
                var bytes = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                    .GetBytes(xml);
                await File.WriteAllBytesAsync(path, bytes);
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

    /// <summary>
    /// UX X.32 v0.1.19-beta.6: kopiert die Combined-Wanted-List als XML
    /// in die Windows-Zwischenablage. User kann sie dann auf
    /// https://www.bricklink.com/v2/wanted/upload.page im "Paste XML"-
    /// Tab direkt einfuegen - kein File-Upload noetig.
    ///
    /// Funktioniert bewusst nur fuer den Combined-Modus, weil mehrere
    /// XMLs in der Zwischenablage keinen Sinn ergeben.
    /// </summary>
    private async void CopyToClipboard_Click(object sender, RoutedEventArgs e)
    {
        ClipboardButton.IsEnabled = false;
        try
        {
            StatusText.Text = "XML wird generiert...";
            var xml = await _exportService.GenerateCombinedAsync();

            // Clipboard.SetText muss auf dem UI-Thread laufen.
            Clipboard.SetText(xml);

            StatusText.Text = "XML wurde in die Zwischenablage kopiert. Du kannst es jetzt " +
                              "im BrickLink-Web-Wanted-Upload (Paste XML-Tab) einfuegen.";
            _notifications.ShowSuccess("Wanted-List-XML in Zwischenablage kopiert");
            Log.Information("Wanted-List in Zwischenablage kopiert ({Length} Zeichen)", xml.Length);
        }
        catch (InvalidOperationException ex)
        {
            // Service wirft InvalidOperationException wenn keine fehlenden
            // Teile - freundlich anzeigen, kein Stacktrace.
            StatusText.Text = ex.Message;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Wanted-List Zwischenablage-Kopie fehlgeschlagen");
            StatusText.Text = $"Fehler beim Kopieren: {ex.Message}";
        }
        finally
        {
            ClipboardButton.IsEnabled = true;
        }
    }
}
