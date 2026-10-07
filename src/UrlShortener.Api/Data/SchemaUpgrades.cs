using System.Data;
using Microsoft.EntityFrameworkCore;

namespace UrlShortener.Api.Data;

/// <summary>
/// Minimal, idempotent schema upgrades for the SQLite development store. <c>EnsureCreated</c> only creates a database
/// from scratch and never alters existing tables, so a column added after a database was first created must be
/// patched in here or the first query fails with "no such column". This is the one hand-rolled migration in the
/// service; production deployments should use EF Core migrations instead.
/// </summary>
public static class SchemaUpgrades
{
    public static void Apply(AppDbContext db)
    {
        if (!db.Database.IsSqlite()) return;
        EnsureColumn(db, table: "Links", column: "ManageTokenHash", definition: "TEXT NULL");
    }

    private static void EnsureColumn(AppDbContext db, string table, string column, string definition)
    {
        var connection = db.Database.GetDbConnection();
        var wasClosed = connection.State != ConnectionState.Open;
        if (wasClosed) connection.Open();
        try
        {
            using var check = connection.CreateCommand();
            check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
            if (Convert.ToInt64(check.ExecuteScalar()) > 0) return;

            using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition}";
            alter.ExecuteNonQuery();
        }
        finally
        {
            if (wasClosed) connection.Close();
        }
    }
}
