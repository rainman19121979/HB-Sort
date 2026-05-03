using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using HBSort.Core.Database;
using HBSort.Core.Services;
using HBSort.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;
using Serilog;

namespace HBSort.Views;

/// <summary>
/// Phase 7: BSX-Export-Dialog. Wird ueber DI als Transient instanziiert
/// und vom Caller per <see cref="Initialize"/> mit der Figur-ID-Liste gefuettert.
/// </summary>
public partial class BsxExportDialog : Window
{
    private readonly IBsxExportService _exporter;
    private readonly IMinifigPersistenceService _persistence;
    private readonly IStorageBinService _binService;
    private readonly INotificationService _notifications;
    private readonly ISettingsService _settings;
    private readonly IDbContextFactory<UserDataContext> _ctxFactory;

    private List<int> _minifigIds = new();
    private string? _writtenPath;
    private List<int> _emptyBinIdsAfterExport = new();

    public BsxExportDialog(
        IBsxExportService exporter,
        IMinifigPersistenceService persistence,
        IStorageBinService binService,
        INotificationService notifications,
        ISettingsService settings,
        IDbContextFactory<UserDataContext> ctxFactory)
    {
        InitializeComponent();
        _exporter = exporter;
        _persistence = persistence;
        _binService = binService;
        _notifications = notifications;
        _settings = settings;
        _ctxFactory = ctxFactory;
    }

    /// <summary>
    /// Wird vom Aufrufer mit den ausgewaehlten TrackedMinifig-IDs initialisiert.
    /// Laed die zugehoerigen Daten + setzt Default-Speicherpfad.
    /// </summary>
    public void Initialize(IReadOnlyList<int> minifigIds)
    {
        _minifigIds = minifigIds.ToList();
        Loaded += async (_, _) =>
        {
            await LoadMinifigsAsync();
            PathBox.Text = BuildDefaultExportPath();
        };
    }

    private async Task LoadMinifigsAsync()
    {
        try
        {
            await using var ctx = await _ctxFactory.CreateDbContextAsync();
            var rows = await ctx.TrackedMinifigs.AsNoTracking()
                .Where(m => _minifigIds.Contains(m.Id))
                .Include(m => m.StorageBin)
                .ToListAsync();

            // Reihenfolge wie in der ID-Liste
            var lookup = rows.ToDictionary(m => m.Id);
            var ordered = _minifigIds.Where(lookup.ContainsKey).Select(i => lookup[i]).ToList();

            ItemList.ItemsSource = ordered.Select(m => new ExportRow
            {
                Id = m.Id,
                Name = m.Name,
                BricklinkId = m.BricklinkId ?? m.FigNum,
                ImageUrl = m.LocalImagePath ?? m.ImageUrl,
                BinLabel = m.StorageBin?.Label ?? "(kein Fach)"
            }).ToList();

            HeaderText.Text = $"{ordered.Count} Figur(en) exportieren";
            SubText.Text = "Format: BrickStore-XML (.bsx). Wird in BrickStore via File > Import importiert.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "BsxExportDialog: Laden der Figuren fehlgeschlagen");
            MessageBox.Show(ex.Message, "Fehler",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Default-Pfad: BsxExportFolder aus Settings (oder Documents/HBSort-Export/),
    /// Dateiname HBSort-Export-yyyy-MM-dd-HHmm.bsx.
    /// </summary>
    private string BuildDefaultExportPath()
    {
        var folder = _settings.Current.BsxExportFolder;
        if (string.IsNullOrWhiteSpace(folder))
        {
            folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "HBSort-Export");
        }
        Directory.CreateDirectory(folder);
        var fileName = $"HBSort-Export-{DateTime.Now:yyyy-MM-dd-HHmm}.bsx";
        return Path.Combine(folder, fileName);
    }

    private void BrowsePath_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "BSX-Datei speichern",
            Filter = "BrickStore-XML (*.bsx)|*.bsx|Alle Dateien (*.*)|*.*",
            DefaultExt = ".bsx",
            FileName = Path.GetFileName(PathBox.Text),
            InitialDirectory = Path.GetDirectoryName(PathBox.Text)
        };
        if (dlg.ShowDialog(this) == true)
        {
            PathBox.Text = dlg.FileName;
        }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var path = PathBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            _notifications.ShowWarning("Bitte einen Speicherpfad waehlen.");
            return;
        }

