using HBSort.Models;

namespace HBSort.Services;

/// <summary>
/// Liefert die Hilfe-Inhalte aus den eingebetteten Resources unter
/// Resources/Help/. Trennt das XAML/VM von der konkreten Resource-Lade-
/// Mechanik (pack-URI), damit die ViewModel-Logik testbar bleibt.
/// </summary>
public interface IHelpContentService
{
    /// <summary>
    /// Liefert alle in index.json definierten Kapitel, sortiert nach Order
    /// (aufsteigend), bei Gleichstand alphabetisch nach Title.
    /// </summary>
    IReadOnlyList<HelpChapter> GetChapters();

    /// <summary>
    /// Liest den Markdown-Quelltext eines Kapitels. Bei Fehler (Datei fehlt,
    /// Resource nicht gefunden) wird eine kurze Markdown-Fehlermeldung
    /// zurueckgegeben - die UI soll nie crashen.
    /// </summary>
    string LoadMarkdown(HelpChapter chapter);
}
