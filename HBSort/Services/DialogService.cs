using System.Windows;
using ModernWpf.Controls;

namespace HBSort.Services;

/// <summary>
/// Default-Implementierung des Dialog-Service ueber ModernWpf.Controls.ContentDialog.
/// ContentDialog wird auf das aktive WPF-Window angewuendet (ueber XamlRoot resp.
/// Owner-Mechanik in ModernWpf). Wir setzen Owner=ActiveWindow damit der Dialog
/// als Modal zur richtigen Window-Instanz erscheint.
/// </summary>
public class DialogService : IDialogService
{
    public Task ShowInfoAsync(string title, string message)
        => ShowSimpleAsync(title, message);

    public Task ShowErrorAsync(string title, string message)
        => ShowSimpleAsync(title, message);

    public async Task<bool> ShowConfirmAsync(
        string title,
        string message,
        string okText = "OK",
        string cancelText = "Abbrechen")
    {
        var dialog = BuildDialog(title, message);
        dialog.PrimaryButtonText = okText;
        dialog.CloseButtonText = cancelText;
        dialog.DefaultButton = ContentDialogButton.Primary;

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    public Task<bool> ShowQuestionAsync(string title, string message)
        => ShowConfirmAsync(title, message, okText: "Ja", cancelText: "Nein");

    /// <summary>Internes Helper: einfache OK-only-Box.</summary>
    private async Task ShowSimpleAsync(string title, string message)
    {
        var dialog = BuildDialog(title, message);
        dialog.CloseButtonText = "OK";
        await dialog.ShowAsync();
    }

    /// <summary>
    /// Baut einen ContentDialog mit gemeinsamen Defaults. Der Dialog "haftet"
    /// am aktiven WPF-Window (= Window mit IsActive=true), damit er optisch
    /// im richtigen Fenster sitzt - bei Multi-Window-Setups (z.B. SettingsWindow
    /// offen) sonst kaempft er ums richtige Fenster.
    /// </summary>
    private static ContentDialog BuildDialog(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            // Owner sicherstellen, sonst springt der Dialog im Multi-Window-Fall
            // auf das falsche Fenster.
            Owner = GetActiveOwner()
        };
        return dialog;
    }

    /// <summary>
    /// Findet das gerade aktive WPF-Window. Fallback: Application.Current.MainWindow.
    /// </summary>
    private static Window? GetActiveOwner()
    {
        var app = Application.Current;
        if (app == null) return null;

        foreach (Window w in app.Windows)
        {
            if (w.IsActive) return w;
        }
        return app.MainWindow;
    }
}
