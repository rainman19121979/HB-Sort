using System.Text.Json.Serialization;

namespace HBSort.Core.Models;

/// <summary>
/// Top-Level-Response der Brickognize-API.
/// Wird aus den 4 Endpoints (/predict/, /predict/parts/, /predict/figs/, /predict/sets/)
/// zurueckgeliefert. Format-Doku: BRICKOGNIZE_API.md.
/// </summary>
public class BrickognizePrediction
{
    /// <summary>Eindeutige ID dieser Erkennungs-Session bei Brickognize.</summary>
    [JsonPropertyName("listing_id")]
    public string? ListingId { get; set; }

    /// <summary>Bounding-Box des erkannten Objekts (kann null sein wenn nichts erkannt).</summary>
    [JsonPropertyName("bounding_box")]
    public BrickognizeBoundingBox? BoundingBox { get; set; }

    /// <summary>Top-Treffer zuerst, absteigend nach Score sortiert.</summary>
    [JsonPropertyName("items")]
    public List<BrickognizeItem> Items { get; set; } = new();

    /// <summary>
    /// Farb-Vorschlaege. Nur beim /predict/parts/?predict_color=true Endpoint befuellt,
    /// sonst leer/null.
    /// </summary>
    [JsonPropertyName("colors")]
    public List<BrickognizeColor>? Colors { get; set; }
}

/// <summary>
/// Wo im Bild Brickognize das Objekt erkannt hat.
/// WICHTIG: Alle Koordinaten und Bildmasse sind in echten Responses 'double'
/// (z.B. left=114.08761596679688), NICHT 'int' wie urspruenglich angenommen.
/// Validiert am 2026-04-30 mit echten API-Aufrufen.
/// </summary>
public class BrickognizeBoundingBox
{
    [JsonPropertyName("left")]         public double Left { get; set; }
    [JsonPropertyName("upper")]        public double Upper { get; set; }
    [JsonPropertyName("right")]        public double Right { get; set; }
    [JsonPropertyName("lower")]        public double Lower { get; set; }
    [JsonPropertyName("image_width")]  public double ImageWidth { get; set; }
    [JsonPropertyName("image_height")] public double ImageHeight { get; set; }
    [JsonPropertyName("score")]        public double Score { get; set; }
}

/// <summary>
/// Ein Treffer in Brickognize.Items.
/// Wir nutzen die external_sites-URLs zur ID-Aufloesung (siehe ExternalIdResolver).
/// </summary>
public class BrickognizeItem
{
    /// <summary>Item-ID, Format ist typabhaengig. Nicht ohne external_sites raten!</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("img_url")]
    public string? ImgUrl { get; set; }

    [JsonPropertyName("external_sites")]
    public List<BrickognizeExternalSite> ExternalSites { get; set; } = new();

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    /// <summary>"part", "set", "fig" oder "sticker"</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>Konfidenz 0.0 - 1.0.</summary>
    [JsonPropertyName("score")]
    public double Score { get; set; }
}

/// <summary>Link auf einen Drittanbieter-Katalog (BrickLink, Rebrickable, BrickOwl).</summary>
public class BrickognizeExternalSite
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}

/// <summary>Farb-Vorschlag bei /predict/parts/.</summary>
public class BrickognizeColor
{
    /// <summary>Color-ID als String (Brickognize liefert das so) - Rebrickable-Color-IDs.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("score")]
    public double Score { get; set; }
}

/// <summary>
/// Result-Record fuer den ID-Resolver. Enthaelt die aus external_sites-URLs
/// extrahierten IDs. Felder koennen null sein wenn die Site nicht vorhanden war.
/// </summary>
public record ExternalIds(
    string? RebrickableId,    // fig-XXXXXX, part_num oder set_num
    string? BricklinkId,      // sw0001, 3001 oder 75300-1
    string? BrickOwlId
);

/// <summary>Item-Typ-Enum, vereinfacht den Brickognize-string ("part"/"fig"/...) ab.</summary>
public enum BrickognizeItemType
{
    Unknown,
    Part,
    Minifig,
    Set,
    Sticker
}
