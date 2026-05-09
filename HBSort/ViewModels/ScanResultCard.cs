using CommunityToolkit.Mvvm.ComponentModel;
using HBSort.Core.Models;

namespace HBSort.ViewModels;

/// <summary>
/// Ein Treffer der Top-3-Karten unter dem Webcam-Bild.
/// Beinhaltet die fuer die UI-Anzeige aufbereiteten Daten:
/// Bild, Name, Score, Typ, sowie die rohen IDs fuer den naechsten Workflow-Schritt.
///
/// Audit W-11 (2026-05-04): Klasse hat keine WPF-Brush-Properties mehr.
/// Visuelle Marker (Top-Treffer, Selected) sind reine bool-Flags; die XAML-
/// View setzt darauf die Brushes via DataTrigger. So bleibt das ViewModel
/// frei von System.Windows.Media-Imports.
/// </summary>
public partial class ScanResultCard : ObservableObject
{
    /// <summary>Position in der Treffer-Liste (1-3, fuer Anzeige).</summary>
    public int Rank { get; init; }

    /// <summary>Brickognize-Item-Name, z.B. "Stormtrooper".</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Score 0..1, vorberechnet als Prozent-String z.B. "92 %".</summary>
    public string ScoreText { get; init; } = string.Empty;

    /// <summary>Score numerisch (zur Hervorhebung des Top-Treffers).</summary>
    public double Score { get; init; }

    /// <summary>Typ-Label fuer die UI ("Minifigur", "Teil", "Set", "Sticker").</summary>
    public string TypeLabel { get; init; } = string.Empty;

    /// <summary>
    /// URL zum Vorschaubild. Wird ueber den IPartImageProvider aufgeloest -
    /// bevorzugt BrickLink-Foto (farbig), Fallback auf Brickognize-Render.
    /// Observable damit das Bild nach der Farb-Identifikation austauschen koennen
    /// (Erst-Anzeige ohne Farbe, dann farbiges BL-Bild wenn die Farbe erkannt wurde).
    /// </summary>
    [ObservableProperty]
    private string? _imageUrl;

    /// <summary>Aufgeloeste externe IDs (BL, RB, BO).</summary>
    public ExternalIds Ids { get; init; } = new(null, null, null);

    /// <summary>Roher BrickognizeItem zum Spaeter-Verarbeiten.</summary>
    public BrickognizeItem? RawItem { get; init; }

    /// <summary>
    /// True wenn diese Karte der Top-Treffer mit Score &gt;= ScoreThresholdAuto
    /// ist und damit auto-akzeptiert wurde. XAML verbindet das per DataTrigger
    /// mit gruenem 3px-Border + Highlight-Label.
    /// </summary>
    [ObservableProperty]
    private bool _isTopHit;

    /// <summary>Hervorhebungs-Hinweis bei Auto-Akzept ("Top-Treffer (auto-akzeptiert)").</summary>
    [ObservableProperty]
    private string _highlightLabel = string.Empty;

    /// <summary>
    /// True wenn diese Karte aktuell ausgewaehlt ist (Auto-Akzept oder Click).
    /// XAML verbindet das per DataTrigger mit gruenem 3px-Border.
    /// </summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// UX X.30 (v0.1.17) Block D: Tastatur-Hotkey-Label - intern fuer Tests
    /// und ggf. ToolTip-Texte. Im UI selbst wird der Hotkey via
    /// <see cref="UebernehmenButtonLabel"/> direkt im Button-Content gezeigt.
    /// </summary>
    public string HotkeyLabel => Rank switch
    {
        1 => "[1]",
        2 => "[2]",
        3 => "[3]",
        _ => string.Empty
    };

    /// <summary>
    /// UX X.30 Block D Bug-Fix (v0.1.17): Button-Content fuer den
    /// "Uebernehmen"-Button mit vorangestelltem Hotkey-Label. Saubere
    /// Variante statt eines schwarzen Badges oben links - der User sieht
    /// den Hotkey genau dort wo er klicken wuerde.
    /// </summary>
    public string UebernehmenButtonLabel => string.IsNullOrEmpty(HotkeyLabel)
        ? "Uebernehmen"
        : $"{HotkeyLabel} Uebernehmen";
}
