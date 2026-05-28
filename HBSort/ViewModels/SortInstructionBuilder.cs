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
    /// v0.1.24-beta.11: BL-Shop-Take-Sektionen. Pro Reservierung eine
    /// <see cref="SortSection"/> gruppiert nach BL-Lagerplatz (Remarks).
    /// Sections werden NACH den internen Take-Sektionen angehaengt damit
    /// der User erst sein Lager abklappert und dann an seinen BL-Shop geht.
    /// Reihenfolge erhaelt die Aufruf-Reihenfolge der <paramref name="lots"/>.
    /// </summary>
    public static void AddBlShopTakeSections(
        SortInstruction instruction,
        IEnumerable<BlShopTake> lots)
    {
        if (lots == null) return;
        foreach (var byShopBin in lots.GroupBy(l => string.IsNullOrWhiteSpace(l.Remarks) ? "(kein Lagerplatz)" : l.Remarks))
        {
            var section = new SortSection { BinLabel = $"BL-Shop: {byShopBin.Key}" };
            foreach (var t in byShopBin)
            {
                var cond = t.Condition == "N" ? "Neu" : "Gebraucht";
                section.Items.Add(new SortItemLine
                {
                    Label = t.PartName,
                    Detail = $"{t.BlPartNo} - {t.ColorName} ({cond})",
                    QuantityText = $"{t.Quantity}x",
                    ImageUrl = t.ImageUrl
                });
            }
            instruction.Take.Add(section);
        }
    }

    /// <summary>
    /// v0.1.24-beta.13 (V5): Fall A (U-Lot / Gebraucht). Eine Put-Sektion
    /// "BL-Shop: {Remarks}" mit dem gescannten Teil — das physische Teil
    /// fuellt den BL-Shop wieder auf (das Reservierte ist mental schon bei
    /// der Figur). Keine Take-Sektion notwendig (User hat Teil in der Hand).
    /// PlusHint setzt den Erklaerungs-Text.
    /// </summary>
    public static SortInstruction BuildV5ShopReturn(
        string partName,
        string blPartNo,
        string colorName,
        string? lotRemarks,
        string? partImageUrl)
    {
        var instr = new SortInstruction
        {
            HeaderText = "Teil zugeordnet — Reservierung aufgeloest",
            PlusHint = "Reservierung aufgeloest. Figur behaelt ihr Teil, " +
                       "BL-Shop wieder vollstaendig."
        };
        var shopLabel = string.IsNullOrWhiteSpace(lotRemarks)
            ? "(kein Lagerplatz)"
            : lotRemarks!;
        instr.Put.Add(new SortSection
        {
            BinLabel = $"BL-Shop: {shopLabel}",
            Items = new List<SortItemLine>
            {
                new()
                {
                    Label = partName,
                    Detail = $"{blPartNo} - {colorName} (Gebraucht)",
                    QuantityText = "1x",
                    ImageUrl = partImageUrl
                }
            }
        });
        return instr;
    }

    /// <summary>
    /// v0.1.24-beta.13 (V5): Fall B (N-Lot / Neu). Tausch zwischen Figur-Fach
    /// und BL-Shop-Fach:
    /// <list type="number">
    ///   <item>Take: das neue Teil aus dem Figur-Fach (das hatte der User
    ///   beim Reservieren mental aus dem Shop in die Figur gelegt).</item>
    ///   <item>Put: das neue Teil zurueck in den BL-Shop (am Lagerplatz
    ///   <c>lotRemarks</c>).</item>
    ///   <item>Put: das gescannte (gebrauchte) Teil in das Figur-Fach.</item>
    /// </list>
    /// PlusHint setzt die Erklaerung.
    /// </summary>
    public static SortInstruction BuildV5ShopExchange(
        string partName,
        string blPartNo,
        string colorName,
        string? lotRemarks,
        string? minifigBinLabel,
        string? partImageUrl)
    {
        var instr = new SortInstruction
        {
            HeaderText = "Teil zugeordnet — Reservierung aufgeloest (Tausch)",
            PlusHint = "Reservierung aufgeloest. Das neue Teil geht zurueck in " +
                       "den Shop, dein gescanntes Teil zur Figur."
        };
        var shopLabel = string.IsNullOrWhiteSpace(lotRemarks)
            ? "(kein Lagerplatz)"
            : lotRemarks!;
        var figBin = string.IsNullOrWhiteSpace(minifigBinLabel)
            ? "(kein Fach)"
            : minifigBinLabel!;

        // 1) Take aus dem Figur-Fach: das neue Teil rausnehmen.
        instr.Take.Add(new SortSection
        {
            BinLabel = figBin,
            Items = new List<SortItemLine>
            {
                new()
                {
                    Label = partName,
                    Detail = $"{blPartNo} - {colorName} (Neu, das aus dem Shop)",
                    QuantityText = "1x",
                    ImageUrl = partImageUrl
                }
            }
        });
        // 2) Put: das neue Teil zurueck in den BL-Shop.
        instr.Put.Add(new SortSection
        {
            BinLabel = $"BL-Shop: {shopLabel}",
            Items = new List<SortItemLine>
            {
                new()
                {
                    Label = partName,
                    Detail = $"{blPartNo} - {colorName} (Neu)",
                    QuantityText = "1x",
                    ImageUrl = partImageUrl
                }
            }
        });
        // 3) Put: das gescannte (gebrauchte) Teil in die Figur.
        instr.Put.Add(new SortSection
        {
            BinLabel = figBin,
            Items = new List<SortItemLine>
            {
                new()
                {
                    Label = partName,
                    Detail = $"{blPartNo} - {colorName} (gescanntes Teil)",
                    QuantityText = "1x",
                    ImageUrl = partImageUrl
                }
            }
        });
        return instr;
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

/// <summary>
/// v0.1.24-beta.11: DTO fuer eine BL-Shop-Entnahme im SortInstruction-Modal.
/// Wird vom BuildSuggestionDetailDialog beim Anlegen pro reserviertem Lot
/// gefuellt — Pfad zum Lagerplatz (Remarks), Zustand (Neu/Gebraucht),
/// Menge und optionales Teile-Bild.
/// </summary>
public sealed class BlShopTake
{
    public string BlPartNo { get; init; } = string.Empty;
    public string PartName { get; init; } = string.Empty;
    public string ColorName { get; init; } = string.Empty;
    public string? Remarks { get; init; }
    public string Condition { get; init; } = "U"; // "N" / "U"
    public int Quantity { get; init; } = 1;
    public string? ImageUrl { get; init; }
}
