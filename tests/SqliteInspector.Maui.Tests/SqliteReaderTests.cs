using FluentAssertions;
using Microsoft.Data.Sqlite;
using SqliteInspector.Maui.Models;

namespace SqliteInspector.Maui.Tests;

public class SqliteReaderTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqliteReader _reader;

    public SqliteReaderTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE Users (
                Id INTEGER PRIMARY KEY,
                Name TEXT NOT NULL,
                Email TEXT,
                Age INTEGER DEFAULT 0
            );
            INSERT INTO Users (Name, Email, Age) VALUES ('Alice', 'alice@example.com', 30);
            INSERT INTO Users (Name, Email, Age) VALUES ('Bob', 'bob@example.com', 25);
            INSERT INTO Users (Name, Email, Age) VALUES ('Charlie', NULL, 40);

            CREATE TABLE Posts (
                Id INTEGER PRIMARY KEY,
                UserId INTEGER NOT NULL,
                Title TEXT NOT NULL,
                Body TEXT
            );
            INSERT INTO Posts (UserId, Title, Body) VALUES (1, 'Hello World', 'First post');
            INSERT INTO Posts (UserId, Title, Body) VALUES (1, 'Second Post', NULL);

            CREATE TABLE EmptyTable (
                Id INTEGER PRIMARY KEY,
                Value TEXT
            );
            """;
        cmd.ExecuteNonQuery();

        _reader = new SqliteReader(_connection);
    }

    [Fact]
    public async Task GetTablesAsync_ReturnsTables_ExcludingSqliteInternal()
    {
        var tables = await _reader.GetTablesAsync();

        tables.Should().HaveCount(3);
        tables.Select(t => t.Name).Should().BeEquivalentTo(["EmptyTable", "Posts", "Users"]);
        tables.Should().NotContain(t => t.Name.StartsWith("sqlite_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetTablesAsync_ReturnsCorrectRowCounts()
    {
        var tables = await _reader.GetTablesAsync();

        tables.First(t => t.Name == "Users").RowCount.Should().Be(3);
        tables.First(t => t.Name == "Posts").RowCount.Should().Be(2);
        tables.First(t => t.Name == "EmptyTable").RowCount.Should().Be(0);
    }

    [Fact]
    public async Task GetSchemaAsync_ReturnsColumnsWithTypes()
    {
        var schema = await _reader.GetSchemaAsync("Users");

        schema.TableName.Should().Be("Users");
        schema.Columns.Should().HaveCount(4);

        var idCol = schema.Columns.First(c => c.Name == "Id");
        idCol.Type.Should().Be("INTEGER");
        idCol.IsPrimaryKey.Should().BeTrue();

        var nameCol = schema.Columns.First(c => c.Name == "Name");
        nameCol.Type.Should().Be("TEXT");
        nameCol.NotNull.Should().BeTrue();

        var ageCol = schema.Columns.First(c => c.Name == "Age");
        ageCol.DefaultValue.Should().Be("0");
    }

    [Fact]
    public async Task GetSchemaAsync_InvalidTable_Throws()
    {
        var act = () => _reader.GetSchemaAsync("NonExistent");

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*'NonExistent'*does not exist*");
    }

    [Fact]
    public async Task GetRowsAsync_ReturnsPaginatedData()
    {
        var result = await _reader.GetRowsAsync("Users", offset: 0, limit: 2);

        result.ColumnNames.Should().Contain(["Id", "Name", "Email", "Age"]);
        result.Rows.Should().HaveCount(2);
        result.TotalRows.Should().Be(3);
    }

    [Fact]
    public async Task GetRowsAsync_WithOffset()
    {
        var result = await _reader.GetRowsAsync("Users", offset: 2, limit: 10);

        result.Rows.Should().HaveCount(1);
        result.TotalRows.Should().Be(3);
    }

    [Fact]
    public async Task GetRowsAsync_EmptyTable_ReturnsEmpty()
    {
        var result = await _reader.GetRowsAsync("EmptyTable");

        result.Rows.Should().BeEmpty();
        result.TotalRows.Should().Be(0);
        result.ColumnNames.Should().Contain(["Id", "Value"]);
    }

    [Fact]
    public async Task ExecuteQueryAsync_SelectQuery_ReturnsResults()
    {
        var result = await _reader.ExecuteQueryAsync("SELECT Name, Age FROM Users WHERE Age > 25");

        result.ColumnNames.Should().BeEquivalentTo(["Name", "Age"]);
        result.Rows.Should().HaveCount(2);
        result.Rows.Should().Contain(r => (string)r["Name"]! == "Alice");
        result.Rows.Should().Contain(r => (string)r["Name"]! == "Charlie");
    }

    [Fact]
    public async Task ExecuteQueryAsync_InsertQuery_Throws()
    {
        var act = () => _reader.ExecuteQueryAsync("INSERT INTO Users (Name) VALUES ('Eve')");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*SELECT*");
    }

    [Fact]
    public async Task ExecuteQueryAsync_DeleteQuery_Throws()
    {
        var act = () => _reader.ExecuteQueryAsync("DELETE FROM Users WHERE Id = 1");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*SELECT*");
    }

    [Fact]
    public async Task ExecuteQueryAsync_DropQuery_Throws()
    {
        var act = () => _reader.ExecuteQueryAsync("DROP TABLE Users");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*SELECT*");
    }

    [Fact]
    public async Task ExecuteQueryAsync_SelectWithSubqueryDelete_Throws()
    {
        var act = () => _reader.ExecuteQueryAsync("SELECT * FROM Users; DELETE FROM Users");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*DELETE*");
    }

    [Fact]
    public async Task GetRowsAsync_HandlesNullValues()
    {
        var result = await _reader.GetRowsAsync("Users");

        var charlie = result.Rows.First(r => (string)r["Name"]! == "Charlie");
        charlie["Email"].Should().BeNull();
    }

    [Fact]
    public async Task ExecuteQueryAsync_UpdateQuery_Throws()
    {
        var act = () => _reader.ExecuteQueryAsync("UPDATE Users SET Name = 'Evil' WHERE Id = 1");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*SELECT*");
    }

    [Fact]
    public async Task ExecuteQueryAsync_ReplaceQuery_Throws()
    {
        var act = () => _reader.ExecuteQueryAsync("REPLACE INTO Users (Id, Name) VALUES (1, 'Evil')");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*SELECT*");
    }

    [Fact]
    public async Task ExecuteQueryAsync_AlterQuery_Throws()
    {
        var act = () => _reader.ExecuteQueryAsync("ALTER TABLE Users ADD COLUMN Hacked TEXT");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*SELECT*");
    }

    [Fact]
    public async Task ExecuteQueryAsync_CreateQuery_Throws()
    {
        var act = () => _reader.ExecuteQueryAsync("CREATE TABLE Evil (Id INTEGER)");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*SELECT*");
    }

    [Fact]
    public async Task ExecuteQueryAsync_SqlInjection_SemicolonDrop_Throws()
    {
        var act = () => _reader.ExecuteQueryAsync("SELECT * FROM Users; DROP TABLE Users");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*DROP*");
    }

    [Fact]
    public async Task ExecuteQueryAsync_SqlInjection_UnionInsert_Throws()
    {
        var act = () => _reader.ExecuteQueryAsync("SELECT * FROM Users UNION INSERT INTO Users (Name) VALUES ('Evil')");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*INSERT*");
    }

    [Fact]
    public async Task ExecuteQueryAsync_SqlInjection_AttachDatabase_Throws()
    {
        var act = () => _reader.ExecuteQueryAsync("SELECT 1; ATTACH DATABASE '/tmp/evil.db' AS evil");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*ATTACH*");
    }

    [Fact]
    public async Task ExecuteQueryAsync_SelectContainingKeywordSubstring_Succeeds()
    {
        // "CREATED_AT" contains "CREATE" as a substring — should NOT be rejected
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "ALTER TABLE Users ADD COLUMN CreatedAt TEXT";
        cmd.ExecuteNonQuery();

        var result = await _reader.ExecuteQueryAsync("SELECT CreatedAt FROM Users");

        result.ColumnNames.Should().Contain("CreatedAt");
    }

    [Fact]
    public async Task GetSqliteVersionAsync_ReturnsVersionString()
    {
        var version = await _reader.GetSqliteVersionAsync();

        version.Should().MatchRegex(@"^\d+\.\d+\.\d+");
    }

    [Fact]
    public async Task GetRowsAsync_InvalidTable_Throws()
    {
        var act = () => _reader.GetRowsAsync("NonExistent");

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*'NonExistent'*does not exist*");
    }

    [Fact]
    public async Task GetRowsAsync_LimitZero_ReturnsNoRows()
    {
        var result = await _reader.GetRowsAsync("Users", offset: 0, limit: 0);

        result.Rows.Should().BeEmpty();
        result.TotalRows.Should().Be(3);
    }

    [Fact]
    public async Task GetRowsAsync_OffsetBeyondTotal_ReturnsEmpty()
    {
        var result = await _reader.GetRowsAsync("Users", offset: 100, limit: 10);

        result.Rows.Should().BeEmpty();
        result.TotalRows.Should().Be(3);
    }

    public void Dispose()
    {
        _reader.Dispose();
        _connection.Dispose();
    }
}
