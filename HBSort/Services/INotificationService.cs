namespace HBSort.Services;

/// <summary>
/// Toast-Benachrichtigungs-System.
/// Zeigt kurze Hinweise unten rechts im Hauptfenster (3 Sekunden).
///
/// UX X.20 Teil 5: Toasts koennen optional ein kleines Item-Bild
/// links neben dem Text fuehren - z.B. nach erfolgreichem Einlagern.
/// </summary>
public interface INotificationService
{
    /// <summary>Zeigt einen Info-Toast (Standard).</summary>
    void ShowInfo(string message);

    /// <summary>Zeigt einen Erfolgs-Toast (gruen). Optional mit Item-Bild.</summary>
    void ShowSuccess(string message, string? imageUrl = null);

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

    /// <summary>
    /// Optionale URL zu einem kleinen Item-Bild (BL- oder lokal-gecached).
    /// Wird im Toast-Template links neben dem Text gerendert. Null/Leer
    /// = kein Bild, Toast bleibt schmal.
    /// </summary>
    public string? ImageUrl { get; init; }

    /// <summary>True wenn ImageUrl gesetzt ist - Binding-freundlich.</summary>
    public bool HasImage => !string.IsNullOrWhiteSpace(ImageUrl);
}
