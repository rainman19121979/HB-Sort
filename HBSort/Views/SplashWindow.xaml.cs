using System.Reflection;
using System.Windows;

namespace HBSort.Views;

/// <summary>
/// Splash-Screen beim App-Start (UX-Iteration X.23).
///
/// Wird in App.xaml.cs::Application_Startup als allererstes Window
/// erstellt und gezeigt, bevor die langwierigen Init-Schritte (DI,
/// Settings, DB-Migration, BL-Cache) laufen. Nach mind. 800ms +
/// MainWindow ready wird er geschlossen.
///
/// Versions-Anzeige liest die FileVersion aus dem aktuellen Assembly,
/// damit eine spaetere Version-Aenderung in HBSort.csproj automatisch
/// im Splash erscheint.
/// </summary>
public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        VersionLabel.Text = "v" + GetVersionString();
    }

    /// <summary>
    /// Liefert die Version aus dem aktuellen Assembly im Format "Major.Minor.Patch".
    /// Fallback "0.0.0" wenn die Assembly keine Version hat (sollte nicht passieren,
    /// aber defensiv).
    /// </summary>
    private static string GetVersionString()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        if (version is null) return "0.0.0";
        return $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
