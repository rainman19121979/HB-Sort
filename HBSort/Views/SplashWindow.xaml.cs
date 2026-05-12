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
    /// v0.1.22-beta.1 Block A: Status-Text auf dem Splash aktualisieren.
    /// Wird von Application_Startup an den Init-Schritt-Grenzen aufgerufen,
    /// damit der User sieht dass "irgendwas laeuft".
    ///
    /// 2026-05-12 Fix: vorher nur Property-Set ohne Render-Flush - WPF
    /// rendert erst beim naechsten Layout-Pass, aber die Init-Schritte
    /// laufen schneller. Dispatcher.Invoke mit Render-Priority erzwingt
    /// den Render-Pass synchron bevor der Aufrufer weiterlaeuft, sodass
    /// die 6 Status-Wechsel sichtbar werden.
    /// </summary>
    public void SetStatus(string text)
    {
        Dispatcher.Invoke(() =>
        {
            StatusLabel.Text = text;
        }, System.Windows.Threading.DispatcherPriority.Render);
    }

    /// <summary>
    /// UX X.28 (v0.1.15): bevorzugt InformationalVersion damit ein "-dev"-
    /// Suffix fuer lokale Builds sichtbar wird. Im Tag-Release setzt die
    /// Pipeline -p:InformationalVersion=X.Y.Z (ohne Suffix), bei lokalem
    /// Visual-Studio-Build bleibt "0.1.15-dev" aus der csproj.
    /// Fallback auf AssemblyVersion (Major.Minor.Patch) wenn
    /// InformationalVersion leer ist.
    /// </summary>
    private static string GetVersionString()
    {
        var asm = Assembly.GetExecutingAssembly();
        var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // GitVersion-/MSBuild-Suffixe wie "+abc1234"-Commit-Hash abschneiden,
            // damit der Splash nicht ueberlaeuft. "-dev"-Suffix bleibt erhalten.
            var plusIdx = informational.IndexOf('+');
            return plusIdx > 0 ? informational[..plusIdx] : informational;
        }

        var version = asm.GetName().Version;
        if (version is null) return "0.0.0";
        return $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
