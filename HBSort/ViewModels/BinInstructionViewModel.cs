using CommunityToolkit.Mvvm.ComponentModel;

namespace HBSort.ViewModels;

/// <summary>
/// UX X.31 Block B (v0.1.18): Mini-ViewModel fuer das Anweisungs-Overlay
/// nach erfolgreichem Persist (siehe BinInstructionOverlay.xaml). Lebt
/// als Sub-Property des ScanViewModel und ist bewusst trennbar gehalten,
/// damit das Verhalten ohne den ganzen ScanViewModel-Stack testbar ist.
///
/// Der Auto-Dismiss-Timer (8s) wird NICHT hier verwaltet - das macht das
/// ScanViewModel mit DispatcherTimer (Timer-Logik braucht WPF-Dispatcher,
/// in Tests nicht sinnvoll).
/// </summary>
public partial class BinInstructionViewModel : ObservableObject
{
    /// <summary>True solange das Overlay angezeigt werden soll.</summary>
    [ObservableProperty]
    private bool _isVisible;

    /// <summary>Lagerfach-Label, z.B. "Box 005".</summary>
    [ObservableProperty]
    private string _label = string.Empty;

    /// <summary>URL/Pfad zum Item-Bild (Minifig oder Einzelteil).</summary>
    [ObservableProperty]
    private string? _imageUrl;

    /// <summary>
    /// Macht das Overlay sichtbar mit Bin-Label + Item-Bild. Leeres Label
    /// wird als No-Op behandelt - sonst saehe der User ein "Lege diesen
    /// Eintrag in" ohne konkretes Ziel.
    /// </summary>
    public void Show(string binLabel, string? imageUrl)
    {
        if (string.IsNullOrWhiteSpace(binLabel)) return;
        Label = binLabel;
        ImageUrl = imageUrl;
        IsVisible = true;
    }

    /// <summary>Schliesst das Overlay (manuell durch Klick / Hotkey / Timer).</summary>
    public void Dismiss()
    {
        IsVisible = false;
    }
}
