using System.Globalization;
using System.Windows.Media;
using HBSort.Core.Services;

namespace HBSort.ViewModels;

/// <summary>
/// Eine erkannte Farbe (Brickognize-/predict/parts/-Response).
/// Enthaelt sowohl die rohen Daten als auch das RGB-Swatch fuer die UI,
/// sowie die ueber ColorMapping aufgeloesten BrickLink-Daten.
/// </summary>
public class ColorMatch
{
    /// <summary>Rebrickable-Color-ID (z.B. 71 = Light Bluish Gray). -1 wenn unbekannt.</summary>
    public int CatalogId { get; init; }

    /// <summary>
    /// BrickLink-Color-ID via ColorMapping. Null wenn keine BL-Entsprechung
    /// existiert (z.B. Modulex-Farben). Wird beim Speichern in
    /// TrackedMinifigPart/FloatingPart uebernommen, damit der BSX-Export
    /// in Phase 7 keine Konvertierungsarbeit hat.
    /// </summary>
    public int? BricklinkId { get; init; }

    /// <summary>BrickLink-Farbname (aus ColorMapping). Null bei fehlendem Mapping.</summary>
    public string? BricklinkName { get; init; }

    /// <summary>Anzeige-Name der Farbe (BrickLink-Name vom Brickognize).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Konfidenz als Prozent-String (z.B. "75 %").</summary>
    public string ScoreText { get; init; } = string.Empty;

    /// <summary>Score numerisch fuer Sortierung/Hervorhebung.</summary>
    public double Score { get; init; }

    /// <summary>Brush fuer das Farbquadrat in der UI – auf Basis des Hex-RGB-Werts aus catalog.db.</summary>
    public Brush SwatchBrush { get; init; } = Brushes.Gray;

    /// <summary>True wenn die Farbe lt. Catalog transparent ist (z.B. Trans-Clear).</summary>
    public bool IsTransparent { get; init; }

    /// <summary>
    /// Vorberechnetes Anzeige-Label im Format
    ///   "Light Bluish Gray (RB:71 / BL:86)"
    /// oder bei fehlendem BL-Mapping
    ///   "Modulex Light Bluish Gray (RB:1014 / kein BL-Mapping)".
    /// Wird direkt im XAML gebunden.
    /// </summary>
    public string DisplayLabel { get; init; } = string.Empty;

    /// <summary>
    /// Erzeugt einen ColorMatch aus den Brickognize-Daten und dem optional aus catalog.db
    /// gelesenen Eintrag.
    ///
    /// Anzeige-Konvention: Wir verwenden ueberall den BrickLink-Namen, weil der User
    /// auf BrickLink verkauft. Brickognize liefert genau diesen Namen schon mit –
    /// die catalog.db (Rebrickable) wird nur fuer das RGB-Swatch und die interne
    /// Rebrickable-Color-ID (fuer Phase-3-Matching) genutzt.
    /// Zusaetzlich loesen wir via ColorMapping die BL-ID auf, damit wir sie spaeter
    /// am Teil persistieren und im BSX-Export wiederverwenden koennen.
    /// </summary>
    public static ColorMatch FromCatalogAndScore(int catalogId, string brickognizeName, double score, Core.Models.CatalogColor? catalog)
    {
        // BL-Lookup ueber die statische Mapping-Tabelle.
        int? blId = null;
        string? blName = null;
        if (catalogId >= 0 && ColorMapping.TryGetBricklinkId(catalogId, out var resolvedBlId))
        {
            blId = resolvedBlId;
            blName = ColorMapping.GetBricklinkColorName(catalogId);
        }

        return new ColorMatch
        {
            CatalogId = catalogId,
            BricklinkId = blId,
            BricklinkName = blName,
            // Anzeigename: bevorzugt der BL-Name aus dem Mapping (gepflegt + konsistent),
            // Fallback der Brickognize-Name (auch BL-Konvention, aber u.U. mit Slash etc.).
            Name = blName ?? brickognizeName,
            Score = score,
            ScoreText = score.ToString("P0", CultureInfo.CurrentCulture),
            SwatchBrush = ParseRgbBrush(catalog?.Rgb),
            IsTransparent = catalog?.IsTransparent ?? false,
            DisplayLabel = BuildDisplayLabel(blName ?? brickognizeName, catalogId, blId)
        };
    }

    /// <summary>
    /// Baut das Anzeige-Label "Name (RB:x / BL:y)".
    /// Bei fehlendem RB-Eintrag: Name (RB:? / BL:y)
    /// Bei fehlendem BL-Mapping: Name (RB:x / kein BL-Mapping)
    /// </summary>
    private static string BuildDisplayLabel(string name, int rebrickableId, int? bricklinkId)
    {
        var rbPart = rebrickableId >= 0
            ? $"RB:{rebrickableId.ToString(CultureInfo.InvariantCulture)}"
            : "RB:?";

        var blPart = bricklinkId.HasValue
            ? $"BL:{bricklinkId.Value.ToString(CultureInfo.InvariantCulture)}"
            : "kein BL-Mapping";

        return $"{name} ({rbPart} / {blPart})";
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
