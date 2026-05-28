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

    public MassUpdateExportViewModel(
        IBlInventoryService inventory,
        INotificationService notify)
    {
        _inventory = inventory;
        _notify = notify;
    }

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

    [RelayCommand(CanExecute = nameof(HasPending))]
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

    [RelayCommand(CanExecute = nameof(HasPending))]
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

    private bool CanVerify() => HasPending && !IsVerifying;
}
