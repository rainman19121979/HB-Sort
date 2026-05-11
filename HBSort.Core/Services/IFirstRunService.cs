namespace HBSort.Core.Services;

/// <summary>
/// UX X.34 v0.1.20-beta.2: First-Run-Onboarding-Erkennung.
///
/// Beim App-Start wird der aktuelle Setup-Status geprueft. Drei Dimensionen:
/// 1. BL-Catalog vorhanden (bl_items > 0)? - PFLICHT, ohne kann HBSort
///    keine Stammdaten zu gescannten Teilen anzeigen.
/// 2. Lagerfaecher angelegt? - sehr nuetzlich aber nicht zwingend (User
///    kann das spaeter machen).
/// 3. (Bewusst weggelassen) BL-Tokens - optional, nur fuer Preise. Kein
///    Pflicht-Setup-Schritt.
///
/// Brickognize braucht keinen Setup-Schritt - die API ist anonym nutzbar
/// und wird erst beim ersten Scan kontaktiert.
/// </summary>
public interface IFirstRunService
{
    /// <summary>
    /// Prueft den aktuellen Setup-Status. Macht zwei billige DB-Calls:
    /// COUNT auf bl_items + COUNT auf StorageBins.
    /// </summary>
    Task<FirstRunStatus> CheckStatusAsync(CancellationToken ct = default);
}

/// <summary>
/// UX X.34 v0.1.20-beta.2: Setup-Status-Enum.
/// </summary>
public enum FirstRunStatus
{
    /// <summary>Catalog UND Lagerfaecher vorhanden - kein Onboarding-Bedarf.</summary>
    Complete,
    /// <summary>Catalog fehlt, Lagerfaecher vorhanden.</summary>
    NeedsCatalog,
    /// <summary>Catalog vorhanden, Lagerfaecher fehlen.</summary>
    NeedsBins,
    /// <summary>Beides fehlt (frische Installation).</summary>
    NeedsAll
}
