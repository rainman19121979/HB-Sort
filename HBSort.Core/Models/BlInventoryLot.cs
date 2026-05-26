namespace HBSort.Core.Models;

/// <summary>
/// v0.1.24-beta.6 Phase 1: ein einzelner Verkaufs-Lot aus dem BrickLink-
/// Store-Inventar des Users. Wird vom IBlInventoryService via
/// IBricklinkClient.GetInventoryAsync() von der BL-API gezogen und in
/// userdata.db gespiegelt (Snapshot-Replace bei jedem Sync).
///
/// Phase 1 = nur Mirror + manueller Sync. Phase 2 wuerde das Inventar
/// mit dem lokalen Lager (FloatingParts/TrackedMinifigs) abgleichen.
/// </summary>
public class BlInventoryLot
{
    /// <summary>
    /// BL-seitige inventory_id (BricklinkSharp.Inventory.InventoryId).
    /// Stabile Lot-Kennung - wir nutzen sie direkt als Primary Key, damit
    /// Re-Sync via Snapshot-Replace ohne Lookup-Roundtrip funktioniert.
    /// </summary>
    public int LotId { get; set; }

    /// <summary>BL-Item-Typ als Kurzform: "P" (Part), "M" (Minifig), "S" (Set) etc.</summary>
    public string ItemType { get; set; } = string.Empty;

    /// <summary>BL-Artikelnummer, z.B. "3001" (Part), "sw0001a" (Minifig).</summary>
    public string ItemNo { get; set; } = string.Empty;

    /// <summary>BL-Color-ID. Null wenn das Item farblos ist (z.B. Minifig, Set).</summary>
    public int? ColorId { get; set; }

    /// <summary>Color-Name gecacht (z.B. "Red"). Null wenn ColorId=null.</summary>
    public string? ColorName { get; set; }

    /// <summary>User-Beschreibung des Lots (Listing-Description aus dem BL-Store). Optional.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// v0.1.24-beta.8 Phase 3: BL-Remarks-Feld - User-Notiz pro Lot, typisch
    /// die physische Position im Shop (z.B. "Box A.2, Regal 3"). Wird beim
    /// Sync von BL geholt und im Detail-Panel + Reservierungs-Dialog gezeigt
    /// damit der User das Teil schnell findet.
    /// </summary>
    public string? Remarks { get; set; }

    /// <summary>Stueckzahl im Lot.</summary>
    public int Quantity { get; set; }

    /// <summary>Stueckpreis in Store-Waehrung (BL liefert Decimal).</summary>
    public decimal UnitPrice { get; set; }

    /// <summary>"N" = New, "U" = Used. Aus BricklinkSharp.Condition-Enum gemappt.</summary>
    public string Condition { get; set; } = "U";

    /// <summary>
    /// Menge die HBSort lokal bereits fuer Figuren / Lots eingeplant hat,
    /// aber noch nicht an BL gemeldet ist. Default 0. Bleibt ueber Re-Syncs
    /// erhalten (Snapshot-Replace stellt den Wert pro LotId aus dem alten
    /// Snapshot wieder her). Verfuegbar fuer den Verkauf = Quantity - ReservedQuantity.
    /// </summary>
    public int ReservedQuantity { get; set; }

    /// <summary>Zeitpunkt des letzten Snapshot-Sync. UTC.</summary>
    public DateTime LastSyncedAt { get; set; }
}
