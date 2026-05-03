using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HBSort.Behaviors;

/// <summary>
/// Code-Behind fuer den ZoomOverlayHost. Wird vom ImageZoom-Helper aufgerufen
/// (siehe ImageZoom.cs::Show), zeigt das uebergebene Bild gross an und sorgt
/// dafuer dass Klick ausserhalb / ESC / X-Button das Overlay wieder schliesst.
///
/// "Klick ausserhalb des Bildes" wird so erkannt: der schwarze Hintergrund-Grid
/// hat einen MouseLeftButtonUp-Handler der schliesst; alles auf dem Bild oder
/// dem inneren Border ruft einen anderen Handler auf der das Event als handled
/// markiert (so wird das Schliessen unterdrueckt).
/// </summary>
public partial class ZoomOverlayHost : UserControl
{
    public ZoomOverlayHost()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Vom ImageZoom-Helper aufgerufen. Setzt die Source und macht das
    /// Overlay sichtbar. Fokus wandert aufs Overlay damit ESC funktioniert.
    /// </summary>
    public void Show(ImageSource source)
    {
        ZoomedImage.Source = source;
        Visibility = Visibility.Visible;
        Focus();      // damit OnKeyDown ausgeloest wird
    }

    /// <summary>Overlay schliessen + Source freigeben (kein Memory-Hold).</summary>
    private void Hide()
    {
        Visibility = Visibility.Collapsed;
        ZoomedImage.Source = null;
    }

    /// <summary>Klick auf den dunklen Hintergrund -> schliessen.</summary>
    private void Background_MouseUp(object sender, MouseButtonEventArgs e)
    {
        Hide();
        e.Handled = true;
    }

    /// <summary>
    /// Klick auf den inneren Border oder das Bild selbst: NICHT schliessen.
    /// Wir markieren das Event als handled damit der Background-Handler
    /// nicht auch noch greift (Event-Bubbling).
    /// </summary>
    private void Border_MouseUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    /// <summary>X-Button.</summary>
    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    /// <summary>ESC-Taste schliesst das Overlay.</summary>
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
    }
}
