using Microsoft.Data.Sqlite;
using SqliteInspector.Maui.Models;

namespace SqliteInspector.Maui;

public sealed class SqliteReader : IDisposable
{
    private static readonly string[] MutationKeywords =
        ["INSERT", "UPDATE", "DELETE", "DROP", "ALTER", "CREATE", "REPLACE", "TRUNCATE", "ATTACH", "DETACH"];

    private readonly string? _connectionString;
    private readonly SqliteConnection? _persistentConnection;

    public SqliteReader(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString();
    }

    internal SqliteReader(SqliteConnection connection)
    {
        _persistentConnection = connection;
        if (_persistentConnection.State != System.Data.ConnectionState.Open)
        {
            _persistentConnection.Open();
        }
    }

    public async Task<IReadOnlyList<TableInfo>> GetTablesAsync()
    {
        await using var lease = await LeaseConnectionAsync();

        var tables = new List<TableInfo>();

        using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";

        using var reader = await cmd.ExecuteReaderAsync();
        var tableNames = new List<string>();
        while (await reader.ReadAsync())
        {
            tableNames.Add(reader.GetString(0));
        }

        foreach (var name in tableNames)
        {
            using var countCmd = lease.Connection.CreateCommand();
            countCmd.CommandText = $"SELECT COUNT(*) FROM \"{EscapeIdentifier(name)}\"";
            var count = (long)(await countCmd.ExecuteScalarAsync())!;
            tables.Add(new TableInfo(name, count));
        }

        return tables;
    }

    public async Task<TableSchema> GetSchemaAsync(string tableName)
    {
        await using var lease = await LeaseConnectionAsync();
        await ValidateTableNameAsync(lease.Connection, tableName);

        var columns = new List<ColumnInfo>();

        using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{EscapeIdentifier(tableName)}\")";

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(new ColumnInfo(
                Cid: reader.GetInt32(0),
                Name: reader.GetString(1),
                Type: reader.GetString(2),
                NotNull: reader.GetInt32(3) != 0,
                DefaultValue: reader.IsDBNull(4) ? null : reader.GetString(4),
                IsPrimaryKey: reader.GetInt32(5) != 0
            ));
        }

        return new TableSchema(tableName, columns);
    }

    public async Task<QueryResult> GetRowsAsync(string tableName, int offset = 0, int limit = 100)
    {
        await using var lease = await LeaseConnectionAsync();
        await ValidateTableNameAsync(lease.Connection, tableName);

        var escapedName = EscapeIdentifier(tableName);

        using var countCmd = lease.Connection.CreateCommand();
        countCmd.CommandText = $"SELECT COUNT(*) FROM \"{escapedName}\"";
        var totalRows = (long)(await countCmd.ExecuteScalarAsync())!;

        using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = $"SELECT * FROM \"{escapedName}\" LIMIT @limit OFFSET @offset";
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", offset);

        return await ReadQueryResultAsync(cmd, totalRows);
    }

    public async Task<QueryResult> ExecuteQueryAsync(string sql)
    {
        ValidateSqlIsReadOnly(sql);

        await using var lease = await LeaseConnectionAsync();

        using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = sql;

        return await ReadQueryResultAsync(cmd, totalRows: -1);
    }

    public async Task<string> GetSqliteVersionAsync()
    {
        await using var lease = await LeaseConnectionAsync();

        using var cmd = lease.Connection.CreateCommand();
        cmd.CommandText = "SELECT sqlite_version()";
        var result = await cmd.ExecuteScalarAsync();
        return result?.ToString() ?? "unknown";
    }

    public void Dispose()
    {
        _persistentConnection?.Dispose();
    }

    private async Task<ConnectionLease> LeaseConnectionAsync()
    {
        if (_persistentConnection is not null)
        {
            return new ConnectionLease(_persistentConnection, owned: false);
        }

        var connection = new SqliteConnection(_connectionString!);
        await connection.OpenAsync();
        return new ConnectionLease(connection, owned: true);
    }

    private static async Task<QueryResult> ReadQueryResultAsync(SqliteCommand cmd, long totalRows)
    {
        using var reader = await cmd.ExecuteReaderAsync();

        var columnNames = new List<string>();
        for (var i = 0; i < reader.FieldCount; i++)
        {
            columnNames.Add(reader.GetName(i));
        }

        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[columnNames[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        var resultTotal = totalRows >= 0 ? totalRows : rows.Count;
        return new QueryResult(columnNames, rows, resultTotal);
    }

    private static async Task ValidateTableNameAsync(SqliteConnection connection, string tableName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name";
        cmd.Parameters.AddWithValue("@name", tableName);

        var count = (long)(await cmd.ExecuteScalarAsync())!;
        if (count == 0)
        {
            throw new ArgumentException($"Table '{tableName}' does not exist.", nameof(tableName));
        }
    }

    private static void ValidateSqlIsReadOnly(string sql)
    {
        var trimmed = sql.TrimStart();
        if (!trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only SELECT queries are allowed.");
        }

        var upper = sql.ToUpperInvariant();
        foreach (var keyword in MutationKeywords)
        {
            var index = 0;
            while ((index = upper.IndexOf(keyword, index, StringComparison.Ordinal)) >= 0)
            {
                var before = index == 0 || !char.IsLetterOrDigit(upper[index - 1]);
                var afterPos = index + keyword.Length;
                var after = afterPos >= upper.Length || !char.IsLetterOrDigit(upper[afterPos]);

                if (before && after && keyword != "SELECT")
                {
                    throw new InvalidOperationException($"Query contains disallowed keyword: {keyword}");
                }

                index += keyword.Length;
            }
        }
    }

    private static string EscapeIdentifier(string identifier) =>
        identifier.Replace("\"", "\"\"");

    /// <summary>
    /// Wraps a SqliteConnection with conditional ownership. Per-query connections
    /// are disposed after use; the persistent test connection is left open.
    /// </summary>
    private readonly struct ConnectionLease(SqliteConnection connection, bool owned) : IAsyncDisposable
    {
        public SqliteConnection Connection { get; } = connection;

        public ValueTask DisposeAsync() =>
            owned ? Connection.DisposeAsync() : ValueTask.CompletedTask;
    }
}
