using System.Collections.ObjectModel;
using System.Windows.Documents;
using CommunityToolkit.Mvvm.ComponentModel;
using HBSort.Models;
using HBSort.Services;
using Markdig;
using Markdig.Wpf;
using Serilog;

namespace HBSort.ViewModels;

/// <summary>
/// ViewModel fuer den Hilfe-Tab.
///
/// Aufgaben:
/// - Kapitelliste aus dem HelpContentService bereitstellen
/// - Bei Auswahl eines Kapitels den zugehoerigen Markdown-Inhalt laden und
///   in ein FlowDocument rendern (Markdig.Wpf)
/// - Beim ersten Anzeigen automatisch das erste Kapitel auswaehlen
///
/// Bewusst klein gehalten: keine Suche, kein Caching - die Inhalte sind
/// klein (paar Kilobyte pro Kapitel) und das Rendering ist instant.
///
/// TODO 2026-05 (UX-Politur): Such-Funktion ueber Kapitel-Inhalte. Idee:
/// Eingabefeld in der Sidebar -> filtere Kapitel deren Markdown-Quelltext
/// (case-insensitive) den Suchstring enthaelt; Treffer-Kapitel oben in der
/// Liste, andere ausgrauen. Bewusst aufgeschoben weil 7 Kapitel zur Zeit
/// auch ohne Suche gut ueberschaubar sind.
/// </summary>
public partial class HelpViewModel : ObservableObject
{
    private readonly IHelpContentService _content;

    /// <summary>
    /// Markdig-Pipeline mit gaengigen Erweiterungen (Tabellen, Task-Listen,
    /// Auto-Links). UseSupportedExtensions enthaelt alles was Markdig.Wpf
    /// rendern kann - ein Aufruf reicht.
    /// </summary>
    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .UseSupportedExtensions()
        .Build();

    public ObservableCollection<HelpChapter> Chapters { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private HelpChapter? _selectedChapter;

    /// <summary>Aktuelles FlowDocument das in der Content-Spalte angezeigt wird.</summary>
    [ObservableProperty]
    private FlowDocument? _currentDocument;

    /// <summary>True wenn ein Kapitel ausgewaehlt ist - steuert Visibility des Hinweises.</summary>
    public bool HasSelection => SelectedChapter != null;

    /// <summary>Hinweis-Text wenn keine Kapitel geladen werden konnten.</summary>
    [ObservableProperty]
    private string _emptyStateMessage =
        "Keine Hilfe-Inhalte gefunden. Bitte App neu installieren.";

    public bool HasChapters => Chapters.Count > 0;

    public HelpViewModel(IHelpContentService content)
    {
        _content = content;

        // Kapitel beim Konstruktor laden - alles sync, keine I/O ausserhalb der
        // Resource-Streams.
        foreach (var ch in _content.GetChapters())
        {
            Chapters.Add(ch);
        }

        OnPropertyChanged(nameof(HasChapters));

        // Auto-Select des ersten Kapitels (falls vorhanden), damit der User
        // beim ersten Tab-Wechsel direkt Inhalt sieht.
        if (Chapters.Count > 0)
        {
            SelectedChapter = Chapters[0];
        }
    }

    /// <summary>
    /// Wird automatisch von ObservableProperty aufgerufen, wenn
    /// SelectedChapter sich aendert. Laedt das Markdown und baut das
    /// FlowDocument neu auf.
    /// </summary>
    partial void OnSelectedChapterChanged(HelpChapter? value)
    {
        if (value == null)
        {
            CurrentDocument = null;
            return;
        }

        try
        {
            var md = _content.LoadMarkdown(value);
            CurrentDocument = Markdig.Wpf.Markdown.ToFlowDocument(md, _pipeline);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Markdown-Rendering fehlgeschlagen fuer {Chapter}", value.Title);

            // Fallback: einfacher Fehler-Hinweis als FlowDocument, damit die
            // UI nicht leer bleibt.
            CurrentDocument = Markdig.Wpf.Markdown.ToFlowDocument(
                $"# Fehler beim Anzeigen\n\nDas Kapitel **{value.Title}** konnte " +
                $"nicht gerendert werden.\n\nDetails: {ex.Message}",
                _pipeline);
        }
    }
}
