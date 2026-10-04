using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using MusicRoyaltyApi.Data;
using Npgsql;
using Microsoft.EntityFrameworkCore;

namespace MusicRoyaltyApi.Controllers;

// Generic row-level CRUD for any table (core or custom). Table and column names are checked
// against the database catalog, and all values are sent as parameters.
[ApiController]
[Route("api/data")]
public class DataController : ControllerBase
{
    private static readonly Regex Rx = new("^[a-z_][a-z0-9_]{0,62}$");
    private readonly AppDbContext _c;

    public DataController(AppDbContext c)
    {
        _c = c;
    }

    private record Col(string Name, string Type, bool Nullable, bool HasDefault, bool IsPk);

    [HttpGet("{table}")]
    public Task<IActionResult> List(string table) => Exec(table, async (t, conn, cols) =>
    {
        var pk = cols.FirstOrDefault(c => c.IsPk);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM \"{t}\" ORDER BY {(pk != null ? $"\"{pk.Name}\"" : "1")} LIMIT 500";
        var rows = new List<Dictionary<string, object?>>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var d = new Dictionary<string, object?>();
            for (var i = 0; i < r.FieldCount; i++) d[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
            rows.Add(d);
        }
        return Ok(new
        {
            columns = cols.Select(c => new { c.Name, c.Type, c.Nullable, c.HasDefault, c.IsPk }),
            readOnly = pk == null || t == "audit_log",
            rows
        });
    });

    [HttpPost("{table}")]
    public Task<IActionResult> Add(string table, [FromBody] Dictionary<string, JsonElement> body) =>
        Exec(table, async (t, conn, cols) =>
        {
            var g = Guard(t, cols);
            if (g != null) return g;
            var pairs = Pairs(body, cols, false);
            if (pairs.Count == 0) return BadRequest("No values supplied.");

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"INSERT INTO \"{t}\" ({string.Join(", ", pairs.Select(p => $"\"{p.Key}\""))}) " +
                              $"VALUES ({string.Join(", ", pairs.Select((_, i) => "@p" + i))})";
            AddParams(cmd, pairs);
            await cmd.ExecuteNonQueryAsync();
            return Ok(new { added = 1 });
        });

    [HttpPut("{table}/{id}")]
    public Task<IActionResult> Edit(string table, string id, [FromBody] Dictionary<string, JsonElement> body) =>
        Exec(table, async (t, conn, cols) =>
        {
            var g = Guard(t, cols);
            if (g != null) return g;
            var pk = cols.First(c => c.IsPk);
            var pairs = Pairs(body, cols, true);
            if (pairs.Count == 0) return BadRequest("Nothing to update.");

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"UPDATE \"{t}\" SET {string.Join(", ", pairs.Select((p, i) => $"\"{p.Key}\" = @p{i}"))} " +
                              $"WHERE \"{pk.Name}\" = @id";
            AddParams(cmd, pairs);
            AddParam(cmd, "id", Conv(id, pk.Type));
            if (await cmd.ExecuteNonQueryAsync() == 0) return NotFound("Row not found.");
            return Ok(new { updated = 1 });
        });

    [HttpDelete("{table}/{id}")]
    public Task<IActionResult> Remove(string table, string id) => Exec(table, async (t, conn, cols) =>
    {
        var g = Guard(t, cols);
        if (g != null) return g;
        var pk = cols.First(c => c.IsPk);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM \"{t}\" WHERE \"{pk.Name}\" = @id";
        AddParam(cmd, "id", Conv(id, pk.Type));
        if (await cmd.ExecuteNonQueryAsync() == 0) return NotFound("Row not found.");
        return Ok(new { deleted = 1 });
    });

    private async Task<IActionResult> Exec(string table, Func<string, DbConnection, List<Col>, Task<IActionResult>> f)
    {
        var t = (table ?? "").ToLowerInvariant();
        if (!Rx.IsMatch(t)) return BadRequest("Invalid table name.");

        var conn = _c.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            var cols = await Meta(conn, t);
            if (cols.Count == 0) return NotFound($"Table '{t}' does not exist.");
            return await f(t, conn, cols);
        }
        catch (PostgresException ex)
        {
            return BadRequest(ex.SqlState switch
            {
                "23503" => "This row is linked to other data. Change or remove the linked rows first.",
                "23505" => "That value already exists where it must be unique.",
                _ => ex.MessageText
            });
        }
        catch (FormatException)
        {
            return BadRequest("A value has the wrong format for its column.");
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    private IActionResult? Guard(string t, List<Col> cols)
    {
        if (t == "audit_log") return BadRequest("The audit log is read-only.");
        if (!cols.Any(c => c.IsPk)) return BadRequest("This table has no primary key, so it is read-only.");
        return null;
    }

    private static async Task<List<Col>> Meta(DbConnection conn, string t)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT c.column_name, c.data_type, c.is_nullable = 'YES', c.column_default IS NOT NULL,
            EXISTS (SELECT 1 FROM information_schema.table_constraints tc
                    JOIN information_schema.key_column_usage k
                      ON tc.constraint_name = k.constraint_name AND tc.table_schema = k.table_schema
                    WHERE tc.table_schema = 'public' AND tc.table_name = c.table_name
                      AND tc.constraint_type = 'PRIMARY KEY' AND k.column_name = c.column_name)
            FROM information_schema.columns c
            WHERE c.table_schema = 'public' AND c.table_name = @t ORDER BY c.ordinal_position";
        AddParam(cmd, "t", t);

        var list = new List<Col>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new Col(r.GetString(0), r.GetString(1), r.GetBoolean(2), r.GetBoolean(3), r.GetBoolean(4)));
        return list;
    }

    // Only real, non-key columns are used. Empty values are skipped on insert (so defaults apply)
    // and become NULL on update when the column allows it.
    private static List<KeyValuePair<string, object>> Pairs(Dictionary<string, JsonElement> body, List<Col> cols, bool update)
    {
        var res = new List<KeyValuePair<string, object>>();
        foreach (var c in cols)
        {
            if (c.IsPk || !body.TryGetValue(c.Name, out var e)) continue;
            var s = e.ValueKind == JsonValueKind.Null ? "" : e.ToString();
            if (s == "")
            {
                if (update && c.Nullable) res.Add(new(c.Name, DBNull.Value));
                continue;
            }
            res.Add(new(c.Name, Conv(s, c.Type)));
        }
        return res;
    }

    private static object Conv(string s, string type) => type switch
    {
        "integer" or "smallint" => int.Parse(s, CultureInfo.InvariantCulture),
        "bigint" => long.Parse(s, CultureInfo.InvariantCulture),
        "numeric" or "double precision" or "real" => decimal.Parse(s, CultureInfo.InvariantCulture),
        "date" => DateOnly.Parse(s, CultureInfo.InvariantCulture),
        "timestamp without time zone" => DateTime.Parse(s, CultureInfo.InvariantCulture),
        "boolean" => bool.Parse(s),
        _ => s
    };

    private static void AddParam(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    private static void AddParams(DbCommand cmd, List<KeyValuePair<string, object>> pairs)
    {
        for (var i = 0; i < pairs.Count; i++) AddParam(cmd, "p" + i, pairs[i].Value);
    }
}
