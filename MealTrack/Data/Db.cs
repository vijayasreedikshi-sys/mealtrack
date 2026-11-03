using System.Data;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace MealTrack.Data;

public class DbOptions
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 3306;
    public string User { get; set; } = "root";
    public string Password { get; set; } = "";
    public string Name { get; set; } = "mealtrack";
}

public class AppOptions
{
    public int MaxQtyPerMeal { get; set; } = 1;               // one person = 1 portion per meal per day (matches CHECK in schema.sql)
    public int ReminderHoursBeforeCutoff { get; set; } = 2;   // deadline reminder window
    public int SweepIntervalSeconds { get; set; } = 60;       // how often the lock/notify sweep runs
    public int PasswordResetMinutes { get; set; } = 30;
    public bool ShowDemoAccounts { get; set; } = true;
    public string PublicBaseUrl { get; set; } = "";           // e.g. http://192.168.1.20:5000 (blank = address the screen was opened with)
}

/// <summary>
/// MySQL access helpers. Every query uses Dapper parameters (@name),
/// so values are never concatenated into SQL. Connections come from the
/// MySqlConnector pool, one per call (or per transaction).
/// </summary>
public class Db(IOptions<DbOptions> options)
{
    private readonly DbOptions _o = options.Value;

    public string DatabaseName => _o.Name;

    public string ConnectionString(bool withDatabase = true)
    {
        var b = new MySqlConnectionStringBuilder
        {
            Server = _o.Host,
            Port = (uint)_o.Port,
            UserID = _o.User,
            Password = _o.Password,
            CharacterSet = "utf8mb4",
        };
        if (withDatabase) b.Database = _o.Name;
        return b.ConnectionString;
    }

    public async Task<MySqlConnection> OpenAsync(bool withDatabase = true)
    {
        var conn = new MySqlConnection(ConnectionString(withDatabase));
        await conn.OpenAsync();
        return conn;
    }

    public async Task<List<T>> QueryAsync<T>(string sql, object? param = null)
    {
        await using var conn = await OpenAsync();
        return (await conn.QueryAsync<T>(sql, param)).AsList();
    }

    public async Task<T?> QueryOneAsync<T>(string sql, object? param = null)
    {
        await using var conn = await OpenAsync();
        return await conn.QueryFirstOrDefaultAsync<T>(sql, param);
    }

    public async Task<T> ScalarAsync<T>(string sql, object? param = null, T fallback = default!)
    {
        await using var conn = await OpenAsync();
        var value = await conn.ExecuteScalarAsync(sql, param);
        if (value is null || value is DBNull) return fallback;
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(value, target);
    }

    public async Task<int> ExecuteAsync(string sql, object? param = null)
    {
        await using var conn = await OpenAsync();
        return await conn.ExecuteAsync(sql, param);
    }

    /// <summary>INSERT and return the new AUTO_INCREMENT id.</summary>
    public async Task<long> InsertAsync(string sql, object? param = null)
    {
        await using var conn = await OpenAsync();
        return await conn.ExecuteScalarAsync<long>(sql + "; SELECT LAST_INSERT_ID();", param);
    }

    /// <summary>Run work in one transaction: commit on success, roll back on any exception.</summary>
    public async Task<T> InTransactionAsync<T>(Func<MySqlConnection, IDbTransaction, Task<T>> work)
    {
        await using var conn = await OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            var result = await work(conn, tx);
            await tx.CommitAsync();
            return result;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    public Task InTransactionAsync(Func<MySqlConnection, IDbTransaction, Task> work) =>
        InTransactionAsync<bool>(async (c, t) => { await work(c, t); return true; });
}
