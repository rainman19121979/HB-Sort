namespace HBSort.Core.Services;

/// <summary>
/// UX X.32 v0.1.19-beta.4 (User-Befund): Heuristische Kategorie-
/// Bestimmung fuer BL-Teile. Ergebnis ist ein String-Schluessel, der
/// gruppiert: zwei Teile mit gleichem Schluessel landen ins selbe Bin.
///
/// Drei Strategien (in Reihenfolge):
///   1. PartName-Praefix bis zum ersten Komma (BL-Naming-Konvention,
///      sehr zuverlaessig): "Minifig Head, Female..." → "minifig head".
///      Alle Kopf-Varianten haben den gleichen Schluessel.
///   2. PartNo-Praefix (numerisch) mit Aliasen fuer bekannte Varianten:
///      "3815c01" / "3816" / "3817" alle → "num:3815" (Bein-Varianten).
///   3. Sentinel "unknown" - mehrere unerkannte Teile zaehlen zueinander
///      als gleiche Kategorie (kein Fach pro unbekanntem Item blockieren).
/// </summary>
public static class BlPartCategoryHeuristic
{
    /// <summary>Sentinel fuer "keine Kategorie ableitbar" - alle ohne Match
    /// landen im selben Bin.</summary>
    public const string UnknownCategory = "unknown";

    /// <summary>
    /// BL-Body-Part-Whitelist. BL-Naming-Konvention ist
    /// "Minifigure, &lt;BodyPart&gt; ..." (z.B. "Minifigure, Head Beard
    /// Brown Angular...", "Minifigure, Headgear Helmet Motorcycle..."
    /// "Minifigure, Hips and Legs Plain"). Das &lt;BodyPart&gt; ist der
    /// Kategorie-Schluessel.
    ///
    /// Reihenfolge: laengere Begriffe ZUERST, damit z.B. "hips and legs"
    /// nicht faelschlich als "hips" matcht und "headgear" nicht als
    /// "head".
    /// </summary>
    private static readonly string[] BodyPartKeywords = new[]
    {
        "hips and legs",
        "headgear",
        "body part",
        "head",
        "hair",
        "hat",
        "torso",
        "hips",
        "legs",
        "arms",
        "hands",
        "beard",
        "cape",
        "tool",
        "shield",
        "weapon",
        "neckwear",
        "footwear",
    };

    /// <summary>
    /// Strategie 1: BL-Body-Part-Whitelist. Akzeptiert sowohl "Minifigure,
    /// {BodyPart} ..." (Standard-BL-Naming) als auch "{BodyPart} ..."
    /// (kuerzere Schreibweise wie "Hips and Legs Plain"). Beide Varianten
    /// liefern den gleichen Kategorie-Schluessel.
    ///
    /// User-Befund (Box19-16): "Minifigure, Headgear Helmet Motorcycle"
    /// und "Minifigure, Headgear Helmet Space" haben beide den
    /// Body-Part "Headgear" - sollen also als gleiche Kategorie erkannt
    /// werden, auch wenn die PartNos vollkommen verschieden sind
    /// (2446 vs 193a2).
    /// </summary>
    public static string? GetNameCategoryKey(string? partName)
    {
        if (string.IsNullOrWhiteSpace(partName)) return null;
        var name = partName.Trim();

        // BL-Praefix "Minifigure," (mit oder ohne Komma direkt dahinter)
        // entfernen damit der Body-Part-Match an der richtigen Stelle ansetzt.
        const string minifigurePrefix = "Minifigure,";
        var search = name;
        if (search.StartsWith(minifigurePrefix, StringComparison.OrdinalIgnoreCase))
        {
            search = search[minifigurePrefix.Length..].TrimStart();
        }

        // Auch Variante "Minifig " (ohne 'ure,') akzeptieren.
        const string minifigPrefix = "Minifig ";
        if (search.StartsWith(minifigPrefix, StringComparison.OrdinalIgnoreCase))
        {
            search = search[minifigPrefix.Length..].TrimStart();
        }

        var lower = search.ToLowerInvariant();
        foreach (var kw in BodyPartKeywords)
        {
            if (lower.StartsWith(kw, StringComparison.Ordinal))
            {
                // Wort-Grenze pruefen damit z.B. "hat" nicht in "hatchet"
                // matcht - naechstes Zeichen muss Nicht-Buchstabe sein
                // oder das Keyword reicht ans Stringende.
                if (kw.Length == lower.Length || !char.IsLetter(lower[kw.Length]))
                {
                    return "name:" + kw;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Strategie 2: numerischer Praefix der BL-Part-No mit Aliasen.
    /// </summary>
    public static string GetPrefixCategoryKey(string? partNo)
    {
        if (string.IsNullOrWhiteSpace(partNo)) return UnknownCategory;

        int i = 0;
        while (i < partNo.Length && char.IsDigit(partNo[i])) i++;
        if (i == 0) return UnknownCategory;

        if (!int.TryParse(partNo.AsSpan(0, i), out var num))
            return UnknownCategory;

        // Bekannte Varianten-Aliase (gleiche User-Kategorie).
        var aliased = num switch
        {
            970 or 971           => 970,  // Hips+Legs (970c00 etc.)
            3815 or 3816 or 3817 => 3815, // Bein-Varianten
            3818 or 3819         => 3818, // Arm-Varianten
            982  or 983          => 982,  // Hand-Varianten
            _ => num
        };
        return "num:" + aliased.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Bestimmt den Kategorie-Schluessel mit der besten verfuegbaren
    /// Strategie (Name -> PartNo-Praefix -> "unknown").
    ///
    /// <see cref="ColorId"/> wird IMMER an den Schluessel gehaengt: zwei
    /// Teile gleicher Kategorie aber unterschiedlicher Farbe (z.B. ein
    /// Black-Brick und ein Red-Brick) sollen NICHT im selben Kategorie-
    /// Stapel-Bin landen. Step 1 in <c>SuggestBinForFloatingPartAsync</c>
    /// macht ohnehin schon den exakten PartNo+ColorId-Match.
    /// </summary>
    public static string Resolve(string? partNo, int colorId, string? partName)
    {
        var nameKey = GetNameCategoryKey(partName);
        var baseKey = nameKey ?? GetPrefixCategoryKey(partNo);
        return baseKey + "/c" + colorId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
