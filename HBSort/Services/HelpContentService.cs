using System.IO;
using System.Text.Json;
using System.Windows;
using HBSort.Models;
using Serilog;

namespace HBSort.Services;

/// <summary>
/// Implementierung des HelpContentService.
///
/// Inhalte sind als WPF-Resource eingebunden (Build-Action "Resource") und
/// werden ueber pack://application:,,,/Resources/Help/... gelesen. Damit
/// kann Markdig.Wpf relativ referenzierte Bilder ohne Extra-Mapping aufloesen.
///
/// Wir laden index.json einmal beim Konstruktor und cachen die Kapitelliste -
/// die Datei aendert sich zur Laufzeit nicht.
/// </summary>
public class HelpContentService : IHelpContentService
{
    private readonly List<HelpChapter> _chapters;

    public HelpContentService()
    {
        _chapters = LoadIndexSafe();
    }

    public IReadOnlyList<HelpChapter> GetChapters() => _chapters;

    public string LoadMarkdown(HelpChapter chapter)
    {
        try
        {
            var uri = BuildResourceUri(chapter.FileName);
            var info = Application.GetResourceStream(uri);
            if (info == null)
            {
                Log.Warning("Hilfe-Resource nicht gefunden: {File}", chapter.FileName);
                return BuildErrorMarkdown(
                    $"Resource **{chapter.FileName}** nicht gefunden.");
            }

            using var reader = new StreamReader(info.Stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Hilfe-Resource konnte nicht gelesen werden: {File}", chapter.FileName);
            return BuildErrorMarkdown(
                $"Fehler beim Laden von **{chapter.FileName}**: {ex.Message}");
        }
    }

    /// <summary>
    /// Liest und deserialisiert index.json. Bei Fehler: leere Liste +
    /// Log-Eintrag, die UI zeigt dann einen Hinweis statt einen Crash.
    /// </summary>
    private static List<HelpChapter> LoadIndexSafe()
    {
        try
        {
            var uri = BuildResourceUri("index.json");
            var info = Application.GetResourceStream(uri);
            if (info == null)
            {
                Log.Warning("Hilfe-Index (index.json) nicht gefunden");
                return new List<HelpChapter>();
            }

            using var reader = new StreamReader(info.Stream);
            var json = reader.ReadToEnd();

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            var chapters = JsonSerializer.Deserialize<List<HelpChapter>>(json, options)
                           ?? new List<HelpChapter>();

            // Sortierung: Order aufsteigend, bei Gleichstand Title.
            return chapters
                .OrderBy(c => c.Order)
                .ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Hilfe-Index konnte nicht geladen werden");
            return new List<HelpChapter>();
        }
    }

    private static Uri BuildResourceUri(string fileName)
        => new($"pack://application:,,,/Resources/Help/{fileName}", UriKind.Absolute);

    private static string BuildErrorMarkdown(string message) =>
        $"# Hilfe-Inhalt nicht verfuegbar\n\n{message}\n\n" +
        "Bitte pruefe die Installation oder melde den Fehler.";
}
