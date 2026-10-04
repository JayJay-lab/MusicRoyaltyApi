using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MusicRoyaltyApi.Data;

namespace MusicRoyaltyApi.Controllers;

[ApiController]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _c;
    private readonly IConfiguration _cfg;

    public AuthController(AppDbContext c, IConfiguration cfg)
    {
        _c = c;
        _cfg = cfg;
    }

    // Prototype login: admin uses a password (appsettings "AdminPassword", default admin123),
    // clients log in with the email address registered on their user record.
    [HttpPost("api/auth/login")]
    public async Task<IActionResult> Login(LoginRequest r)
    {
        if (r.Role == "admin")
        {
            if (r.Username == "admin" && r.Password == (_cfg["AdminPassword"] ?? "admin123"))
                return Ok(new { role = "admin", name = "Administrator" });
            return Unauthorized("Wrong admin username or password.");
        }

        var u = await _c.Users.FirstOrDefaultAsync(x => x.Email == r.Username.Trim());
        if (u == null) return Unauthorized("No account found with that email. Please sign up first.");
        return Ok(new { role = "client", userId = u.UserId, name = u.FullName });
    }

    [HttpGet("api/lookup/roles")]
    public async Task<IActionResult> Roles() =>
        Ok(await _c.Roles.OrderBy(x => x.RoleName).ToListAsync());
}

public class LoginRequest
{
    public string Role { get; set; } = "client";
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

[ApiController]
[Route("api/tables")]
public class TableImportController : ControllerBase
{
    private static readonly Regex NameRx = new("^[a-z_][a-z0-9_]{0,62}$");
    private readonly AppDbContext _c;

    public TableImportController(AppDbContext c)
    {
        _c = c;
    }

    // Creates a table from a CSV file (first row = headers), guesses column types, loads all rows.
    [HttpPost("import")]
    public async Task<IActionResult> Import([FromForm] string tableName, IFormFile file)
    {
        var name = (tableName ?? "").Trim().ToLowerInvariant();
        if (!NameRx.IsMatch(name)) return BadRequest("Invalid table name (lowercase letters, digits, underscores).");
        if (file == null || file.Length == 0) return BadRequest("Choose a CSV file.");

        var lines = new List<List<string>>();
        using (var sr = new StreamReader(file.OpenReadStream(), Encoding.UTF8))
        {
            string? line;
            while ((line = await sr.ReadLineAsync()) != null)
                if (line.Trim() != "") lines.Add(ParseLine(line));
        }
        if (lines.Count < 2) return BadRequest("The CSV needs a header row and at least one data row.");

        var cols = new List<string>();
        foreach (var h in lines[0])
        {
            var n = Regex.Replace(h.Trim().ToLowerInvariant(), "[^a-z0-9_]", "_");
            if (n == "" || char.IsDigit(n[0])) n = "c_" + n;
            if (n == "id") n = "csv_id";
            while (cols.Contains(n)) n += "_2";
            cols.Add(n);
        }

        var rows = lines.Skip(1).Select(r => cols.Select((_, i) => i < r.Count ? r[i].Trim() : "").ToList()).ToList();
        var types = cols.Select((_, i) => Infer(rows.Select(r => r[i]))).ToList();

        var conn = _c.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            using (var chk = conn.CreateCommand())
            {
                chk.CommandText = $"SELECT to_regclass('public.\"{name}\"') IS NOT NULL";
                if ((bool)(await chk.ExecuteScalarAsync())!) return BadRequest($"A table named '{name}' already exists.");
            }

            using var tx = await conn.BeginTransactionAsync();
            try
            {
                using (var cr = conn.CreateCommand())
                {
                    cr.Transaction = tx;
                    cr.CommandText = $"CREATE TABLE \"{name}\" (id SERIAL PRIMARY KEY, " +
                                     string.Join(", ", cols.Select((c, i) => $"\"{c}\" {types[i]}")) + ")";
                    await cr.ExecuteNonQueryAsync();
                }

                var insertSql = $"INSERT INTO \"{name}\" ({string.Join(", ", cols.Select(c => $"\"{c}\""))}) " +
                                $"VALUES ({string.Join(", ", cols.Select((_, i) => "@p" + i))})";
                foreach (var r in rows)
                {
                    using var ins = conn.CreateCommand();
                    ins.Transaction = tx;
                    ins.CommandText = insertSql;
                    for (var i = 0; i < cols.Count; i++)
                    {
                        var p = ins.CreateParameter();
                        p.ParameterName = "p" + i;
                        p.Value = Convert(r[i], types[i]);
                        ins.Parameters.Add(p);
                    }
                    await ins.ExecuteNonQueryAsync();
                }

                await tx.CommitAsync();
                return Ok(new { table = name, rows = rows.Count, columns = cols });
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                return BadRequest("Import failed: " + ex.Message);
            }
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    private static object Convert(string v, string type)
    {
        if (v == "") return DBNull.Value;
        return type switch
        {
            "INTEGER" => int.Parse(v, CultureInfo.InvariantCulture),
            "NUMERIC(12,2)" => decimal.Parse(v, CultureInfo.InvariantCulture),
            "DATE" => DateOnly.Parse(v, CultureInfo.InvariantCulture),
            _ => v
        };
    }

    private static string Infer(IEnumerable<string> values)
    {
        var v = values.Where(x => x != "").ToList();
        if (v.Count == 0) return "TEXT";
        if (v.All(x => int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))) return "INTEGER";
        if (v.All(x => decimal.TryParse(x, NumberStyles.Number, CultureInfo.InvariantCulture, out _))) return "NUMERIC(12,2)";
        if (v.All(x => DateOnly.TryParse(x, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))) return "DATE";
        return "TEXT";
    }

    private static List<string> ParseLine(string line)
    {
        var res = new List<string>();
        var sb = new StringBuilder();
        var q = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (q)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (ch == '"') q = false;
                else sb.Append(ch);
            }
            else if (ch == '"') q = true;
            else if (ch == ',') { res.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        res.Add(sb.ToString());
        return res;
    }
}
