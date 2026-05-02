using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LegoMinifigSorter.Core.Models;
using Serilog;

namespace LegoMinifigSorter.Core.Services;

/// <summary>
/// Default-Implementierung des Token-Storages mit Windows DPAPI.
///
/// Ablauf bei Save:
///   tokens -> JSON -> UTF8-Bytes -> ProtectedData.Protect(CurrentUser) -> Base64 -> settings.json
/// Ablauf bei Load:
///   settings.json -> Base64-Decode -> ProtectedData.Unprotect -> UTF8-String -> JSON-Deserialize
/// </summary>
public class BricklinkTokenStorage : IBricklinkTokenStorage
{
    /// <summary>
    /// Optionaler "Entropy"-Wert: zusaetzliche app-spezifische Pseudo-Salt damit auch andere
    /// Anwendungen mit DPAPI-CurrentUser-Scope das Blob nicht versehentlich entschluesseln.
    /// </summary>
    private static readonly byte[] AppEntropy = Encoding.UTF8.GetBytes("LegoMinifigSorter.BL.v1");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ISettingsService _settings;

    public BricklinkTokenStorage(ISettingsService settings)
    {
        _settings = settings;
    }

    public async Task SaveAsync(BricklinkTokens tokens)
    {
        // Tokens-Objekt -> JSON -> Bytes
        var json = JsonSerializer.Serialize(tokens, JsonOptions);
        var plain = Encoding.UTF8.GetBytes(json);

        // DPAPI-Encrypt (CurrentUser-Scope: an Windows-User-SID gebunden)
        byte[] encrypted = ProtectedData.Protect(plain, AppEntropy, DataProtectionScope.CurrentUser);
        var base64 = Convert.ToBase64String(encrypted);

        _settings.Current.Bricklink.TokensEncrypted = base64;
        await _settings.SaveAsync();
        Log.Information("BL-Tokens gespeichert (DPAPI-verschluesselt)");
    }

    public Task<BricklinkTokens?> LoadAsync()
    {
        var encrypted = _settings.Current.Bricklink.TokensEncrypted;
        if (string.IsNullOrWhiteSpace(encrypted))
        {
            return Task.FromResult<BricklinkTokens?>(null);
        }

        try
        {
            var blob = Convert.FromBase64String(encrypted);
            var plain = ProtectedData.Unprotect(blob, AppEntropy, DataProtectionScope.CurrentUser);
            var json = Encoding.UTF8.GetString(plain);
            var tokens = JsonSerializer.Deserialize<BricklinkTokens>(json, JsonOptions);
            return Task.FromResult(tokens);
        }
        catch (CryptographicException ex)
        {
            // Typischerweise: settings.json wurde von anderem Windows-User kopiert,
            // oder die Maschine hat den DPAPI-Schluessel verloren. Wir reichen die
            // Exception nach oben durch mit klarer Message – der Aufrufer (UI)
            // kann dem User dann sagen "bitte Tokens neu eingeben".
            throw new CryptographicException(
                "BL-Tokens konnten nicht entschluesselt werden. Sie wurden vermutlich " +
                "auf einem anderen Windows-Benutzer-Konto erzeugt. Bitte Tokens neu eingeben.",
                ex);
        }
        catch (FormatException ex)
        {
            // Base64 ungueltig (sollte nicht vorkommen, ausser settings.json wurde manuell editiert)
            throw new CryptographicException(
                "BL-Tokens-Eintrag ist kein gueltiges Base64. Bitte Tokens neu eingeben.",
                ex);
        }
    }

    public bool HasTokens()
        => !string.IsNullOrWhiteSpace(_settings.Current.Bricklink.TokensEncrypted);

    public async Task ClearAsync()
    {
        _settings.Current.Bricklink.TokensEncrypted = string.Empty;
        await _settings.SaveAsync();
        Log.Information("BL-Tokens geloescht");
    }
}