        var options = new BsxExportOptions(
            Condition: GetSelectedTag(ConditionCombo) ?? "U",
            Status:    GetSelectedTag(StatusCombo)    ?? "I",
            Remark: string.IsNullOrWhiteSpace(RemarkBox.Text) ? null : RemarkBox.Text.Trim());

        try
        {
            var xml = await _exporter.GenerateBsxAsync(_minifigIds, options);

            // Verzeichnis sicherstellen + UTF-8 ohne BOM (BrickStore mag das so)
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, xml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _writtenPath = path;

            // Default-Folder fuer naechstes Mal merken (wenn der User einen anderen
            // Ordner gewaehlt hat als den derzeitigen Default).
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    _settings.Current.BsxExportFolder = dir;
                    await _settings.SaveAsync();
                }
            }
            catch (Exception sx)
            {
                Log.Debug(sx, "BsxExportDialog: BsxExportFolder konnte nicht persistiert werden");
            }

            _notifications.ShowSuccess(
                $"BSX exportiert: {_minifigIds.Count} Figur(en) -> {Path.GetFileName(path)}");

            // Cleanup-Block sichtbar machen, Action-Buttons (Abbrechen/Exportieren)
            // ausblenden bzw. entfernen.
            await ShowCleanupBlockAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "BSX-Export fehlgeschlagen");
            MessageBox.Show("Export fehlgeschlagen:\n\n" + ex.Message,
                "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Bereitet den Cleanup-Block vor (wieviele Figuren entfernen, wieviele
    /// Faecher waeren danach leer) und blendet ihn ein.
    /// </summary>
    private async Task ShowCleanupBlockAsync()
    {
        try
        {
            // Vorab-Berechnung ueber den IStorageBinService (Helper-Methode
            // statt Inline-Loop ueber alle Bins).
            var emptyBins = await _binService.FindBinsThatWouldBeEmptyAsync(_minifigIds);
            _emptyBinIdsAfterExport = emptyBins.Select(b => b.Id).ToList();

            CleanupHeader.Text =
                $"Export erfolgreich ({_minifigIds.Count} Figur(en) -> {Path.GetFileName(_writtenPath)}).";
            ReleaseBinsLabel.Text = _emptyBinIdsAfterExport.Count > 0
                ? $"Auch leere Lagerfaecher freigeben ({_emptyBinIdsAfterExport.Count} Faecher waeren leer)"
                : "Auch leere Lagerfaecher freigeben (keine Faecher wuerden leer)";
            ReleaseBinsCheckbox.IsEnabled = _emptyBinIdsAfterExport.Count > 0;
            ReleaseBinsCheckbox.IsChecked = _emptyBinIdsAfterExport.Count > 0;

            CleanupPanel.Visibility = Visibility.Visible;
            ActionPanel.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Konnte Cleanup-Vorschau nicht aufbereiten");
            CleanupHeader.Text = $"Export erfolgreich ({_minifigIds.Count} Figur(en)).";
            CleanupPanel.Visibility = Visibility.Visible;
            ActionPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void KeepFigures_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private async void RemoveFigures_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var removed = await _persistence.RemoveExportedMinifigsAsync(_minifigIds);
            int releasedBins = 0;
            if (ReleaseBinsCheckbox.IsChecked == true && _emptyBinIdsAfterExport.Count > 0)
            {
                releasedBins = await _binService.ReleaseBinsAsync(_emptyBinIdsAfterExport);
            }

            var msg = releasedBins > 0
                ? $"{removed} Figur(en) entfernt, {releasedBins} Fach/Faecher freigegeben."
                : $"{removed} Figur(en) entfernt.";
            _notifications.ShowSuccess(msg);

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "BSX-Export-Cleanup fehlgeschlagen");
            MessageBox.Show("Cleanup fehlgeschlagen:\n\n" + ex.Message,
                "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static string? GetSelectedTag(ComboBox cb)
        => (cb.SelectedItem as ComboBoxItem)?.Tag?.ToString();

    /// <summary>Reine UI-Datenklasse fuer die Item-Liste.</summary>
    private sealed class ExportRow
    {
        public int Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string BricklinkId { get; init; } = string.Empty;
        public string? ImageUrl { get; init; }
        public string BinLabel { get; init; } = string.Empty;
    }
}
