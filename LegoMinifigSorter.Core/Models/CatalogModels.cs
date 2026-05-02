namespace LegoMinifigSorter.Core.Models;

/// <summary>
/// Lese-Modelle fuer Lookups in der catalog.db.
/// Diese Klassen sind reine Datenbehaelter (keine EF-Entitaeten),
/// weil wir die catalog.db direkt mit Microsoft.Data.Sqlite lesen.
/// </summary>

/// <summary>Eintrag in der "metadata"-Tabelle (Schluessel/Wert).</summary>
public class CatalogMetadata
{
    /// <summary>Datum/Version des Imports (ISO-8601, z.B. "2026-04-30T16:00:00Z")</summary>
    public string ImportedAt { get; set; } = string.Empty;

    /// <summary>Anzahl Minifiguren im Katalog</summary>
    public int MinifigCount { get; set; }

    /// <summary>Anzahl Teile im Katalog</summary>
    public int PartCount { get; set; }

    /// <summary>Anzahl Farben im Katalog</summary>
    public int ColorCount { get; set; }

    /// <summary>Anzahl Sets im Katalog</summary>
    public int SetCount { get; set; }

    /// <summary>Quelle der Daten (z.B. "Embedded Seed" oder Pfad zum ZIP)</summary>
    public string Source { get; set; } = string.Empty;
}

/// <summary>Lego-Farbe aus catalog.db.colors</summary>
public class CatalogColor
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Rgb { get; set; } = string.Empty;
    public bool IsTransparent { get; set; }
}

/// <summary>Minifigur-Stammdaten aus catalog.db.minifigs</summary>
public class CatalogMinifig
{
    public string FigNum { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int NumParts { get; set; }
    public string? ImgUrl { get; set; }
}

/// <summary>Teile-Stammdaten aus catalog.db.parts</summary>
public class CatalogPart
{
    public string PartNum { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int? PartCatId { get; set; }
    public string? PartMaterial { get; set; }
}

/// <summary>
/// Ein Eintrag aus der Teileliste einer Minifigur.
/// Verbindet inventory_minifigs -> inventories -> inventory_parts mit
/// Stammdaten aus parts und colors.
/// </summary>
public class CatalogMinifigPart
{
    public string PartNumber { get; set; } = string.Empty;
    public string PartName { get; set; } = string.Empty;
    public int ColorId { get; set; }
    public string ColorName { get; set; } = string.Empty;

    /// <summary>RGB-Hex (ohne #) der Farbe – direkt aus catalog.db.colors.rgb mitgeladen.</summary>
    public string ColorRgb { get; set; } = string.Empty;

    public int Quantity { get; set; }
    public bool IsSpare { get; set; }
    public string? ImgUrl { get; set; }
}
