using HBSort.Core.Models;

namespace HBSort.Core.Services;

/// <summary>
/// UX X.33 v0.1.19-beta.7 Block M: Mapping einer Brickognize-Kategorie
/// (z.B. "Minifigure, Head") auf ein bestimmtes Lagerfach.
///
/// Der Service hat zwei Aufgaben:
/// 1. Zur Scan-Zeit: nachschlagen welches Fach fuer eine gegebene Kategorie
///    konfiguriert ist (oder fuer einen PartName via Praefix-Match - das
///    ist der Fallback fuer Wizard-Pfade ohne Brickognize-Daten).
/// 2. Pflege: neue Kategorien zur SeenBrickognizeCategories-Liste hinzufuegen
///    damit das Settings-UI sie zeigen kann.
///
/// Die Mapping-Daten leben in <see cref="AppSettings.CategoryToBinMapping"/>
/// und <see cref="AppSettings.SeenBrickognizeCategories"/> - der Service ist
/// ein duenner Accessor + Persistenz.
/// </summary>
public interface ICategoryBinMappingService
{
    /// <summary>
    /// Sucht das gemappte Bin fuer eine Brickognize-Kategorie. Liefert null
    /// wenn kein Mapping existiert oder das gemappte Bin geloescht wurde.
    /// Bin existiert wird vom <see cref="IStorageBinService"/> gegen die
    /// aktuelle DB-Liste verifiziert.
    /// </summary>
    Task<StorageBin?> ResolveBinForCategoryAsync(
        string? brickognizeCategory,
        CancellationToken ct = default);

    /// <summary>
    /// Praefix-Match-Variante fuer Pfade ohne Brickognize-Daten (Wizard,
    /// BuildSuggestion). Schaut ob der PartName mit einer der gemappten
    /// Kategorie-Strings beginnt - "Minifigure, Head Black Eyebrows ..."
    /// matcht "Minifigure, Head". Bei mehreren passenden gewinnt der
    /// laengste Match (spezifischer schlaegt allgemeiner).
    /// </summary>
    Task<StorageBin?> ResolveBinForPartNameAsync(
        string? partName,
        CancellationToken ct = default);

    /// <summary>
    /// UX X.33 Block N (v0.1.19-beta.7) + Pre-Tag-Fix: leitet eine
    /// Brickognize-Kategorie aus dem PartName ab. Match gegen die
    /// <see cref="AppSettings.SeenBrickognizeCategories"/>-Liste:
    ///
    ///   "Minifigure, Head Black Eyebrows..." -> "Minifigure, Head"
    ///   "Minifigure, Headgear Helmet..."     -> "Minifigure, Headgear"
    ///
    /// Laengster Match gewinnt damit "Minifigure, Headgear" nicht
    /// faelschlich auf "Minifigure, Head" matcht.
    ///
    /// **Liefert immer einen String** - Fallback auf <see cref="UnknownCategory"/>
    /// ("Unbekannt") wenn kein Praefix-Match. Damit ist die Spalte
    /// <see cref="FloatingPart.BrickognizeCategory"/> in der DB nie mehr null
    /// und die Default-Regel "max 1 PartId pro Kategorie pro Bin" greift
    /// auch fuer ungelabelte Teile (z.B. aus dem Wizard).
    /// </summary>
    string DeriveCategoryFromPartName(string? partName);

    /// <summary>
    /// Pseudo-Kategorie fuer alle Items deren echte Brickognize-Kategorie
    /// nicht bekannt ist. Wird in der DB persistiert und im Settings-Tab
    /// "Kategorien" als gewoehnliche Kategorie angezeigt - User kann sie
    /// also genauso einem festen Bin zuweisen.
    /// </summary>
    public const string UnknownCategory = "Unbekannt";

    /// <summary>
    /// Fuegt eine Kategorie zu <see cref="AppSettings.SeenBrickognizeCategories"/>
    /// hinzu wenn sie noch nicht drin ist und persistiert die Settings.
    /// Wird beim Scannen aufgerufen damit das Settings-UI organisch
    /// alle bisher gesehenen Strings zeigt.
    /// </summary>
    Task RememberSeenCategoryAsync(string brickognizeCategory, CancellationToken ct = default);

    // UX X.33 Block N (v0.1.19-beta.7 Refactor): UpdateMappingAsync entfernt.
    // Das Auto-Mapping beim Lagern ist raus - User pflegt Mapping nur noch
    // manuell ueber den Settings-Tab "Kategorien" (direkter Property-Set
    // beim Save). Heisst: ungemappte Kategorien fallen auf die Default-Regel
    // "max 1 PartId pro Kategorie pro Bin" zurueck.
}
