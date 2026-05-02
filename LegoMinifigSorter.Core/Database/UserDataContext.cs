using LegoMinifigSorter.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace LegoMinifigSorter.Core.Database;

/// <summary>
/// Entity Framework Core DbContext für die userdata.db.
/// Verwaltet alle Benutzerdaten: gescannte Figuren, Lagerfächer, Floating Parts,
/// Scan-Historie und Tagesstatistiken.
///
/// Architektur-Hinweis: Die catalog.db wird NICHT über EF Core verwaltet,
/// sondern über direktes ADO.NET (Microsoft.Data.Sqlite) für Performance
/// beim Bulk-Import. Dieser Context ist nur für userdata.db.
/// </summary>
public class UserDataContext : DbContext
{
    // Jede DbSet-Property repräsentiert eine Tabelle in der Datenbank
    public DbSet<TrackedMinifig> TrackedMinifigs => Set<TrackedMinifig>();
    public DbSet<TrackedMinifigPart> TrackedMinifigParts => Set<TrackedMinifigPart>();
    public DbSet<StorageBin> StorageBins => Set<StorageBin>();
    public DbSet<FloatingPart> FloatingParts => Set<FloatingPart>();
    public DbSet<ScanEvent> ScanEvents => Set<ScanEvent>();
    public DbSet<DailyStats> DailyStats => Set<DailyStats>();

    public UserDataContext(DbContextOptions<UserDataContext> options) : base(options)
    {
    }

    /// <summary>
    /// Hier konfigurieren wir das Datenbank-Schema im Detail.
    /// EF Core leitet zwar vieles automatisch ab, aber manche Dinge
    /// (z.B. Primärschlüssel ohne "Id"-Konvention) müssen wir explizit angeben.
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

            // Eine Minifigur hat viele benötigte Teile
            entity.HasMany(e => e.RequiredParts)
                .WithOne(e => e.TrackedMinifig)
                .HasForeignKey(e => e.TrackedMinifigId)
                .OnDelete(DeleteBehavior.Cascade);

            // Eine Minifigur gehört optional zu einem Lagerfach
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

            // Ein FloatingPart gehört immer zu einem Lagerfach
            entity.HasOne(e => e.StorageBin)
                .WithMany(e => e.FloatingParts)
                .HasForeignKey(e => e.StorageBinId)
                .OnDelete(DeleteBehavior.Cascade);

            // Optional-FK auf die Origin-Figur (aus DismantleWizard).
            // SetNull beim Loeschen – Teil bleibt als verwaister Eintrag bestehen.
            entity.HasOne(e => e.OriginMinifig)
                .WithMany()
                .HasForeignKey(e => e.OriginMinifigId)
                .OnDelete(DeleteBehavior.SetNull);
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
        // Das Datum ist der Primärschlüssel (ein Eintrag pro Tag)
        modelBuilder.Entity<DailyStats>(entity =>
        {
            entity.HasKey(e => e.Date);
        });
    }
}
