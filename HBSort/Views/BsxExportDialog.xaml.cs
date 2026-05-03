using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using HBSort.Core.Database;
using HBSort.Core.Services;
using HBSort.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using Serilog;

namespace HBSort.Views;

/// <summary>
/// Phase 7 + UX X.6: BSX-Export-Dialog. Wird ueber DI als Transient instanziiert
/// und vom Caller per <see cref="Initialize"/> mit den Figur- und Einzelteil-IDs
/// gefuettert.
///
/// Der Dialog zeigt zwei Sektionen ("Komplette Figuren (X)" und "Einzelteile (Y)")
/// und exportiert beides in eine BSX-Datei. Cleanup nach Export entfernt sowohl
/// Figuren als auch Einzelteile aus der DB; Bin-Freigabe erfolgt nur wenn nach
/// dem Cleanup das Fach komplett leer ist.
/// </summary>
public partial class BsxExportDialog : Window
{
    private readonly IBsxExportService _exporter;
    private readonly IMinifigPersistenceService _persistence;
    private readonly IStorageBinService _binService;
    private readonly INotificationService _notifications;
    private readonly ISettingsService _settings;
    private readonly IDbContextFactory<UserDataContext> _ctxFactory;
    private readonly IBlCatalogService _catalog;

    private List<int> _minifigIds = new();
    private List<int> _floatingIds = new();
    private string? _writtenPath;
    private List<int> _emptyBinIdsAfterExport = new();

    public BsxExportDialog(
        IBsxExportService exporter,
        IMinifigPersistenceService persistence,
        IStorageBinService binService,
        INotificationService notifications,
        ISettingsService settings,
        IDbContextFactory<UserDataContext> ctxFactory,
        IBlCatalogService catalog)
    {
        InitializeComponent();
        _exporter = exporter;
        _persistence = persistence;
        _binService = binService;
        _notifications = notifications;
        _settings = settings;
        _ctxFactory = ctxFactory;
        _catalog = catalog;
    }

    /// <summary>
    /// Wird vom Aufrufer mit den ausgewaehlten TrackedMinifig-IDs UND
    /// FloatingPart-IDs initialisiert. Mindestens eine der beiden Listen
    /// muss befuellt sein. Der Dialog blendet leere Sektionen automatisch
    /// aus.
    /// </summary>
    public void Initialize(IReadOnlyList<int> minifigIds, IReadOnlyList<int> floatingIds)
    {
        _minifigIds = minifigIds.ToList();
        _floatingIds = floatingIds.ToList();
        Loaded += async (_, _) =>
        {
            await LoadItemsAsync();
            PathBox.Text = BuildDefaultExportPath();
        };
    }

