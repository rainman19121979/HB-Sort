using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HBSort.Core.Models.Exceptions;
using HBSort.Core.Services;
using HBSort.Services;
using Serilog;

namespace HBSort.ViewModels;

/// <summary>
/// v0.1.24-beta.12 Phase 4: Dialog "BL Mass-Update". Zeigt das generierte
/// XML, bietet "In Zwischenablage", "BrickLink oeffnen" und "Verifizieren"
/// an. Bei Empty-State (keine Reservierungen) wird die Aktion deaktiviert
/// und ein Hinweis-Text angezeigt.
///
/// <para>v0.1.24-beta.13: Beim Oeffnen des Dialogs laeuft zuerst ein
/// frischer <see cref="IBlInventoryService.SyncInventoryAsync"/> damit das
/// XML gegen die aktuelle BL-Realitaet generiert wird. Faellt der Sync aus
/// (Auth/Rate-Limit/Netzwerk), wird trotzdem mit den lokalen Stale-Daten
/// generiert (gleiche Konvention wie "BL offline -> stale Cache mit Toast-
/// Hinweis"). Der Sync-Status wird in <see cref="SyncInfoText"/> sichtbar
/// gemacht.</para>
/// </summary>
public partial class MassUpdateExportViewModel : ObservableObject
{
    private const string BrickLinkMassUpdateUrl = "https://www.bricklink.com/invXML.asp#update";

    private readonly IBlInventoryService _inventory;
    private readonly INotificationService _notify;

    [ObservableProperty]
    private string _xmlText = string.Empty;

    /// <summary>"X Lots betroffen (Y werden geloescht, Z reduziert)" oder Empty-Hinweis.</summary>
    [ObservableProperty]
    private string _infoText = string.Empty;

