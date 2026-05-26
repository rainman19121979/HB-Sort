using HBSort.Core.Models;

namespace HBSort.Core.Services;

/// <summary>
/// v0.1.23 Strict-Mode: wird vom Service geworfen wenn eine Schreib-Aktion
/// versucht ein Item in einen typ-inkompatiblen Bin zu legen (z.B.
/// FloatingPart in einen Complete-Bin, wartende Figur in einen
/// Floating-Bin).
///
/// Reverse-Match-Bypass: FloatingPart -&gt; Waiting-Bin ist erlaubt, wenn
/// das Teil zu einer der wartenden Figuren im Bin passt. Aufrufer-Pfade
/// die diesen Bypass nicht haben muessen den Fehler an die UI weitergeben.
/// </summary>
public class InvalidBinKindException : InvalidOperationException
{
    /// <summary>Ziel-Bin-Id die abgelehnt wurde.</summary>
    public int TargetBinId { get; }

    /// <summary>Aktueller Kind des Ziel-Bins.</summary>
    public StorageBinKind TargetBinKind { get; }

    /// <summary>Label des Ziel-Bins (fuer User-Meldungen).</summary>
    public string TargetBinLabel { get; }

    /// <summary>Beschreibung der versuchten Aktion (z.B. "FloatingPart hinzufuegen").</summary>
    public string AttemptedAction { get; }

    public InvalidBinKindException(
        int targetBinId,
        StorageBinKind targetBinKind,
        string targetBinLabel,
        string attemptedAction,
        string message)
        : base(message)
    {
        TargetBinId = targetBinId;
        TargetBinKind = targetBinKind;
        TargetBinLabel = targetBinLabel;
        AttemptedAction = attemptedAction;
    }
}

/// <summary>v0.1.23 Models: Bin-Typ-Spalte fuer StorageBin.Kind.</summary>
public static class StorageBinKindExtensions
{
    /// <summary>Lesbare Bezeichnung fuer Toast-/Log-Texte.</summary>
    public static string ToDisplayName(this StorageBinKind kind) => kind switch
    {
        StorageBinKind.Empty => "Leer",
        StorageBinKind.Floating => "Einzelteile",
        StorageBinKind.Waiting => "Wartende Figuren",
        StorageBinKind.Complete => "Komplette Figuren",
        _ => kind.ToString()
    };
}

/// <summary>
/// v0.1.23 Strict-Mode Guard: zentrale Pruef-Logik fuer typ-vertraegliches
/// Ablegen von Items in Lagerfaecher. Wirft <see cref="InvalidBinKindException"/>
/// bei Verletzung. Reverse-Match-Bypass fuer Floating-in-Waiting-Bin ist
/// hier implementiert.
/// </summary>
public static class BinKindGuard
{
    /// <summary>
    /// Prueft ob ein FloatingPart (PartNo+ColorId) in den uebergebenen Bin
    /// gelegt werden darf. Bin muss <see cref="StorageBin.TrackedMinifigs"/>
    /// inklusive <c>RequiredParts</c> geladen haben (fuer den Reverse-Match-
    /// Bypass).
    ///
    /// Regeln:
    /// - Empty / Floating: immer OK
    /// - Complete: niemals
    /// - Waiting: nur wenn (partNo, colorId) zu mind. einer wartenden Figur
    ///   im Bin passt UND deren RequiredPart noch nicht voll ist.
    /// </summary>
    public static void EnsureBinAcceptsFloatingPart(
        StorageBin bin,
        string blPartNo, int blColorId,
        string actionDescription)
    {
        if (bin.Kind == StorageBinKind.Empty || bin.Kind == StorageBinKind.Floating)
            return;

        if (bin.Kind == StorageBinKind.Complete)
        {
            throw new InvalidBinKindException(
                bin.Id, bin.Kind, bin.Label, actionDescription,
                $"Lagerfach '{bin.Label}' enthaelt nur komplette Figuren - " +
                "Einzelteile sind dort nicht zulaessig.");
        }

        // Waiting: Reverse-Match-Bypass.
        // v0.1.24-beta.8 Phase 3: effektive Restmenge (physisch + BL-reserviert).
        var hasMatch = bin.TrackedMinifigs.Any(m =>
            m.Status == TrackedMinifigStatus.Waiting
            && m.RequiredParts.Any(rp =>
                rp.PartNumber == blPartNo
                && rp.ColorId == blColorId
                && (rp.QuantityCollected + rp.QuantityReservedFromBl) < rp.QuantityNeeded));
        if (!hasMatch)
        {
            throw new InvalidBinKindException(
                bin.Id, bin.Kind, bin.Label, actionDescription,
                $"Lagerfach '{bin.Label}' enthaelt wartende Figuren - " +
                $"Teil {blPartNo}/{blColorId} passt zu keiner davon.");
        }
    }

    /// <summary>
    /// Prueft ob eine Figur (wartend oder komplett) in den uebergebenen Bin
    /// gelegt werden darf.
    ///
    /// Regeln:
    /// - Empty / Waiting / Complete: OK (Reifungs-Mix Wartend+Complete erlaubt)
    /// - Floating: niemals
    /// </summary>
    public static void EnsureBinAcceptsMinifig(
        StorageBin bin,
        string actionDescription)
    {
        if (bin.Kind == StorageBinKind.Empty
            || bin.Kind == StorageBinKind.Waiting
            || bin.Kind == StorageBinKind.Complete)
            return;

        // Floating-Only Bin
        throw new InvalidBinKindException(
            bin.Id, bin.Kind, bin.Label, actionDescription,
            $"Lagerfach '{bin.Label}' enthaelt nur Einzelteile - " +
            "Figuren koennen dort nicht abgelegt werden.");
    }
}
