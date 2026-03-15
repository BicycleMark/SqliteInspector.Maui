using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace SqliteInspector.Maui.Tests;

public class DbInspectorServerTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private readonly DbInspectorServer _server;
    private readonly HttpClient _client;
    private const int BasePort = 15555;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public DbInspectorServerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"inspector_test_{Guid.NewGuid():N}.db");
        CreateTestDatabase(_dbPath);

        var options = new SqliteInspectorOptions
        {
            Port = BasePort,
            MaxPortRetries = 10,
            DatabasePath = _dbPath,
        };

        _server = new DbInspectorServer(options, NullLogger<DbInspectorServer>.Instance);
        _client = new HttpClient();
    }

    public async Task InitializeAsync()
    {
        await _server.StartAsync();
        _client.BaseAddress = new Uri($"http://localhost:{_server.Port}");
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _server.StopAsync();
        _server.Dispose();
        SqliteConnection.ClearAllPools();

        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [Fact]
    public async Task GetTables_ReturnsJsonArray()
    {
        var response = await _client.GetAsync("/api/tables");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        doc.RootElement.GetArrayLength().Should().Be(3);

        var tableNames = doc.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("name").GetString())
            .ToList();

        tableNames.Should().Contain(["Users", "Posts", "EmptyTable"]);
    }

    [Fact]
    public async Task GetRows_ReturnsPagedResults()
    {
        var response = await _client.GetAsync("/api/tables/Users");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("totalRows").GetInt64().Should().Be(3);
        doc.RootElement.GetProperty("rows").GetArrayLength().Should().Be(3);
        doc.RootElement.GetProperty("columnNames").GetArrayLength().Should().Be(4);
    }

    [Fact]
    public async Task GetRows_WithPagination()
    {
        var response = await _client.GetAsync("/api/tables/Users?offset=1&limit=1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("totalRows").GetInt64().Should().Be(3);
        doc.RootElement.GetProperty("rows").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task GetSchema_ReturnsColumnInfo()
    {
        var response = await _client.GetAsync("/api/tables/Users/schema");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("tableName").GetString().Should().Be("Users");

        var columns = doc.RootElement.GetProperty("columns");
        columns.GetArrayLength().Should().Be(4);

        var idCol = columns.EnumerateArray().First(c => c.GetProperty("name").GetString() == "Id");
        idCol.GetProperty("type").GetString().Should().Be("INTEGER");
        idCol.GetProperty("isPrimaryKey").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task GetSchema_InvalidTable_Returns404()
    {
        var response = await _client.GetAsync("/api/tables/NonExistent/schema");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("error").GetString().Should().Contain("NonExistent");
    }

    [Fact]
    public async Task ExecuteQuery_ReturnsResults()
    {
        var sql = Uri.EscapeDataString("SELECT Name FROM Users WHERE Age > 25");
        var response = await _client.GetAsync($"/api/query?sql={sql}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("rows").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task ExecuteQuery_NonSelect_Returns400()
    {
        var sql = Uri.EscapeDataString("DELETE FROM Users WHERE Id = 1");
        var response = await _client.GetAsync($"/api/query?sql={sql}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("error").GetString().Should().NotBeEmpty();
    }

    [Fact]
    public async Task GetInfo_ReturnsDatabaseMetadata()
    {
        var response = await _client.GetAsync("/api/info");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("databasePath").GetString().Should().NotBeNullOrEmpty();
        doc.RootElement.GetProperty("fileSize").GetInt64().Should().BeGreaterThan(0);
        doc.RootElement.GetProperty("sqliteVersion").GetString().Should().MatchRegex(@"^\d+\.\d+");
    }

    [Fact]
    public async Task GetRoot_ReturnsHtml()
    {
        var response = await _client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");

        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("SQLite Inspector");
    }

    [SkippableFact]
    public async Task PortFallback_UsesNextAvailable()
    {
        // EmbedIO uses SO_REUSEPORT internally, so socket-level port blocking
        // doesn't work. Instead, use privileged ports (< 1024) which require root.
        // Skip this test when running as root (CI containers, etc.).
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var canBindPrivileged = false;
        try
        {
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 1020));
            canBindPrivileged = true;
        }
        catch (SocketException)
        {
            // Expected on non-root — port 1020 is privileged
        }

        Skip.If(canBindPrivileged, "Test requires non-root (privileged ports must be restricted)");

        // Ports 1020-1023 are privileged (will fail), 1024+ are non-privileged
        var startPort = 1020;

        var dbPath2 = Path.Combine(Path.GetTempPath(), $"inspector_test_{Guid.NewGuid():N}.db");
        CreateTestDatabase(dbPath2);

        var options2 = new SqliteInspectorOptions
        {
            Port = startPort,
            MaxPortRetries = 20,
            DatabasePath = dbPath2,
        };

        var server2 = new DbInspectorServer(options2, NullLogger<DbInspectorServer>.Instance);

        try
        {
            await server2.StartAsync();
            server2.Port.Should().BeGreaterThan(startPort);

            // Verify it's actually serving
            using var client2 = new HttpClient { BaseAddress = new Uri($"http://localhost:{server2.Port}") };
            var response = await client2.GetAsync("/api/tables");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await server2.StopAsync();
            server2.Dispose();
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath2))
            {
                File.Delete(dbPath2);
            }
        }
    }

    [Fact]
    public async Task CorsHeaders_Present()
    {
        var response = await _client.GetAsync("/api/tables");

        response.Headers.GetValues("Access-Control-Allow-Origin")
            .Should().Contain("*");
    }

    [Fact]
    public async Task UnknownRoute_Returns404()
    {
        var response = await _client.GetAsync("/api/nonexistent");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("error").GetString().Should().Contain("Not found");
    }

    [Fact]
    public async Task ExecuteQuery_MissingSqlParam_Returns400()
    {
        var response = await _client.GetAsync("/api/query");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("error").GetString().Should().Contain("sql");
    }

    [Fact]
    public async Task OptionsRequest_ReturnsCorsHeaders()
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/tables");
        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.GetValues("Access-Control-Allow-Methods")
            .Should().Contain("GET, OPTIONS");
        response.Headers.GetValues("Access-Control-Allow-Headers")
            .Should().Contain("Content-Type");
    }

    [Fact]
    public async Task GetRows_InvalidTable_Returns404()
    {
        var response = await _client.GetAsync("/api/tables/NonExistent");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("error").GetString().Should().Contain("NonExistent");
    }

    [Fact]
    public async Task NoCacheHeaders_Present()
    {
        var response = await _client.GetAsync("/api/tables");

        var cacheControl = string.Join(", ", response.Headers.GetValues("Cache-Control"));
        cacheControl.Should().Contain("no-cache");
        cacheControl.Should().Contain("no-store");
    }

    private static void CreateTestDatabase(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();

        using var cmd = connection.CreateCommand();
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
    }
}