    /// <summary>
    /// v0.1.24-beta.13: Status des Initial-Syncs ("Inventar synchronisiert
    /// um HH:mm", "Sync fehlgeschlagen - Export basiert auf zuletzt
    /// bekanntem Stand vom DD.MM. HH:mm", ggf. Cap-Hinweis-Zusatz). Wird
    /// im Dialog ueber der XML-Box angezeigt.
    /// </summary>
    [ObservableProperty]
    private string _syncInfoText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPending))]
    [NotifyCanExecuteChangedFor(nameof(CopyXmlCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenBrickLinkCommand))]
    [NotifyCanExecuteChangedFor(nameof(VerifyCommand))]
    private int _pendingCount;

    public bool HasPending => PendingCount > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(VerifyCommand))]
    private bool _isVerifying;

    /// <summary>
    /// v0.1.24-beta.13: True solange Initial-Sync + Generate laufen.
    /// Buttons im Dialog werden waehrenddessen deaktiviert (HasPending=false
    /// zusaetzlich). UI bindet auch eine Loading-Anzeige hieran.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyXmlCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenBrickLinkCommand))]
    [NotifyCanExecuteChangedFor(nameof(VerifyCommand))]
    private bool _isLoading;

    public MassUpdateExportViewModel(
        IBlInventoryService inventory,
        INotificationService notify)
    {
        _inventory = inventory;
        _notify = notify;
    }

    /// <summary>
    /// v0.1.24-beta.13: Einstiegspunkt beim Dialog-Open. Macht zwei Schritte:
    /// <list type="number">
    ///   <item>Frischen BL-Inventar-Sync (damit das XML gegen die aktuellen
    ///   Mengen generiert wird - sonst entsteht ein Update gegen Stale-Daten
    ///   das beim Verify spaeter fehlschlaegt).</item>
    ///   <item><see cref="GenerateAsync"/> mit dem nun aktuellen Stand.</item>
    /// </list>
    /// Bei Sync-Fehler (Auth/Rate-Limit/Netz) wird trotzdem generiert -
    /// der User kann mit Stale-Daten exportieren, wird aber im Dialog
    /// explizit darauf hingewiesen (Stale-Cache-Konvention).
    /// </summary>
    public async Task InitializeAsync()
    {
        IsLoading = true;
        SyncInfoText = "Inventar wird synchronisiert...";
        try
        {
            // --- Phase 1: Sync ---
            try
            {
                var syncResult = await _inventory.SyncInventoryAsync();
                var local = syncResult.SyncedAt.ToLocalTime();
                var baseMsg = $"Inventar synchronisiert um {local:HH:mm}.";
                if (syncResult.CappedReservations > 0)
                {
                    // V6-Cap: HBSort hat Reservierungen wegen gesunkener BL-
                    // Mengen nachgezogen. User soll das mitbekommen damit
                    // klar ist warum manche Lots evtl. anders im XML stehen.
                    baseMsg += $" {syncResult.CappedReservations} Reservierung(en) " +
                               "wegen geaenderter BL-Mengen angepasst.";
                }
                SyncInfoText = baseMsg;
            }
            catch (BricklinkAuthException ex)
            {
                Log.Warning(ex, "MassUpdateExport.Initialize: Sync Auth fehlgeschlagen - Stale-Fallback");
                SyncInfoText = BuildStaleHint("Tokens fehlen oder ungueltig");
            }
            catch (BricklinkRateLimitException ex)
            {
                Log.Warning(ex, "MassUpdateExport.Initialize: Sync Rate-Limit - Stale-Fallback");
                SyncInfoText = BuildStaleHint("Rate-Limit erreicht");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "MassUpdateExport.Initialize: Sync fehlgeschlagen - Stale-Fallback");
                SyncInfoText = BuildStaleHint("BL nicht erreichbar");
            }

            // --- Phase 2: Generate immer ausfuehren (auch bei Sync-Fehler) ---
            await GenerateAsync();
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Baut den Stale-Fallback-Hinweis fuer den Dialog. Liest das aelteste
    /// LastSyncedAt aus dem lokalen Inventar nicht aus (waere ein DB-Read +
    /// async) - der User sieht stattdessen den ungenauen aber sicheren
    /// Hinweis "zuletzt bekanntem Stand".
    /// </summary>
    private static string BuildStaleHint(string reason)
        => $"Sync fehlgeschlagen ({reason}) - Export basiert auf zuletzt " +
           $"bekanntem Stand. Bitte BL pruefen bevor du das XML hochlaedst.";

    /// <summary>Generiert XML + PendingExports + befuellt die Anzeige-Properties.</summary>
    public async Task GenerateAsync()
    {
        try
        {
            var result = await _inventory.GenerateMassUpdateXmlAsync();
            XmlText = result.Xml;
            PendingCount = result.TotalLots;
            InfoText = result.TotalLots == 0
                ? "Keine offenen Reservierungen zum Exportieren."
                : $"{result.TotalLots} Lot(s) betroffen ({result.DeletedLots} werden geloescht, " +
                  $"{result.ReducedLots} reduziert).";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "MassUpdateExport: GenerateAsync fehlgeschlagen");
            InfoText = $"Fehler beim Erzeugen: {ex.Message}";
            XmlText = string.Empty;
            PendingCount = 0;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCopyOrOpen))]
    public void CopyXml()
    {
        if (string.IsNullOrEmpty(XmlText)) return;
        try
        {
            Clipboard.SetText(XmlText);
            _notify.ShowSuccess("XML in die Zwischenablage kopiert.");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "MassUpdateExport: Clipboard.SetText fehlgeschlagen");
            _notify.ShowError("Konnte XML nicht in die Zwischenablage kopieren.");
        }
    }

    [RelayCommand(CanExecute = nameof(CanCopyOrOpen))]
    public void OpenBrickLink()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = BrickLinkMassUpdateUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "MassUpdateExport: Browser-Oeffnen fehlgeschlagen");
            _notify.ShowError("Konnte BrickLink nicht im Browser oeffnen.");
        }
    }

    [RelayCommand(CanExecute = nameof(CanVerify))]
    public async Task VerifyAsync()
    {
        IsVerifying = true;
        try
        {
            var result = await _inventory.VerifyExportAsync();
            if (result.SuccessCount == 0 && result.FailedCount == 0)
            {
                _notify.ShowInfo("Keine offenen Mass-Update-Eintraege zum Verifizieren.");
            }
            else
            {
                _notify.ShowSuccess(
                    $"{result.SuccessCount} erfolgreich verifiziert, " +
                    $"{result.FailedCount} fehlgeschlagen, " +
                    $"{result.RemainingCount} offen.");
            }
            // Nach Verify den Dialog-Inhalt frisch ziehen: erfolgreiche
            // Pending-Eintraege sind weg, der XML-Inhalt schrumpft.
            await GenerateAsync();
        }
        catch (BricklinkAuthException ex)
        {
            Log.Warning(ex, "MassUpdateExport.Verify: Auth");
            _notify.ShowError("BrickLink-Tokens fehlen oder sind ungueltig.");
        }
        catch (BricklinkRateLimitException ex)
        {
            Log.Warning(ex, "MassUpdateExport.Verify: Rate-Limit");
            _notify.ShowError(ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "MassUpdateExport: VerifyAsync fehlgeschlagen");
            _notify.ShowError($"Verifizieren fehlgeschlagen: {ex.Message}");
        }
        finally
        {
            IsVerifying = false;
        }
    }

    private bool CanVerify() => HasPending && !IsVerifying && !IsLoading;
    private bool CanCopyOrOpen() => HasPending && !IsLoading;
}
