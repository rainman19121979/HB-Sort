using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace HBSort.Core.Database;

/// <summary>
/// Diese Klasse wird NUR vom EF-Core-Migrations-Tooling verwendet (dotnet ef migrations add ...).
/// Sie erstellt einen DbContext mit einem festen Pfad, damit EF weiß wie es die DB öffnen soll.
/// Im laufenden Programm wird der Context über den DI-Container konfiguriert.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<UserDataContext>
{
    public UserDataContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<UserDataContext>();

        // Temporärer Pfad nur für Migrations-Erstellung (wird zur Laufzeit überschrieben)
        optionsBuilder.UseSqlite("Data Source=design_time_userdata.db");

        return new UserDataContext(optionsBuilder.Options);
    }
}
