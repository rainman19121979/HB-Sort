namespace HBSort.Services;

/// <summary>
/// Modal-Dialoge im einheitlichen ModernWpf-Stil. Ersetzt die klassischen
/// System.Windows.MessageBox.Show-Aufrufe (UX-Iteration X.4) und sorgt fuer
/// einheitliche Buttons + Look quer durch die App.
///
/// Konventionen:
///   - Destruktive Aktionen (Loeschen, Leeren, Aufgeben): "Ja" / "Nein"
///   - Nicht-destruktive Bestaetigung: "OK" / "Abbrechen"
///   - Info / Fehler: nur "OK"
///
/// Alle Methoden sind async. Aufrufer aus Click-Handlern muessen "async void"
/// sein damit await sauber laeuft - .Wait() oder .Result wuerden den UI-Thread
/// blockieren.
/// </summary>
public interface IDialogService
{
    /// <summary>Reine Hinweis-Box mit "OK"-Button.</summary>
    Task ShowInfoAsync(string title, string message);

    /// <summary>Fehler-Box mit "OK"-Button (visuell wie Info, aber semantisch klar).</summary>
    Task ShowErrorAsync(string title, string message);

    /// <summary>
    /// Generische Bestaetigungs-Box. okText/cancelText explizit setzbar.
    /// Liefert true wenn der Primary-Button (links) geklickt wurde.
    /// </summary>
    Task<bool> ShowConfirmAsync(
        string title,
        string message,
        string okText = "OK",
        string cancelText = "Abbrechen");

    /// <summary>
    /// Convenience: destruktive Frage mit "Ja" / "Nein". Nutzt ShowConfirmAsync.
    /// </summary>
    Task<bool> ShowQuestionAsync(string title, string message);
}
