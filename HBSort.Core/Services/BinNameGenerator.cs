using System.Globalization;

namespace HBSort.Core.Services;

/// <summary>Sequenz-Typ fuer Bulk-Create-Dialog: numerisch oder alphabetisch.</summary>
public enum BinSequenceType
{
    Numbers,
    Letters
}

/// <summary>
/// Erzeugt Lagerfach-Labels in Bulk. Beispiele:
///   Generate("Box ", "", Numbers, "1", "10", padding: 3)
///     -> ["Box 001", "Box 002", ..., "Box 010"]
///   Generate("Schale ", "", Letters, "A", "C", 0)
///     -> ["Schale A", "Schale B", "Schale C"]
///   Generate("", "-rot", Letters, "Z", "AB", 0)
///     -> ["Z-rot", "AA-rot", "AB-rot"]
///
/// Statisch, keine Dependencies – einfach testbar.
/// </summary>
public static class BinNameGenerator
{
    /// <summary>
    /// Erzeugt die Liste aller Labels zwischen <paramref name="startStr"/> und
    /// <paramref name="endStr"/> (inklusive), eingerahmt von Prefix und Suffix.
    /// Bei <see cref="BinSequenceType.Numbers"/> wird <paramref name="padding"/>
    /// als minimale Stellenzahl verwendet (0 = keine fuehrenden Nullen).
    /// </summary>
    public static List<string> Generate(
        string prefix,
        string suffix,
        BinSequenceType type,
        string startStr,
        string endStr,
        int padding = 0)
    {
        var result = new List<string>();
        prefix ??= string.Empty;
        suffix ??= string.Empty;

        if (type == BinSequenceType.Numbers)
        {
            if (!int.TryParse(startStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var start))
                throw new ArgumentException("Start-Wert ist keine gueltige Zahl.", nameof(startStr));
            if (!int.TryParse(endStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var end))
                throw new ArgumentException("Ende-Wert ist keine gueltige Zahl.", nameof(endStr));
            if (end < start)
                throw new ArgumentException("Ende-Wert muss >= Start-Wert sein.");

            var format = padding > 0 ? "D" + padding : "D";
            for (int i = start; i <= end; i++)
            {
                result.Add(prefix + i.ToString(format, CultureInfo.InvariantCulture) + suffix);
            }
        }
        else // Letters
        {
            if (string.IsNullOrEmpty(startStr) || !IsValidAlpha(startStr))
                throw new ArgumentException("Start-Wert muss aus A-Z bestehen.", nameof(startStr));
            if (string.IsNullOrEmpty(endStr) || !IsValidAlpha(endStr))
                throw new ArgumentException("Ende-Wert muss aus A-Z bestehen.", nameof(endStr));
            if (CompareAlpha(startStr, endStr) > 0)
                throw new ArgumentException("Ende-Wert muss >= Start-Wert sein.");

            var current = startStr.ToUpperInvariant();
            var endUpper = endStr.ToUpperInvariant();
            // Schutz gegen versehentliche Endlosschleife (z.B. AAA-ZZZ = 17576 Schritte)
            int safety = 100_000;
            while (CompareAlpha(current, endUpper) <= 0 && safety-- > 0)
            {
                result.Add(prefix + current + suffix);
                if (current == endUpper) break;
                current = IncrementAlpha(current);
            }
        }

        return result;
    }

    /// <summary>
    /// Naechster Buchstabe in der Sequenz: "A" → "B", "Z" → "AA",
    /// "AZ" → "BA", "ZZ" → "AAA". Eingabe wird vorher upper-case gemacht.
    /// </summary>
    public static string IncrementAlpha(string s)
    {
        if (string.IsNullOrEmpty(s)) return "A";

        var chars = s.ToUpperInvariant().ToCharArray();
        for (int i = chars.Length - 1; i >= 0; i--)
        {
            if (chars[i] < 'Z')
            {
                chars[i]++;
                return new string(chars);
            }
            // Uebertrag: aktuelle Stelle wird 'A', vorige inkrementieren
            chars[i] = 'A';
        }
        // Alle Stellen waren 'Z' -> eine Stelle mehr
        return "A" + new string(chars);
    }

    /// <summary>
    /// Anzahl der Schritte zwischen Start und End (inklusive beider).
    /// "A"-"C" = 3, "Z"-"AB" = 3, "A"-"A" = 1.
    /// Wird fuer die Vorschau-Anzeige im Dialog gebraucht.
    /// </summary>
    public static int CountAlpha(string startStr, string endStr)
    {
        if (string.IsNullOrEmpty(startStr) || string.IsNullOrEmpty(endStr)) return 0;
        if (!IsValidAlpha(startStr) || !IsValidAlpha(endStr)) return 0;
        if (CompareAlpha(startStr, endStr) > 0) return 0;

        var current = startStr.ToUpperInvariant();
        var endUpper = endStr.ToUpperInvariant();
        int count = 0;
        int safety = 100_000;
        while (CompareAlpha(current, endUpper) <= 0 && safety-- > 0)
        {
            count++;
            if (current == endUpper) break;
            current = IncrementAlpha(current);
        }
        return count;
    }

    /// <summary>True wenn der String nur aus A-Z (case-insensitive) besteht.</summary>
    public static bool IsValidAlpha(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var c in s)
        {
            if (!char.IsLetter(c)) return false;
            if (c > 'z') return false;
        }
        return true;
    }

    /// <summary>
    /// Vergleicht zwei alphabetische Sequenz-Strings: kuerzer ist immer kleiner
    /// (weil "Z" &lt; "AA"); bei gleicher Laenge lexikografisch.
    /// </summary>
    public static int CompareAlpha(string a, string b)
    {
        var ua = a.ToUpperInvariant();
        var ub = b.ToUpperInvariant();
        if (ua.Length != ub.Length) return ua.Length.CompareTo(ub.Length);
        return string.CompareOrdinal(ua, ub);
    }
}
