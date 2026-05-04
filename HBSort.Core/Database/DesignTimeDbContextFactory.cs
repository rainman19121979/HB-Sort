using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace HBSort.Core.Database;

/// <summary>
/// Diese Klasse wird NUR vom EF-Core-Migrations-Tooling verwendet (dotnet ef migrations add ...).
/// Sie erstellt einen DbContext mit einem festen Pfad, damit EF weiss wie es die DB oeffnen soll.
/// Im laufenden Programm wird der Context ueber den DI-Container konfiguriert.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<UserDataContext>
{
    public UserDataContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<UserDataContext>();

        // Temporaerer Pfad nur fuer Migrations-Erstellung (wird zur Laufzeit ueberschrieben)
        optionsBuilder.UseSqlite("Data Source=design_time_userdata.db");

        return new UserDataContext(optionsBuilder.Options);
    }
}
