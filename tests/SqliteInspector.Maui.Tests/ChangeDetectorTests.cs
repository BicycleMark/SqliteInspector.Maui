using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace SqliteInspector.Maui.Tests;

public class ChangeDetectorTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private readonly DbInspectorServer _server;
    private readonly HttpClient _client;
    private const int BasePort = 16555;

    public ChangeDetectorTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"change_test_{Guid.NewGuid():N}.db");
        CreateTestDatabase(_dbPath);

        var options = new SqliteInspectorOptions
        {
            Port = BasePort,
            MaxPortRetries = 10,
            DatabasePath = _dbPath,
            AutoRefreshSeconds = 1,
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
    public async Task Changes_ReturnsEventStream()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/changes");
        var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
    }

    [Fact]
    public async Task Changes_ReceivesEventOnDataChange()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Connect SSE client
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/changes");
        var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token);

        var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        // Give the client time to register
        await Task.Delay(200, cts.Token);

        // Insert a row into the database to trigger change detection
        InsertRow(_dbPath, "Users", "(NULL, 'Dave', 'dave@example.com', 35)");

        // Wait a bit for debounce (500ms) + detection
        await Task.Delay(1500, cts.Token);

        // Force a check in case FSW didn't fire (CI environments)
        await TriggerFallbackCheck();

        // Read SSE data
        var eventData = await ReadSseEventAsync(reader, cts.Token);

        eventData.Should().NotBeNullOrEmpty();

        using var doc = JsonDocument.Parse(eventData!);
        doc.RootElement.GetProperty("tables").GetArrayLength().Should().BeGreaterThan(0);
        doc.RootElement.GetProperty("timestamp").GetString().Should().NotBeNullOrEmpty();

        // Verify updated row count (was 3, now 4)
        var usersTable = doc.RootElement.GetProperty("tables").EnumerateArray()
            .First(t => t.GetProperty("name").GetString() == "Users");
        usersTable.GetProperty("rowCount").GetInt64().Should().Be(4);
    }

    [Fact]
    public async Task Changes_MultipleClients()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client2 = new HttpClient { BaseAddress = _client.BaseAddress };

        // Connect two SSE clients
        var response1 = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/changes"),
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token);

        var response2 = await client2.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/changes"),
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token);

        var stream1 = await response1.Content.ReadAsStreamAsync(cts.Token);
        var stream2 = await response2.Content.ReadAsStreamAsync(cts.Token);
        using var reader1 = new StreamReader(stream1);
        using var reader2 = new StreamReader(stream2);

        // Give clients time to register
        await Task.Delay(200, cts.Token);

        // Trigger a change
        InsertRow(_dbPath, "Users", "(NULL, 'Eve', 'eve@example.com', 28)");
        await Task.Delay(1500, cts.Token);
        await TriggerFallbackCheck();

        // Both clients should receive the event
        var event1 = await ReadSseEventAsync(reader1, cts.Token);
        var event2 = await ReadSseEventAsync(reader2, cts.Token);

        event1.Should().NotBeNullOrEmpty();
        event2.Should().NotBeNullOrEmpty();

        // Both should have the same table data
        using var doc1 = JsonDocument.Parse(event1!);
        using var doc2 = JsonDocument.Parse(event2!);

        doc1.RootElement.GetProperty("tables").GetArrayLength()
            .Should().Be(doc2.RootElement.GetProperty("tables").GetArrayLength());
    }

    [Fact]
    public async Task Changes_DisconnectedClientCleanedUp()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client2 = new HttpClient { BaseAddress = _client.BaseAddress };

        // Connect two SSE clients
        var response1 = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/changes"),
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token);

        var response2 = await client2.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/changes"),
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token);

        var stream2 = await response2.Content.ReadAsStreamAsync(cts.Token);
        using var reader2 = new StreamReader(stream2);

        // Give clients time to register
        await Task.Delay(200, cts.Token);

        // Disconnect client 1 by disposing its response
        response1.Dispose();
        await Task.Delay(200, cts.Token);

        // Trigger a change — should not throw despite disconnected client
        InsertRow(_dbPath, "Users", "(NULL, 'Frank', 'frank@example.com', 45)");
        await Task.Delay(1500, cts.Token);
        await TriggerFallbackCheck();

        // Client 2 should still receive the event
        var event2 = await ReadSseEventAsync(reader2, cts.Token);
        event2.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Changes_EventFormat()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Connect SSE client using raw stream
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/changes");
        var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token);

        var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        await Task.Delay(200, cts.Token);

        // Trigger a change
        InsertRow(_dbPath, "Posts", "(NULL, 1, 'New Post', 'Content')");
        await Task.Delay(1500, cts.Token);
        await TriggerFallbackCheck();

        // Read raw lines — skip SSE comment and event type, then expect "data: {json}\n\n"
        string? line;
        do
        {
            line = await ReadLineWithTimeoutAsync(reader, cts.Token);
        } while (line is not null && (line.Length == 0 || line.StartsWith(':') || line.StartsWith("event:")));

        line.Should().NotBeNull();
        line.Should().StartWith("data: ");

        var json = line!["data: ".Length..];
        using var doc = JsonDocument.Parse(json);

        // Verify camelCase property names
        doc.RootElement.TryGetProperty("tables", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("timestamp", out _).Should().BeTrue();

        var firstTable = doc.RootElement.GetProperty("tables").EnumerateArray().First();
        firstTable.TryGetProperty("name", out _).Should().BeTrue();
        firstTable.TryGetProperty("rowCount", out _).Should().BeTrue();
    }

    private async Task TriggerFallbackCheck()
    {
        // Use reflection to call CheckAndBroadcastAsync directly as a fallback
        // in case FileSystemWatcher doesn't fire in CI environments
        var serverType = _server.GetType();
        var detectorField = serverType.GetField("_changeDetector",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var detector = detectorField?.GetValue(_server) as ChangeDetector;
        if (detector is not null)
        {
            await detector.CheckAndBroadcastAsync();
        }
    }

    private static async Task<string?> ReadSseEventAsync(StreamReader reader, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await ReadLineWithTimeoutAsync(reader, ct);
            if (line is null)
                return null; // Timeout — no more data

            // Skip SSE comments (lines starting with ':') and empty lines
            if (line.Length == 0 || line.StartsWith(':'))
                continue;

            if (line.StartsWith("data: "))
            {
                return line["data: ".Length..];
            }
        }

        return null;
    }

    private static async Task<string?> ReadLineWithTimeoutAsync(StreamReader reader, CancellationToken ct)
    {
        try
        {
            var readTask = reader.ReadLineAsync(ct).AsTask();
            var completed = await Task.WhenAny(readTask, Task.Delay(5000, ct));

            if (completed == readTask)
            {
                return await readTask;
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    [Fact]
    public async Task Changes_NoChange_NoEventSent()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));

        // Connect SSE client
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/changes");
        var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token);

        var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        // Give client time to register
        await Task.Delay(200, cts.Token);

        // Do NOT modify the database — just trigger a poll
        await TriggerFallbackCheck();

        // Wait a bit then try to read — should timeout with no data event
        var eventData = await ReadSseEventAsync(reader, cts.Token);

        // No change was made, so no data event should have been sent
        eventData.Should().BeNull();
    }

    [Fact]
    public async Task Changes_WalMode_DeleteDetected()
    {
        // Simulate EF Core's WAL mode — this is how the device works
        using var walConnection = new SqliteConnection($"Data Source={_dbPath}");
        walConnection.Open();
        using var walCmd = walConnection.CreateCommand();
        walCmd.CommandText = "PRAGMA journal_mode=WAL";
        walCmd.ExecuteNonQuery();
        walConnection.Close();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Verify initial row count via API
        var before = await _client.GetStringAsync("/api/tables", cts.Token);
        using var docBefore = JsonDocument.Parse(before);
        var usersBefore = docBefore.RootElement.EnumerateArray()
            .First(t => t.GetProperty("name").GetString() == "Users");
        var countBefore = usersBefore.GetProperty("rowCount").GetInt64();
        countBefore.Should().Be(3);

        // Delete a row using a separate connection (like EF Core would)
        using var deleteConn = new SqliteConnection($"Data Source={_dbPath}");
        deleteConn.Open();
        using var deleteCmd = deleteConn.CreateCommand();
        deleteCmd.CommandText = "DELETE FROM Users WHERE Name = 'Alice'";
        deleteCmd.ExecuteNonQuery();
        deleteConn.Close();

        // Force the change detector to poll
        await TriggerFallbackCheck();

        // Check API reflects the delete
        var after = await _client.GetStringAsync("/api/tables", cts.Token);
        using var docAfter = JsonDocument.Parse(after);
        var usersAfter = docAfter.RootElement.EnumerateArray()
            .First(t => t.GetProperty("name").GetString() == "Users");
        var countAfter = usersAfter.GetProperty("rowCount").GetInt64();
        countAfter.Should().Be(2, "SqliteReader should see the delete made by another connection in WAL mode");
    }

    private static void InsertRow(string dbPath, string tableName, string values)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"INSERT INTO {tableName} VALUES {values}";
        cmd.ExecuteNonQuery();
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
