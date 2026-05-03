using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Navigation;

namespace HBSort.Views;

/// <summary>
/// Code-Behind fuer den Hilfe-Tab. DataContext = HelpViewModel.
///
/// Die einzige Logik hier: Markdown-Links (z.B. https://api.brickognize.com)
/// im Standard-Browser oeffnen statt im FlowDocument zu navigieren. WPF
/// macht das nicht automatisch.
/// </summary>
public partial class HelpView : UserControl
{
    public HelpView()
    {
        InitializeComponent();

        // Markdig.Wpf erzeugt Hyperlinks mit RequestNavigate-Event - wir
        // routen die im OS-Browser. Das CommandBinding wird auf Window-
        // Ebene registriert; der einfachste Weg fuer ein UserControl ist
        // das RequestNavigate-Event direkt am FlowDocumentScrollViewer
        // abzugreifen ueber AddHandler.
        AddHandler(Hyperlink.RequestNavigateEvent,
            new RequestNavigateEventHandler(OnRequestNavigate));
    }

    private static void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            // UseShellExecute=true sorgt dafuer, dass Windows den Default-
            // Browser oeffnet statt eine Exception wegen "Datei ist keine .exe".
            Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true
            });
            e.Handled = true;
        }
        catch
        {
            // Kein Crash wenn der Browser nicht startet - der Link wird
            // einfach ignoriert. (Selten relevant; nur wenn das User-
            // Profil kaputt ist.)
        }
    }
}
