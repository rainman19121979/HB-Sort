using HBSort.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace HBSort.Core.Database;

/// <summary>
/// Entity Framework Core DbContext fuer die userdata.db.
/// Verwaltet alle Benutzerdaten: gescannte Figuren, Lagerfaecher, Floating Parts,
/// Scan-Historie und Tagesstatistiken.
///
/// Architektur-Hinweis: Die bl_cache.db wird NICHT ueber EF Core verwaltet,
/// sondern ueber direktes ADO.NET (Microsoft.Data.Sqlite) fuer Performance
/// beim Bulk-Import. Dieser Context ist nur fuer userdata.db.
/// </summary>
public class UserDataContext : DbContext
{
    // Jede DbSet-Property repraesentiert eine Tabelle in der Datenbank
    public DbSet<TrackedMinifig> TrackedMinifigs => Set<TrackedMinifig>();
    public DbSet<TrackedMinifigPart> TrackedMinifigParts => Set<TrackedMinifigPart>();
    public DbSet<StorageBin> StorageBins => Set<StorageBin>();
    public DbSet<FloatingPart> FloatingParts => Set<FloatingPart>();
    public DbSet<ScanEvent> ScanEvents => Set<ScanEvent>();
    public DbSet<DailyStats> DailyStats => Set<DailyStats>();
    // v0.1.24-beta.6 Phase 1: gespiegelter BL-Store-Inventar (Snapshot).
    public DbSet<BlInventoryLot> BlInventoryLots => Set<BlInventoryLot>();

    public UserDataContext(DbContextOptions<UserDataContext> options) : base(options)
    {
    }

    /// <summary>
    /// Hier konfigurieren wir das Datenbank-Schema im Detail.
    /// EF Core leitet zwar vieles automatisch ab, aber manche Dinge
    /// (z.B. Primaerschluessel ohne "Id"-Konvention) muessen wir explizit angeben.
    /// </summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // --- TrackedMinifig ---
        modelBuilder.Entity<TrackedMinifig>(entity =>
        {
            entity.HasKey(e => e.Id);

            // Status wird als String gespeichert (lesbarer in der DB als eine Zahl)
            entity.Property(e => e.Status)
                .HasConversion<string>();

            // Eine Minifigur hat viele benoetigte Teile
            entity.HasMany(e => e.RequiredParts)
                .WithOne(e => e.TrackedMinifig)
                .HasForeignKey(e => e.TrackedMinifigId)
                .OnDelete(DeleteBehavior.Cascade);

            // Eine Minifigur gehoert optional zu einem Lagerfach
            entity.HasOne(e => e.StorageBin)
                .WithMany(e => e.TrackedMinifigs)
                .HasForeignKey(e => e.StorageBinId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // --- TrackedMinifigPart ---
        modelBuilder.Entity<TrackedMinifigPart>(entity =>
        {
            entity.HasKey(e => e.Id);
        });

        // --- StorageBin ---
        modelBuilder.Entity<StorageBin>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Label).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Notes).HasMaxLength(500);
            // Unique-Index auf Label, damit Bulk-Create Konflikte erkennt.
            entity.HasIndex(e => e.Label).IsUnique();
        });

        // --- FloatingPart ---
        modelBuilder.Entity<FloatingPart>(entity =>
        {
            entity.HasKey(e => e.Id);

            // Ein FloatingPart gehoert immer zu einem Lagerfach
            entity.HasOne(e => e.StorageBin)
                .WithMany(e => e.FloatingParts)
                .HasForeignKey(e => e.StorageBinId)
                .OnDelete(DeleteBehavior.Cascade);

            // Optional-FK auf die Origin-Figur (aus DismantleWizard).
            // SetNull beim Loeschen - Teil bleibt als verwaister Eintrag bestehen.
            // FK erzeugt automatisch einen Index auf OriginMinifigId.
            entity.HasOne(e => e.OriginMinifig)
                .WithMany()
                .HasForeignKey(e => e.OriginMinifigId)
                .OnDelete(DeleteBehavior.SetNull);

            // Composite-Index fuer die haeufigste WHERE-Klausel im
            // Reverse-Match-Pfad: WHERE PartNumber=? AND ColorId=?
            // (siehe MinifigPersistenceService.PersistAndStoreAsync und
            // CheckAndMarkCompleteAsync). Ohne diesen Index wird jeder
            // Match linear ueber alle FloatingParts gescannt.
            entity.HasIndex(e => new { e.PartNumber, e.ColorId });
        });

        // --- ScanEvent ---
        modelBuilder.Entity<ScanEvent>(entity =>
        {
            entity.HasKey(e => e.Id);

            // ScanType als String speichern
            entity.Property(e => e.Type)
                .HasConversion<string>();
        });

        // --- DailyStats ---
        // Das Datum ist der Primaerschluessel (ein Eintrag pro Tag)
        modelBuilder.Entity<DailyStats>(entity =>
        {
            entity.HasKey(e => e.Date);
        });

        // --- BlInventoryLot (v0.1.24-beta.6 Phase 1) ---
        // Primary Key = LotId (BL-seitige inventory_id), kein Auto-Increment.
        // Damit kann Snapshot-Replace per RemoveRange/AddRange ohne Id-Mapping
        // arbeiten. ItemType+ItemNo+ColorId-Composite-Index unterstuetzt den
        // spaeteren Phase-2-Matching-Pfad (FloatingPart -> Inventory-Lookup).
        modelBuilder.Entity<BlInventoryLot>(entity =>
        {
            entity.HasKey(e => e.LotId);
            entity.Property(e => e.LotId).ValueGeneratedNever();
            entity.Property(e => e.ItemType).HasMaxLength(8).IsRequired();
            entity.Property(e => e.ItemNo).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ColorName).HasMaxLength(64);
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.Condition).HasMaxLength(2).IsRequired();
            entity.HasIndex(e => new { e.ItemType, e.ItemNo, e.ColorId });
        });
    }
}
