using HBSort.Core.Services;

namespace HBSort.ViewModels;

/// <summary>
/// v0.1.24-beta.2: Statischer Helper fuer wiederkehrende Patterns beim
/// Aufbau einer <see cref="SortInstruction"/>. Reduziert die Duplikation
/// zwischen den zwei Builder-Stellen
/// (<c>ScanViewModel.PersistPendingAsync</c> ~Z.1716 und
/// <c>CollectMinifigWizardViewModel.BuildSortInstructionForSave</c>).
///
/// <para>
/// Beide Stellen tun strukturell dasselbe: Konsumierte FloatingParts in
/// Take-Sektionen umwandeln (gruppiert nach Quell-Bin), dann eine
/// Put-Sektion fuer die Figur in das Ziel-Bin anhaengen. Die zwei DTOs
/// (<see cref="ConsumedFloatingPartInfo"/> aus dem Service vs
/// <see cref="PendingMinifigViewModel.ConsumedFromBin"/> aus dem UI-
/// Tracking) ueberlappen 5 von 6 Feldern, unterscheiden sich nur im
/// Property-Naming (<c>BlPartNo</c>/<c>BlColorId</c> vs
/// <c>PartNo</c>/<c>ColorId</c>).
/// </para>
///
/// <para>
/// Lokation: gleicher Ordner wie <see cref="SortInstruction"/>. Der Helper
/// ist ein UI-Layer-Komfortmittel (nutzt VM-Nested-Type
/// <c>ConsumedFromBin</c>) und darf daher nicht in <c>HBSort.Core</c> wandern.
/// Aufrufer bleiben frei den DTO manuell zu bauen — kein Breaking Change.
/// </para>
///
/// <para>
/// Engineering-Prinzip 1.5: Re-Use vor Re-Implementation. Audit-Empfehlung
/// V5 in <c>docs/komplexitaets-audit-2026-05-17.md</c>.
/// </para>
/// </summary>
internal static class SortInstructionBuilder
{
    /// <summary>
    /// Haengt Take-Sektionen aus einer Liste von
    /// <see cref="ConsumedFloatingPartInfo"/>-Eintraegen (Service-DTO) an die
    /// Instruktion an. Gruppiert nach <c>SourceBinLabel</c>; pro Bin eine
    /// <see cref="SortSection"/>. Leere Eingabe = No-Op.
    /// </summary>
    public static void AddTakeSections(
        SortInstruction instruction,
        IEnumerable<ConsumedFloatingPartInfo> consumed)
    {
        if (consumed == null) return;
        foreach (var byBin in consumed.GroupBy(c => c.SourceBinLabel))
        {
            var section = new SortSection { BinLabel = byBin.Key };
            foreach (var c in byBin)
            {
                section.Items.Add(new SortItemLine
                {
                    Label = c.PartName,
                    Detail = $"{c.BlPartNo} - {c.ColorName}",
                    QuantityText = $"{c.Quantity}x",
                    ImageUrl = c.ImageUrl
                });
            }
            instruction.Take.Add(section);
        }
    }

    /// <summary>
    /// Haengt Take-Sektionen aus einer Liste von
    /// <see cref="PendingMinifigViewModel.ConsumedFromBin"/>-Eintraegen
    /// (UI-Tracking-DTO) an die Instruktion an. Gruppiert nach
    /// <c>SourceBinLabel</c>; pro Bin eine <see cref="SortSection"/>.
    /// Leere Eingabe = No-Op.
    /// </summary>
    public static void AddTakeSections(
        SortInstruction instruction,
        IEnumerable<PendingMinifigViewModel.ConsumedFromBin> consumed)
    {
        if (consumed == null) return;
        foreach (var byBin in consumed.GroupBy(c => c.SourceBinLabel))
        {
            var section = new SortSection { BinLabel = byBin.Key };
            foreach (var c in byBin)
            {
                section.Items.Add(new SortItemLine
                {
                    Label = c.PartName,
                    Detail = $"{c.PartNo} - {c.ColorName}",
                    QuantityText = $"{c.Quantity}x",
                    ImageUrl = c.ImageUrl
                });
            }
            instruction.Take.Add(section);
        }
    }

    /// <summary>
    /// Haengt eine Put-Sektion fuer eine fertige Figur an. Erzeugt eine
    /// neue <see cref="SortSection"/> mit dem Ziel-Bin und genau einer
    /// Item-Zeile fuer die Figur ("Figur '{name}'" + BL-ID + Bild).
    /// </summary>
    public static void AddMinifigPut(
        SortInstruction instruction,
        string targetBinLabel,
        string minifigName,
        string? bricklinkId,
        string? imageUrl)
    {
        instruction.Put.Add(new SortSection
        {
            BinLabel = targetBinLabel,
            Items = new List<SortItemLine>
            {
                new()
                {
                    Label = $"Figur '{minifigName}'",
                    Detail = bricklinkId ?? string.Empty,
                    QuantityText = "1x",
                    ImageUrl = imageUrl
                }
            }
        });
    }
}