    private async Task LoadItemsAsync()
    {
        try
        {
            await using var ctx = await _ctxFactory.CreateDbContextAsync();

            // Minifigs laden + in der Eingabe-Reihenfolge ordnen.
            var minifigs = _minifigIds.Count == 0
                ? new()
                : (await ctx.TrackedMinifigs.AsNoTracking()
                    .Where(m => _minifigIds.Contains(m.Id))
                    .Include(m => m.StorageBin)
                    .ToListAsync())
                  .ToDictionary(m => m.Id);

            var orderedMinifigs = _minifigIds.Where(minifigs.ContainsKey)
                .Select(i => minifigs[i]).ToList();

            // FloatingParts laden + in der Eingabe-Reihenfolge ordnen.
            var floats = _floatingIds.Count == 0
                ? new()
                : (await ctx.FloatingParts.AsNoTracking()
                    .Where(p => _floatingIds.Contains(p.Id))
                    .Include(p => p.StorageBin)
                    .ToListAsync())
                  .ToDictionary(p => p.Id);

            var orderedFloats = _floatingIds.Where(floats.ContainsKey)
                .Select(i => floats[i]).ToList();

            // Color-Lookup aus dem BL-Cache fuer Color-Namen-Anreicherung.
            var colorMap = (await _catalog.GetAllColorsAsync())
                .ToDictionary(c => c.ColorId);

            // Minifig-Liste fuellen.
            MinifigList.ItemsSource = orderedMinifigs.Select(m => new MinifigExportRow
            {
                Name = m.Name,
                BricklinkId = m.BricklinkId ?? m.FigNum,
                ImageUrl = m.LocalImagePath ?? m.ImageUrl,
                BinLabel = m.StorageBin?.Label ?? "(kein Fach)"
            }).ToList();

            // Floating-Liste fuellen.
            FloatingList.ItemsSource = orderedFloats.Select(fp =>
            {
                colorMap.TryGetValue(fp.ColorId, out var color);
                var colorName = color?.Name ?? fp.ColorName;
                if (string.IsNullOrWhiteSpace(colorName)) colorName = $"Color {fp.ColorId}";
                return new FloatingExportRow
                {
                    Name = fp.PartName,
                    BricklinkId = fp.PartNumber,
                    ColorLabel = $"{colorName} (BL:{fp.ColorId})",
                    Quantity = fp.Quantity,
                    BinLabel = fp.StorageBin?.Label ?? "(kein Fach)",
                    ImageUrl = null   // Bild-Lookup hier weggelassen, kein Service-Call noetig
                };
            }).ToList();

            // Sektion-Header mit Counts. Leere Sektion wird komplett ausgeblendet.
            MinifigSectionTitle.Text = $"Komplette Figuren ({orderedMinifigs.Count})";
            FloatingSectionTitle.Text = $"Einzelteile ({orderedFloats.Count})";
            MinifigSectionHeader.Visibility = orderedMinifigs.Count > 0
                ? Visibility.Visible : Visibility.Collapsed;
            MinifigList.Visibility = orderedMinifigs.Count > 0
                ? Visibility.Visible : Visibility.Collapsed;
            FloatingSectionHeader.Visibility = orderedFloats.Count > 0
                ? Visibility.Visible : Visibility.Collapsed;
            FloatingList.Visibility = orderedFloats.Count > 0
                ? Visibility.Visible : Visibility.Collapsed;

            // Headline + Untertitel.
            HeaderText.Text = BuildHeaderText(orderedMinifigs.Count, orderedFloats.Count);
            SubText.Text = "Format: BrickStore-XML (.bsx). Wird in BrickStore via File > Import importiert.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "BsxExportDialog: Laden der Items fehlgeschlagen");
            await App.Services.GetRequiredService<IDialogService>()
                .ShowErrorAsync("Fehler", ex.Message);
        }
    }

    private static string BuildHeaderText(int minifigCount, int floatCount)
    {
        var parts = new List<string>();
        if (minifigCount > 0) parts.Add($"{minifigCount} Figur(en)");
        if (floatCount > 0)   parts.Add($"{floatCount} Einzelteil-Eintrag/-Eintraege");
        return parts.Count == 0
            ? "BSX-Export"
            : "Export: " + string.Join(" + ", parts);
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
            // Beide ID-Listen an den Service durchreichen.
            var xml = await _exporter.GenerateBsxAsync(_minifigIds, _floatingIds, options);

            // Verzeichnis sicherstellen + UTF-8 ohne BOM (BrickStore mag das so).
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, xml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _writtenPath = path;

            // Default-Folder fuer naechstes Mal merken.
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
                $"BSX exportiert: {_minifigIds.Count} Figur(en) + {_floatingIds.Count} Einzelteil-Eintrag/-Eintraege " +
                $"-> {Path.GetFileName(path)}");

