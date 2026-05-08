using Serilog;

namespace HBSort.Core.Helpers;

/// <summary>
/// UX X.28 (v0.1.15): zentrale Helfer fuer Fire-and-Forget-Tasks.
///
/// Hintergrund (Code-Analyse-Bericht): an mehreren Stellen wurden async
/// Methoden mit `_ = SomeAsync()` aufgerufen, ohne dass eine Exception
/// abgefangen wurde - bei einem Fehler ging der ganz still verloren
/// (TaskScheduler.UnobservedTaskException sieht das, aber WPF-Default ist
/// abgewuergt).
///
/// Mit `SomeAsync().FireAndForget("Operation-Label")` wird die Task
/// fortgesetzt und im Fehlerfall ein Warning-Log mit Operation-Label
/// + Exception-Stacktrace geschrieben.
///
/// Verwendung NUR bei Operationen die wirklich nicht erwarten dass jemand
/// auf das Resultat wartet (Bild-Preload, Cache-Refresh, Background-
/// Statistik). Bei kritischen Pfaden (Save, Persist, etc.) eigene
/// Try/Catch verwenden statt Fire-and-Forget.
/// </summary>
public static class FireAndForgetExtensions
{
    /// <summary>
    /// Fuegt der Task einen Continuation-Handler hinzu der bei Fehlern
    /// ein Log.Warning schreibt. Operation-Name landet im Log-Eintrag damit
    /// man im Logfile sieht welche Background-Task fehlgeschlagen ist.
    /// </summary>
    public static void FireAndForget(this Task task, string operationName)
    {
        if (task is null) return;
        _ = task.ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception is not null)
            {
                Log.Warning(t.Exception, "Background-Operation '{Op}' fehlgeschlagen", operationName);
            }
        }, TaskScheduler.Default);
    }
}
