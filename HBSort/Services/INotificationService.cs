namespace HBSort.Services;

/// <summary>
/// Toast-Benachrichtigungs-System.
/// Zeigt kurze Hinweise unten rechts im Hauptfenster (3 Sekunden).
/// </summary>
public interface INotificationService
{
    /// <summary>Zeigt einen Info-Toast (Standard).</summary>
    void ShowInfo(string message);

    /// <summary>Zeigt einen Erfolgs-Toast (gruen).</summary>
    void ShowSuccess(string message);

    /// <summary>Zeigt einen Warnung-Toast (gelb).</summary>
    void ShowWarning(string message);

    /// <summary>Zeigt einen Fehler-Toast (rot).</summary>
    void ShowError(string message);
}

/// <summary>Toast-Typ - bestimmt Hintergrundfarbe.</summary>
public enum ToastKind
{
    Info,
    Success,
    Warning,
    Error
}

/// <summary>Daten-Container fuer einen aktiven Toast (zur UI-Bindung).</summary>
public class ToastItem
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Message { get; init; } = string.Empty;
    public ToastKind Kind { get; init; } = ToastKind.Info;
    public DateTime CreatedAt { get; } = DateTime.Now;
}
