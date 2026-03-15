using Microsoft.Data.Sqlite;

namespace SqliteInspector.Sample;

public class NoteDatabase
{
    private readonly string _connectionString;

    public NoteDatabase(string dbPath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

        Initialize();
    }

    private void Initialize()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Notes (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Title TEXT NOT NULL,
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now'))
            )
            """;
        command.ExecuteNonQuery();

        command.CommandText = "SELECT COUNT(*) FROM Notes";
        var count = (long)command.ExecuteScalar()!;

        if (count == 0)
        {
            command.CommandText = """
                INSERT INTO Notes (Title) VALUES ('Welcome to SqliteInspector!');
                INSERT INTO Notes (Title) VALUES ('Open http://localhost:8271 in your browser');
                INSERT INTO Notes (Title) VALUES ('Add and delete notes to see live updates');
                """;
            command.ExecuteNonQuery();
        }
    }

    public async Task<List<Note>> GetAllAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Title, CreatedAt FROM Notes ORDER BY Id DESC";

        var notes = new List<Note>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            notes.Add(new Note(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2)));
        }

        return notes;
    }

    public async Task AddAsync(string title)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Notes (Title) VALUES (@title)";
        command.Parameters.AddWithValue("@title", title);
        await command.ExecuteNonQueryAsync();
    }

    public async Task DeleteAsync(long id)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Notes WHERE Id = @id";
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync();
    }
}

public record Note(long Id, string Title, string CreatedAt);
