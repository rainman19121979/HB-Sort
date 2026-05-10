using HBSort.Core.Models;
using Serilog;

namespace HBSort.Core.Services;

/// <summary>
/// UX X.33 v0.1.19-beta.7 Block M: Implementation siehe Interface-Doku.
/// Liest und schreibt direkt auf <see cref="AppSettings.CategoryToBinMapping"/>
/// und <see cref="AppSettings.SeenBrickognizeCategories"/>. Bin-Existenz wird
/// gegen den <see cref="IStorageBinService"/> verifiziert - so faellt das
/// Mapping automatisch zurueck wenn ein Bin geloescht wurde.
/// </summary>
public class CategoryBinMappingService : ICategoryBinMappingService
{
    private readonly ISettingsService _settings;
    private readonly IStorageBinService _binService;

    public CategoryBinMappingService(
        ISettingsService settings,
        IStorageBinService binService)
    {
        _settings = settings;
        _binService = binService;
    }

    public async Task<StorageBin?> ResolveBinForCategoryAsync(
        string? brickognizeCategory,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(brickognizeCategory))
            return null;

        var mapping = _settings.Current.CategoryToBinMapping;
        if (!mapping.TryGetValue(brickognizeCategory, out var binId))
            return null;

        var bin = await _binService.GetByIdAsync(binId, ct);
        if (bin == null)
        {
            // Bin geloescht oder ID-Wechsel - Mapping ist stale, faellt
            // zurueck. Nicht selber aufraeumen damit User ggf. eine andere
            // ID setzen kann ohne Zwischen-Save.
            Log.Information(
                "Category-Mapping fuer '{Cat}' zeigt auf gelescht Bin Id={Id} - Fallback auf Suggest-Pfad",
                brickognizeCategory, binId);
            return null;
        }

        return bin;
    }

    public async Task<StorageBin?> ResolveBinForPartNameAsync(
        string? partName,
        CancellationToken ct = default)
    {
        // UX X.33 Block N: Praefix-Match-Pfad geht ueber die gemeinsame
        // Heuristik damit Wizard-Pfad und direkter Lookup exakt den gleichen
        // Kategorie-String benutzen. Pre-Tag-Fix: Heuristik liefert jetzt
        // immer einen String ("Unbekannt"-Fallback), aber fuer "Unbekannt"
        // existiert in der Regel kein User-Mapping - dann faellt
        // ResolveBinForCategoryAsync sauber auf null zurueck.
        var category = DeriveCategoryFromPartName(partName);
        return await ResolveBinForCategoryAsync(category, ct);
    }

    public string DeriveCategoryFromPartName(string? partName)
    {
        // Pre-Tag-Fix (v0.1.19-beta.7): liefert jetzt IMMER einen String
        // (vorher string?). "Unbekannt" wird zur Pseudo-Kategorie wenn
        // kein Praefix-Match gefunden wird oder der Name leer ist.
        // Damit ist BrickognizeCategory in der DB nie null und die
        // Default-Regel "max 1 PartId pro Kategorie pro Bin" greift auch
        // fuer ungelabelte Teile.
        if (string.IsNullOrWhiteSpace(partName))
            return ICategoryBinMappingService.UnknownCategory;

        var seen = _settings.Current.SeenBrickognizeCategories;
        if (seen.Count == 0)
            return ICategoryBinMappingService.UnknownCategory;

        // Laengster-Praefix-Match: BL-Part-Names beginnen typischerweise
        // mit dem Brickognize-Category-String. "Minifigure, Headgear ..."
        // matcht "Minifigure, Headgear" und nicht "Minifigure, Head".
        string? bestKey = null;
        foreach (var key in seen)
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            if (!partName.StartsWith(key, StringComparison.OrdinalIgnoreCase)) continue;
            if (bestKey == null || key.Length > bestKey.Length)
                bestKey = key;
        }
        return bestKey ?? ICategoryBinMappingService.UnknownCategory;
    }

    public async Task RememberSeenCategoryAsync(string brickognizeCategory, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(brickognizeCategory)) return;

        var seen = _settings.Current.SeenBrickognizeCategories;
        if (seen.Contains(brickognizeCategory, StringComparer.OrdinalIgnoreCase))
            return;

        seen.Add(brickognizeCategory);
        // Sortiert speichern damit das Settings-UI eine stabile Reihenfolge
        // zeigt - sonst springt die Liste bei jedem neuen Eintrag.
        seen.Sort(StringComparer.OrdinalIgnoreCase);
        await _settings.SaveAsync();
    }

    // UX X.33 Block N (v0.1.19-beta.7 Refactor): UpdateMappingAsync entfernt.
    // Settings-UI schreibt direkt in AppSettings.CategoryToBinMapping beim
    // Save (siehe SettingsViewModel.SaveSettingsAsync). Auto-Mapping beim
    // Lagern ist raus.
}
