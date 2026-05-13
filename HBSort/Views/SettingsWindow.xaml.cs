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
/// Code-Behind fuer das Einstellungen-Fenster.
/// Minimal gehalten: nur die Button-Clicks, die Logik steckt im ViewModel.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
        : this(viewModel, SettingsTab.Default)
    {
    }

    /// <summary>
    /// v0.1.22-beta.3 (2026-05-13): zusaetzlicher ctor-Pfad fuer Aufrufer
    /// die direkt auf einen bestimmten Tab springen wollen (z.B. der
    /// Volle-Faecher-Banner-Button -> SettingsTab.Lagerfaecher).
    /// </summary>
    public SettingsWindow(SettingsViewModel viewModel, SettingsTab initialTab)
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
        // UX X.33 v0.1.19-beta.7 Block M: Category-Mappings ebenfalls einmalig
        // beim Oeffnen laden (best-effort, fail-silent).
        // v0.1.22-beta.3: zusaetzlich Initial-Tab auswaehlen wenn != Default.
        Loaded += async (_, _) =>
        {
            try { await _viewModel.LoadStatsAsync(); }
            catch (System.Exception ex) { Log.Debug(ex, "LoadStatsAsync fehlgeschlagen"); }

            try { await _viewModel.ReloadCategoryMappingsAsync(); }
            catch (System.Exception ex) { Log.Debug(ex, "ReloadCategoryMappingsAsync fehlgeschlagen"); }

            // v0.1.22-beta.4: Lazy-Enumeration der Kameras beim Settings-
            // Open (auf Thread-Pool, blockt UI nicht). Combobox zeigt
            // bis dahin einen Platzhalter "Kamera {savedIndex}".
            try { await _viewModel.EnumerateCamerasAsync(); }
            catch (System.Exception ex) { Log.Debug(ex, "EnumerateCamerasAsync fehlgeschlagen"); }

            if (initialTab != SettingsTab.Default)
            {
                SelectTab(initialTab);
            }
        };
    }

    private void SelectTab(SettingsTab tab)
    {
        // Header-String muss exakt zum XAML-Header passen (case-insensitiv
        // gegen Contains). Lagerfaecher-Tab = Header="Lagerfaecher" (Zeile 874
        // in SettingsWindow.xaml).
        var headerName = tab switch
        {
            SettingsTab.Lagerfaecher => "Lagerfaecher",
            SettingsTab.BrickLink     => "BrickLink",
            SettingsTab.Kategorien    => "Kategorien",
            _ => null
        };
        if (headerName == null) return;

        foreach (var obj in MainTabControl.Items)
        {
            if (obj is TabItem item
                && item.Header is string header
                && header.Contains(headerName, System.StringComparison.OrdinalIgnoreCase))
            {
                item.IsSelected = true;
                return;
            }
        }
    }

    /// <summary>Speichern-Button: Settings uebernehmen und Fenster schliessen</summary>
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.SaveSettingsAsync();
        DialogResult = true; // Signalisiert dem Hauptfenster: "Aenderungen uebernommen"
        Close();
    }

    /// <summary>Abbrechen-Button: Fenster schliessen ohne zu speichern</summary>
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    // Cache-Limit Radio-Buttons. Wir koennten die direkt an die Property binden,
    // aber RadioButton.IsChecked ist nur One-Way an IsLimitXyz - Click-Handler ist klarer.
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

    /// <summary>Per-Row "Inhalt" - oeffnet BinDetailDialog mit Figuren + Floating-Parts.</summary>
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
            await App.Services.GetRequiredService<IDialogService>()
                .ShowErrorAsync("Umbenennen", ex.Message);
        }
    }

    /// <summary>Per-Row Leeren. Zeigt Bestaetigungs-Dialog mit Inhalts-Info.</summary>
    private async void BinEmpty_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not BinRowViewModel row) return;
        var dialogs = App.Services.GetRequiredService<IDialogService>();

        if (row.IsFree)
        {
            await dialogs.ShowInfoAsync("Leeren", "Das Fach ist bereits frei.");
            return;
        }

        // UX X.29 Block C (v0.1.16): differenzierte Inventur via GetEmptyPreviewAsync,
        // damit Complete-Figuren explizit hervorgehoben werden (sie verlieren beim
        // Leeren ihr Lagerfach - siehe Diagnose-Befund).
        var binService = App.Services.GetRequiredService<IStorageBinService>();
        var preview = await binService.GetEmptyPreviewAsync(row.Id);
        if (preview == null) return;

        string msg;
        if (preview.CompleteMinifigsCount > 0)
        {
            // Sonderwarnung mit Achtung-Hinweis.
            msg = $"ACHTUNG: Fach '{preview.Label}' enthaelt " +
                  $"{preview.CompleteMinifigsCount} KOMPLETT zusammengebaute Figur(en)!\n\n" +
                  $"Diese verlieren beim Leeren ihr Lagerfach (StorageBinId=null) und " +
                  $"erscheinen in der Lagerliste ohne Fach-Zuordnung. " +
                  $"Strg+Z (Verlauf-Tab) macht jede Entkopplung einzeln rueckgaengig.\n\n" +
                  $"Inhalt:\n" +
                  $"  - {preview.WaitingMinifigsCount} wartende Figur(en)\n" +
                  $"  - {preview.CompleteMinifigsCount} komplette Figur(en) - !!!\n" +
                  $"  - {preview.FloatingPartsCount} Einzelteil-Eintrag/Eintraege (werden geloescht)\n\n" +
                  $"Wirklich fortfahren?";
        }
        else
        {
            msg = $"Fach '{preview.Label}' enthaelt {preview.WaitingMinifigsCount} wartende Figur(en) und " +
                  $"{preview.FloatingPartsCount} Einzelteil(e).\n\n" +
                  "Beim Leeren werden:\n" +
                  "  - Wartende Figuren vom Fach geloest (bleiben in der DB)\n" +
                  "  - Einzelteile geloescht\n\n" +
                  "Strg+Z (Verlauf-Tab) macht die Aktion rueckgaengig.\n\n" +
                  "Fortfahren?";
        }
        if (!await dialogs.ShowQuestionAsync("Lagerfach leeren", msg)) return;

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
            await dialogs.ShowErrorAsync("Leeren", ex.Message);
        }
    }

    /// <summary>Per-Row Loeschen. Schlaegt fehl wenn Fach belegt ist (Service prueft).</summary>
    private async void BinDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not BinRowViewModel row) return;
        var dialogs = App.Services.GetRequiredService<IDialogService>();

        if (!row.IsFree)
        {
            await dialogs.ShowInfoAsync(
                "Loeschen nicht moeglich",
                $"Fach '{row.Label}' enthaelt noch Figuren oder Teile.\nBitte erst leeren.");
            return;
        }

        // Destruktive Aktion -> "Ja" / "Nein".
        if (!await dialogs.ShowQuestionAsync(
            "Lagerfach loeschen",
            $"Fach '{row.Label}' wirklich endgueltig loeschen?"))
            return;

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
            await dialogs.ShowErrorAsync("Loeschen", ex.Message);
        }
    }

    // ====================================================================
    // Tab "BL-Catalog-Daten" (Phase 5.5: BrickStore-Bulk-Import)
    // ====================================================================

    /// <summary>"Von GitHub importieren": laedt downloads.zip + entpackt + importiert.
    /// UX X.29 (v0.1.16): mit ETag/Hash-Caching - bei unveraenderter
    /// BrickStore-DB wird nur ein HEAD-roundtrip gemacht und als
    /// "uebersprungen" gemeldet.</summary>
    private async void ImportFromGitHub_Click(object sender, RoutedEventArgs e)
    {
        var importer = App.Services.GetRequiredService<IBlBulkImportService>();
        var notif = App.Services.GetRequiredService<INotificationService>();
        var settings = App.Services.GetRequiredService<HBSort.Core.Services.ISettingsService>();
        var prevEtag = settings.Current.LastBlImportEtag;
        var prevHash = settings.Current.LastBlImportContentHash;
        await RunImportAsync(notif,
            (progress, ct) => importer.ImportFromGitHubAsync(prevEtag, prevHash, progress, ct));
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

    /// <summary>Phase 8: Provider=None ausgewaehlt -> Detail-Felder kollabieren via Visibility-Trigger.</summary>
    private void PriceProviderNone_Click(object sender, RoutedEventArgs e)
        => _viewModel.PriceProvider = "None";

    /// <summary>Phase 8: Provider=BL ausgewaehlt -> Detail-Felder werden sichtbar.</summary>
    private void PriceProviderBricklink_Click(object sender, RoutedEventArgs e)
        => _viewModel.PriceProvider = "BricklinkApi";

    /// <summary>UX X.34 v0.1.20: Filter-Mode Either-Or - Global.</summary>
    private void PriceFilterGlobal_Click(object sender, RoutedEventArgs e)
        => _viewModel.PriceFilterMode = HBSort.Core.Models.PriceFilterMode.Global;

    /// <summary>UX X.34 v0.1.20: Filter-Mode Either-Or - Region.</summary>
    private void PriceFilterRegion_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.PriceFilterMode = HBSort.Core.Models.PriceFilterMode.Region;
        // Sinnvollen Default setzen wenn das Feld leer ist (User hat gerade
        // Region gewaehlt, will also einen Wert haben - sonst macht der Modus
        // keinen Sinn). "europe" matcht das alte Default-Verhalten.
        if (string.IsNullOrWhiteSpace(_viewModel.PriceRegion))
            _viewModel.PriceRegion = "europe";
    }

    /// <summary>UX X.34 v0.1.20: Filter-Mode Either-Or - Country.</summary>
    private void PriceFilterCountry_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.PriceFilterMode = HBSort.Core.Models.PriceFilterMode.Country;
        // Analog: "DE" als Default setzen wenn leer (alte Default-Erwartung).
        if (string.IsNullOrWhiteSpace(_viewModel.PriceCountryCode))
            _viewModel.PriceCountryCode = "DE";
    }

    /// <summary>
    /// Phase 7: Ordner-Picker fuer den BSX-Export-Default-Folder.
    ///
    /// Wichtig: Im Export-Tab wird die Aenderung SOFORT in settings.json
    /// gespeichert (per SaveBsxExportFolderImmediatelyAsync), nicht erst
    /// beim "Speichern"-Button. Damit greift der neue Default direkt beim
    /// naechsten BsxExportDialog.
    ///
    /// Validation: existiert der Ordner nicht (kann passieren wenn man
    /// einen Pfad waehlt der spaeter geloescht wird), warnen wir den User
    /// einmal - speichern aber trotzdem, weil der Export den Ordner bei
    /// Bedarf selbst anlegt (Directory.CreateDirectory in BsxExportDialog).
    /// </summary>
    private async void BrowseBsxFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "BSX-Export-Ordner waehlen"
        };
        // Wenn schon ein gueltiger Pfad gesetzt ist: dort starten.
        if (!string.IsNullOrWhiteSpace(_viewModel.BsxExportFolder)
            && System.IO.Directory.Exists(_viewModel.BsxExportFolder))
        {
            dlg.InitialDirectory = _viewModel.BsxExportFolder;
        }
        if (dlg.ShowDialog(this) != true) return;

        var chosen = dlg.FolderName;

        // Defensive Pruefung: existiert der gewaehlte Ordner? Der WPF-Picker
        // sollte das eigentlich garantieren, aber wir wollen den User
        // warnen falls ein nicht-mehr-vorhandener Pfad reinkommt.
        if (!System.IO.Directory.Exists(chosen))
        {
            await App.Services.GetRequiredService<IDialogService>().ShowInfoAsync(
                "Ordner nicht gefunden",
                $"Der gewaehlte Ordner existiert nicht:\n\n{chosen}\n\n" +
                "Der Pfad wird trotzdem gespeichert. Beim Export wird der Ordner " +
                "bei Bedarf automatisch angelegt.");
        }

        // Sofort speichern (settings.json wird geschrieben, BsxExportDialog
        // sieht den neuen Pfad beim naechsten Oeffnen direkt).
        await _viewModel.SaveBsxExportFolderImmediatelyAsync(chosen);
    }

    /// <summary>
    /// Phase 7: Setzt den BSX-Export-Ordner auf den Default zurueck
    /// (Documents\HBSort-Export\). Intern wird das als NULL gespeichert
    /// damit der BsxExportDialog den Default-Pfad selbst zusammenbaut.
    /// </summary>
    private async void ResetBsxFolder_Click(object sender, RoutedEventArgs e)
    {
        // NULL = Default. Der BsxExportDialog bildet daraus dann
        // Documents\HBSort-Export\ (siehe BsxExportDialog.BuildDefaultExportPath).
        await _viewModel.SaveBsxExportFolderImmediatelyAsync(null);
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
            // Schwere Operation auf den Threadpool - UI-Thread bleibt responsiv,
            // Progress-Reports kommen dank IProgress<T> auf dem UI-Thread an.
            var result = await Task.Run(() => importer(progress, default));

            // UX X.29 (v0.1.16): Skipped-Pfad - keine Daten geaendert,
            // aber Pruefung wurde durchgefuehrt.
            var settings = App.Services.GetRequiredService<HBSort.Core.Services.ISettingsService>();
            settings.Current.LastBlImportCheck = System.DateTime.UtcNow;
            if (result.Skipped)
            {
                _viewModel.ImportResultText =
                    "BrickStore-DB unveraendert seit letztem Import - keine Aenderungen noetig.";
                if (!string.IsNullOrEmpty(result.NewEtag))
                    settings.Current.LastBlImportEtag = result.NewEtag;
                if (!string.IsNullOrEmpty(result.NewContentHash))
                    settings.Current.LastBlImportContentHash = result.NewContentHash;
                await settings.SaveAsync();
                await _viewModel.RefreshLastBlImportTextAsync();
                notif.ShowInfo("BrickLink-Daten sind aktuell.");
                return;
            }

            _viewModel.ImportResultText =
                $"Import erfolgreich: {result.ItemsImported:N0} Items, " +
                $"{result.InventoriesImported:N0} Subsets in {result.Duration.TotalSeconds:F1}s." +
                (result.Errors.Count > 0 ? $" ({result.Errors.Count} Fehler)" : string.Empty);
            await _viewModel.RefreshBrickStoreStatsAsync();
            await _viewModel.RefreshBlCacheStatsAsync();
            // UX X.28 (v0.1.15): manueller Import setzt LastBlImport, damit
            // der Auto-Import-Timer nicht direkt nochmal triggert.
            settings.Current.LastBlImport = System.DateTime.UtcNow;
            if (!string.IsNullOrEmpty(result.NewEtag))
                settings.Current.LastBlImportEtag = result.NewEtag;
            if (!string.IsNullOrEmpty(result.NewContentHash))
                settings.Current.LastBlImportContentHash = result.NewContentHash;
            await settings.SaveAsync();
            await _viewModel.RefreshLastBlImportTextAsync();
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

    // ====================================================================
    // UX X.29 (v0.1.16): Backup-Tab Click-Handler
    // ====================================================================

    private async void CreateBackupNow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        var oldText = BackupStatusText.Text;
        try
        {
            b.IsEnabled = false;
            BackupStatusText.Text = "Backup laeuft...";
            var status = await _viewModel.CreateBackupNowAsync();
            BackupStatusText.Text = status;
        }
        finally
        {
            b.IsEnabled = true;
        }
    }

    private async void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not BackupRowViewModel row) return;
        if (!b.IsEnabled) return;
        b.IsEnabled = false;

        try
        {
            var dialogs = App.Services.GetRequiredService<IDialogService>();
            var ok = await dialogs.ShowQuestionAsync(
                "Backup wiederherstellen?",
                $"Aktuelle Daten werden mit dem Backup vom " +
                $"{row.CreatedAtDisplay} ueberschrieben.\n\n" +
                $"Vor dem Restore wird automatisch ein Sicherungs-Backup deiner " +
                $"aktuellen Daten erstellt - falls etwas schiefgeht, kannst du " +
                $"das ueber dieselbe Liste wiederherstellen.\n\n" +
                $"Die App startet danach automatisch neu, damit die Datenbank-" +
                $"Dateien korrekt ersetzt werden koennen.\n\n" +
                $"Wirklich fortfahren?");
            if (!ok) return;

            var success = await _viewModel.RestoreBackupAsync(row.FileName);
            if (!success)
            {
                BackupStatusText.Text = "Wiederherstellung fehlgeschlagen.";
                await dialogs.ShowErrorAsync(
                    "Fehler",
                    "Das Backup konnte nicht vorbereitet werden. " +
                    "Pruefe die Logs fuer Details.");
                return;
            }

            BackupStatusText.Text = "Restore vorbereitet. App startet jetzt neu...";
            // Kurz Zeit lassen damit der Status sichtbar wird.
            await System.Threading.Tasks.Task.Delay(800);

            // Settings sauber speichern + App-Neustart triggern.
            // Process.Start auf den eigenen Pfad + Application.Shutdown
            // (analog zu Velopack-Update-Pfad aus UX X.23).
            await App.Services.GetRequiredService<ISettingsService>().SaveAsync();

            try
            {
                var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = exePath,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Auto-Neustart nach Restore fehlgeschlagen - " +
                    "User muss manuell starten");
                await dialogs.ShowInfoAsync(
                    "Bitte App neu starten",
                    "Der Auto-Neustart hat nicht funktioniert. Schliesse die " +
                    "App jetzt manuell und starte sie neu - der Restore wird " +
                    "dann automatisch angewendet.");
            }

            Application.Current.Shutdown();
        }
        finally
        {
            b.IsEnabled = true;
        }
    }

    private async void DeleteBackup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not BackupRowViewModel row) return;
        if (!b.IsEnabled) return;
        b.IsEnabled = false;

        try
        {
            var dialogs = App.Services.GetRequiredService<IDialogService>();
            var ok = await dialogs.ShowQuestionAsync(
                "Backup loeschen?",
                $"Backup '{row.FileName}' dauerhaft loeschen?\n\n" +
                $"Diese Aktion kann nicht rueckgaengig gemacht werden.");
            if (!ok) return;

            var success = await _viewModel.DeleteBackupAsync(row.FileName);
            BackupStatusText.Text = success
                ? $"'{row.FileName}' geloescht."
                : "Loeschen fehlgeschlagen.";
        }
        finally
        {
            b.IsEnabled = true;
        }
    }
}

/// <summary>
/// v0.1.22-beta.3 (2026-05-13): Auswahl des Initial-Tabs beim Oeffnen des
/// SettingsWindow. Default = den bestehenden zuerst-deklarierten Tab
/// (Erkennung).
/// </summary>
public enum SettingsTab
{
    Default,
    Lagerfaecher,
    BrickLink,
    Kategorien
}
