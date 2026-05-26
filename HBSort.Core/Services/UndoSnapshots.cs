namespace HBSort.Core.Services;

/// <summary>
/// UX X.29 (v0.1.16): Snapshot-Records die als JSON in
/// <see cref="HBSort.Core.Models.ScanEvent.UndoData"/> serialisiert werden.
/// Pro Aktions-Typ ein eigener Record - der UndoService deserialisiert
/// passend zum ScanEvent.Type.
///
/// Konvention: alle Felder als initonly Properties damit der Snapshot
/// als immutable behandelbar ist. Erweiterungen sind nullable, damit
/// alte JSON-Dokumente weiterhin lesbar bleiben.
/// </summary>
public sealed record UndoSnapshotMinifigDelete
{
    public int OriginalMinifigId { get; init; }
    public string BricklinkId { get; init; } = string.Empty;
    public string FigNum { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? ImageUrl { get; init; }
    public string? LocalImagePath { get; init; }
    public string? UserNotes { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public string Status { get; init; } = string.Empty;
    public int? StorageBinId { get; init; }
    public List<UndoSnapshotPart> RequiredParts { get; init; } = new();
}

/// <summary>RequiredPart-Snapshot - Teil von UndoSnapshotMinifigDelete.</summary>
public sealed record UndoSnapshotPart
{
    public string PartNumber { get; init; } = string.Empty;
    public int ColorId { get; init; }
    public string PartName { get; init; } = string.Empty;
    public string ColorName { get; init; } = string.Empty;
    public int QuantityNeeded { get; init; }
    public int QuantityCollected { get; init; }
}

public sealed record UndoSnapshotFloatingDelete
{
    public int OriginalFloatingId { get; init; }
    public string PartNumber { get; init; } = string.Empty;
    public int ColorId { get; init; }
    public string PartName { get; init; } = string.Empty;
    public string ColorName { get; init; } = string.Empty;
    public int Quantity { get; init; }
    public int? StorageBinId { get; init; }
    public DateTime AddedAt { get; init; }
    public int? OriginMinifigId { get; init; }
}

public sealed record UndoSnapshotMove
{
    public int MinifigId { get; init; }
    public int? OldStorageBinId { get; init; }
    public int? NewStorageBinId { get; init; }
}

/// <summary>
/// UX X.30 (v0.1.17): Snapshot fuer FloatingPart-Verschiebungen via
/// Bulk-Move. Eigener Record damit der UndoService klar zwischen Minifig-
/// und FloatingPart-Move unterscheiden kann (TrackedMinifigPart bzw.
/// FloatingPart sind unterschiedliche Entity-Typen).
/// </summary>
public sealed record UndoSnapshotFloatingMove
{
    public int FloatingPartId { get; init; }
    public int? OldStorageBinId { get; init; }
    public int? NewStorageBinId { get; init; }
}

public sealed record UndoSnapshotBinFreed
{
    public int BinId { get; init; }
    public DateTime PreviousFreedAt { get; init; }
}

public sealed record UndoSnapshotBinCreated
{
    public List<int> BinIds { get; init; } = new();
}

public sealed record UndoSnapshotComplete
{
    public int TrackedMinifigPartId { get; init; }
    public int OldQuantityCollected { get; init; }
    public string OldStatus { get; init; } = string.Empty;
    public DateTime? OldCompletedAt { get; init; }
}

/// <summary>
/// v0.1.24-beta.8 Phase 3: Snapshot fuer eine einzelne BL-Reservierung.
/// Wird in <see cref="HBSort.Core.Models.ScanEvent.UndoData"/> serialisiert
/// damit das spaetere Release das passende Lot kennt. Pro Reservierung ein
/// ScanEvent (Type=BlInventoryReservation) mit diesem Snapshot.
/// </summary>
public sealed record UndoSnapshotBlReservation
{
    public int TrackedMinifigPartId { get; init; }
    public int LotId { get; init; }
}
