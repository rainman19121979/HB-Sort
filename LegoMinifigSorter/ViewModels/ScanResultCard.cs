using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using LegoMinifigSorter.Core.Models;

namespace LegoMinifigSorter.ViewModels;

/// <summary>
/// Ein Treffer der Top-3-Karten unter dem Webcam-Bild.
/// Beinhaltet die fuer die UI-Anzeige aufbereiteten Daten:
/// Bild, Name, Score, Typ, sowie die rohen IDs fuer den naechsten Workflow-Schritt.
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
    /// URL zum Vorschaubild. Wird ueber den IPartImageProvider aufgeloest –
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

    /// <summary>Rahmenfarbe der Karte – wird bei Top-Treffer (Score &gt;= Auto) gruen.</summary>
    [ObservableProperty]
    private Brush _borderBrush = Brushes.Gray;

    /// <summary>Rahmen-Dicke – Top-Treffer wird dicker dargestellt.</summary>
    [ObservableProperty]
    private double _borderThickness = 1;

    /// <summary>Hervorhebungs-Hinweis bei Auto-Akzept ("Top-Treffer").</summary>
    [ObservableProperty]
    private string _highlightLabel = string.Empty;

    /// <summary>
    /// True wenn diese Karte aktuell ausgewaehlt ist (Auto-Akzept oder Click).
    /// Wird vom XAML als Selected-Indicator (gruener Border) genutzt.
    /// </summary>
    [ObservableProperty]
    private bool _isSelected;
}
