using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HBSort.Behaviors;

/// <summary>
/// Attached Property + Static-Helper fuer klickbare Bilder mit grosser Vorschau
/// (UX-Iteration X.4).
///
/// Verwendung im XAML:
/// <code>
///   xmlns:b="clr-namespace:HBSort.Behaviors"
///   ...
///   &lt;Image Source="..." b:ImageZoom.IsEnabled="True" /&gt;
/// </code>
///
/// Bei aktiviertem IsEnabled bekommt das Image:
///   - Cursor=Hand auf Hover
///   - MouseLeftButtonUp-Handler der das Bild im Overlay-Layer des
///     enthaltenden Window zeigt (siehe ZoomOverlayHost im MainWindow).
///
/// Schliessen des Overlays passiert per Klick ausserhalb / ESC / X-Button -
/// siehe ZoomOverlayHost. Der Helper hier feuert nur "ShowSource".
/// </summary>
public static class ImageZoom
{
    /// <summary>Attached Property "IsEnabled" fuer Image-Elemente.</summary>
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(ImageZoom),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject d, bool value)
        => d.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject d)
        => (bool)d.GetValue(IsEnabledProperty);

    /// <summary>
    /// Wird aufgerufen sobald sich IsEnabled aendert. Registriert/entfernt
    /// die Click- und Cursor-Logik am Image.
    /// </summary>
    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image img) return;
        if ((bool)e.NewValue)
        {
            img.Cursor = Cursors.Hand;
            img.MouseLeftButtonUp += OnImageClicked;
        }
        else
        {
            img.MouseLeftButtonUp -= OnImageClicked;
            img.ClearValue(FrameworkElement.CursorProperty);
        }
    }

    /// <summary>
    /// Click-Handler: holt die Source des Bildes und delegiert ans Overlay
    /// im umliegenden Window. Macht nichts wenn das Bild keine Source hat
    /// (z.B. noch laed) oder kein Window-Host gefunden wird.
    /// </summary>
    private static void OnImageClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Image img || img.Source == null) return;

        var window = Window.GetWindow(img);
        if (window == null) return;

        // Suche ein "ZoomOverlayHost"-benanntes Element im Window. Das ist das
        // ContentControl im MainWindow das die Overlay-Anzeige uebernimmt.
        var overlay = FindOverlay(window);
        overlay?.Show(img.Source);

        e.Handled = true;
    }

    /// <summary>
    /// Sucht im Visual-Tree des Windows nach einem ZoomOverlayHost.
    /// Cached den Treffer pro Window NICHT - der WPF-VisualTree ist klein
    /// genug und ein Fenster wechselt sein Overlay-Element nicht.
    /// </summary>
    private static ZoomOverlayHost? FindOverlay(DependencyObject root)
    {
        if (root is ZoomOverlayHost h) return h;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var found = FindOverlay(VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }
}
