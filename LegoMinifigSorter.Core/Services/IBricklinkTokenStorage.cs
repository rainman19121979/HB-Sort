using LegoMinifigSorter.Core.Models;

namespace LegoMinifigSorter.Core.Services;

/// <summary>
/// Verschluesselte Speicherung der vier BL-OAuth1-Tokens.
/// Verschluesselung via Windows DPAPI (CurrentUser-Scope), Persistierung in
/// settings.json unter `bricklink.tokensEncrypted` (Base64).
///
/// Sicherheits-Eigenschaft: kopiert jemand die settings.json auf einen anderen
/// PC oder verwendet sie unter einem anderen Windows-User, scheitert das
/// Entschluesseln (DPAPI bindet den Schluessel an User-SID).
/// </summary>
public interface IBricklinkTokenStorage
{
    /// <summary>
    /// Persistiert die Tokens (verschluesselt) in settings.json.
    /// Loest IsettingsService.SaveAsync intern aus.
    /// </summary>
    Task SaveAsync(BricklinkTokens tokens);

    /// <summary>
    /// Laedt + entschluesselt die Tokens. Null wenn keine Tokens hinterlegt sind.
    /// Wirft <see cref="System.Security.Cryptography.CryptographicException"/>
    /// wenn das verschluesselte Blob nicht entschluesselt werden kann.
    /// </summary>
    Task<BricklinkTokens?> LoadAsync();

    /// <summary>True wenn settings.json einen Tokens-Eintrag hat (egal ob entschluesselbar).</summary>
    bool HasTokens();

    /// <summary>Loescht die Tokens aus settings.json.</summary>
    Task ClearAsync();
}
