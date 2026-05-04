using System.Globalization;
using System.Windows.Media;
using HBSort.Core.Models.Bricklink;

namespace HBSort.ViewModels;

/// <summary>
/// Eine erkannte Farbe (Brickognize-/predict/parts/-Response).
/// Enthaelt sowohl die rohen Daten als auch das RGB-Swatch fuer die UI.
/// Brickognize liefert in der "id"-Spalte BL-Color-IDs direkt - RGB kommt
/// direkt aus bl_colors, kein RB->BL-Mapping noetig.
/// </summary>
public class ColorMatch
{
    /// <summary>
    /// BrickLink-Color-ID (z.B. 86 = Light Bluish Gray). Null wenn Brickognize
    /// keine ID lieferte oder die ID ungueltig war.
    /// </summary>
    public int? BricklinkId { get; init; }

    /// <summary>Anzeige-Name der Farbe (BrickLink-Name aus bl_colors oder Brickognize-Antwort).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Konfidenz als Prozent-String (z.B. "75 %").</summary>
    public string ScoreText { get; init; } = string.Empty;

    /// <summary>Score numerisch fuer Sortierung/Hervorhebung.</summary>
    public double Score { get; init; }

    /// <summary>Brush fuer das Farbquadrat in der UI - auf Basis des Hex-RGB-Werts aus bl_colors.</summary>
    public Brush SwatchBrush { get; init; } = Brushes.Gray;

    /// <summary>True wenn die Farbe lt. BL-Type "Transparent" ist.</summary>
    public bool IsTransparent { get; init; }

    /// <summary>
    /// Vorberechnetes Anzeige-Label im Format "Light Bluish Gray (BL:86)"
    /// oder "Unbekannte Farbe (BL:?)" wenn keine Color-ID erkannt.
    /// </summary>
    public string DisplayLabel { get; init; } = string.Empty;

    /// <summary>
    /// Erzeugt einen ColorMatch aus den Brickognize-Daten und dem optional aus bl_colors
    /// gelesenen Eintrag. Wird mit der direkt von Brickognize gelieferten BL-Color-ID
    /// (Spalte "id") aufgerufen.
    /// </summary>
    public static ColorMatch FromBlColor(int? blColorId, string brickognizeName, double score, BlColor? blColor)
    {
        var name = !string.IsNullOrWhiteSpace(blColor?.Name)
            ? blColor!.Name
            : brickognizeName;

        return new ColorMatch
        {
            BricklinkId = blColorId,
            Name = name,
            Score = score,
            ScoreText = score.ToString("P0", CultureInfo.CurrentCulture),
            SwatchBrush = ParseRgbBrush(blColor?.Rgb),
            IsTransparent = string.Equals(blColor?.Type, "Transparent", System.StringComparison.OrdinalIgnoreCase),
            DisplayLabel = BuildDisplayLabel(name, blColorId)
        };
    }

    /// <summary>
    /// Baut das Anzeige-Label "Name (BL:x)".
    /// Bei fehlender BL-ID: "Name (BL:?)"
    /// </summary>
    private static string BuildDisplayLabel(string name, int? bricklinkId)
    {
        var blPart = bricklinkId.HasValue
            ? $"BL:{bricklinkId.Value.ToString(CultureInfo.InvariantCulture)}"
            : "BL:?";

        return $"{name} ({blPart})";
    }

    /// <summary>
    /// Parsed einen Hex-RGB-Wert (z.B. "FCFCFC" oder "#FFFFFF") in einen SolidColorBrush.
    /// Bei Fehler oder null: graues Quadrat.
    /// </summary>
    private static Brush ParseRgbBrush(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return Brushes.Gray;

        // Optionales "#" entfernen
        var clean = hex.TrimStart('#');
        if (clean.Length != 6) return Brushes.Gray;

        try
        {
            var r = byte.Parse(clean.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var g = byte.Parse(clean.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var b = byte.Parse(clean.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
        catch
        {
            return Brushes.Gray;
        }
    }
}