            // Cleanup-Block sichtbar machen.
            await ShowCleanupBlockAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "BSX-Export fehlgeschlagen");
            await App.Services.GetRequiredService<IDialogService>()
                .ShowErrorAsync("Fehler", "Export fehlgeschlagen:\n\n" + ex.Message);
        }
    }

    /// <summary>
    /// Bereitet den Cleanup-Block vor (wieviele Figuren + Einzelteile entfernen,
    /// wieviele Faecher waeren danach leer) und blendet ihn ein.
    /// </summary>
    private async Task ShowCleanupBlockAsync()
    {
        try
        {
            // Vorab-Berechnung ueber den IStorageBinService - jetzt mit beiden
            // ID-Listen, damit ein Bin nur dann als "wird leer" gilt wenn nach
            // dem Cleanup weder Minifigs noch FloatingParts mehr drin sind.
            var emptyBins = await _binService.FindBinsThatWouldBeEmptyAsync(
                _minifigIds, _floatingIds);
            _emptyBinIdsAfterExport = emptyBins.Select(b => b.Id).ToList();

            CleanupHeader.Text =
                $"Export erfolgreich ({_minifigIds.Count} Figur(en) + " +
                $"{_floatingIds.Count} Einzelteil-Eintraege -> {Path.GetFileName(_writtenPath)}).";
            CleanupBody.Text =
                "Sollen die exportierten Items jetzt aus HB-Sort entfernt werden? " +
                "Floating-Parts mit Origin-Verbindung bleiben als lose Teile bestehen, " +
                "die explizit ausgewaehlten Einzelteile werden komplett geloescht.";
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
            CleanupHeader.Text = "Export erfolgreich.";
            CleanupBody.Text = "Cleanup-Vorschau konnte nicht aufbereitet werden.";
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
            // Beide Listen entfernen. Reihenfolge: erst Figuren (entkoppelt
            // FloatingParts mit OriginMinifigId), dann die explizit ausgewaehlten
            // FloatingParts loeschen.
            var removedMinifigs = await _persistence.RemoveExportedMinifigsAsync(_minifigIds);
            var removedFloats = await _persistence.RemoveExportedFloatingPartsAsync(_floatingIds);

            int releasedBins = 0;
            if (ReleaseBinsCheckbox.IsChecked == true && _emptyBinIdsAfterExport.Count > 0)
            {
                releasedBins = await _binService.ReleaseBinsAsync(_emptyBinIdsAfterExport);
            }

            var msgParts = new List<string>();
            if (removedMinifigs > 0) msgParts.Add($"{removedMinifigs} Figur(en)");
            if (removedFloats > 0)   msgParts.Add($"{removedFloats} Einzelteil(e)");
            var msg = (msgParts.Count == 0 ? "Nichts" : string.Join(" + ", msgParts)) + " entfernt";
            if (releasedBins > 0) msg += $", {releasedBins} Fach/Faecher freigegeben";
            _notifications.ShowSuccess(msg + ".");

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "BSX-Export-Cleanup fehlgeschlagen");
            await App.Services.GetRequiredService<IDialogService>()
                .ShowErrorAsync("Fehler", "Cleanup fehlgeschlagen:\n\n" + ex.Message);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static string? GetSelectedTag(ComboBox cb)
        => (cb.SelectedItem as ComboBoxItem)?.Tag?.ToString();

    /// <summary>UI-Datenklasse fuer komplette Figuren in der Liste.</summary>
    private sealed class MinifigExportRow
    {
        public string Name { get; init; } = string.Empty;
        public string BricklinkId { get; init; } = string.Empty;
        public string? ImageUrl { get; init; }
        public string BinLabel { get; init; } = string.Empty;
    }

    /// <summary>UI-Datenklasse fuer Einzelteile in der Liste.</summary>
    private sealed class FloatingExportRow
    {
        public string Name { get; init; } = string.Empty;
        public string BricklinkId { get; init; } = string.Empty;
        public string ColorLabel { get; init; } = string.Empty;
        public int Quantity { get; init; }
        public string BinLabel { get; init; } = string.Empty;
        public string? ImageUrl { get; init; }
    }
}
