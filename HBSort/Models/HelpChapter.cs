namespace HBSort.Models;

/// <summary>
/// Ein Kapitel im Hilfe-Tab. Wird aus Resources/Help/index.json geladen.
/// Title = Anzeigename in der Sidebar, FileName = Markdown-Datei in
/// Resources/Help/, Order = Sortier-Reihenfolge (kleiner zuerst).
/// </summary>
public class HelpChapter
{
    /// <summary>Anzeigename in der Sidebar, z.B. "Erste Schritte".</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Datei-Name relativ zu Resources/Help/, z.B. "01-erste-schritte.md".</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Sortier-Reihenfolge (1=oben). Bei Gleichstand: alphabetisch nach Title.</summary>
    public int Order { get; set; }
}
