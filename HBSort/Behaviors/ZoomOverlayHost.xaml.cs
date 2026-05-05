using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HBSort.Behaviors;

/// <summary>
/// Code-Behind fuer den ZoomOverlayHost. Wird vom ImageZoom-Helper aufgerufen
/// (siehe ImageZoom.cs::Show), zeigt das uebergebene Bild gross an und sorgt
/// dafuer dass Klick irgendwo im Popup oder ESC das Overlay wieder schliesst.
///
/// UX X.20 Teil 3: Klick IRGENDWO im Popup-Bereich schliesst (vorher: nur
/// dunkler Hintergrund + ESC; Bild-Klick war No-op). Der innere Border-Handler
/// wurde entfernt; Klick auf Bild oder Border bubblet jetzt zum Hintergrund-
/// Grid und schliesst dort.
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

    /// <summary>
    /// UX X.20 Teil 3: Klick IRGENDWO im Overlay schliesst. Da der innere
    /// Border und das Bild selbst keinen eigenen MouseLeftButtonUp-Handler
    /// mehr haben, bubblet jeder Klick zum Hintergrund-Grid und landet hier.
    /// </summary>
    private void Background_MouseUp(object sender, MouseButtonEventArgs e)
    {
        Hide();
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
