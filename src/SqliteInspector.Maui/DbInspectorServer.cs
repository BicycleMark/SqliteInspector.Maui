using System.Reflection;
using System.Text;
using System.Text.Json;
using EmbedIO;
using EmbedIO.Actions;
using Microsoft.Extensions.Logging;
using SqliteInspector.Maui.Models;

namespace SqliteInspector.Maui;

public sealed class DbInspectorServer : IDisposable
{
    private readonly SqliteInspectorOptions _options;
    private readonly ILogger<DbInspectorServer> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private WebServer? _webServer;
    private SqliteReader? _reader;
    private ChangeDetector? _changeDetector;
    private CancellationTokenSource? _cts;
    private Task? _serverTask;

    public int Port { get; private set; }

    public DbInspectorServer(SqliteInspectorOptions options, ILogger<DbInspectorServer> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _reader = new SqliteReader(_options.DatabasePath);
        _changeDetector = new ChangeDetector(
            _reader,
            _options.DatabasePath,
            _options.AutoRefreshSeconds,
            _logger);

        var started = false;
        for (var i = 0; i <= _options.MaxPortRetries; i++)
        {
            var port = _options.Port + i;
            var url = $"http://localhost:{port}/";

            try
            {
                var server = CreateWebServer(url);
                var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                var listeningTcs = new TaskCompletionSource();
                server.StateChanged += (_, e) =>
                {
                    if (e.NewState == WebServerState.Listening)
                        listeningTcs.TrySetResult();
                };

                var serverTask = server.RunAsync(cts.Token);

                // Wait for either: server starts listening, or server task fails
                var completed = await Task.WhenAny(listeningTcs.Task, serverTask);

                if (completed != listeningTcs.Task)
                {
                    // Server failed to start — propagate the exception
                    cts.Dispose();
                    server.Dispose();
                    await serverTask; // throws
                }

                _webServer = server;
                _cts = cts;
                _serverTask = serverTask;
                Port = port;
                started = true;
                _logger.LogInformation("SqliteInspector listening on http://localhost:{Port}/", Port);
                break;
            }
            catch (Exception) when (i < _options.MaxPortRetries)
            {
                _logger.LogWarning("Port {Port} unavailable, trying next...", port);
            }
        }

        if (!started)
        {
            throw new InvalidOperationException(
                $"Could not start HTTP listener on ports {_options.Port}–{_options.Port + _options.MaxPortRetries}.");
        }

        await _changeDetector.StartAsync(_cts!.Token);
    }

    public async Task StopAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
        }

        if (_changeDetector is not null)
        {
            await _changeDetector.StopAsync();
            _changeDetector.Dispose();
            _changeDetector = null;
        }

        if (_serverTask is not null)
        {
            try
            {
                await _serverTask;
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
            }
        }

        _webServer?.Dispose();
        _webServer = null;
        _reader?.Dispose();
        _reader = null;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _changeDetector?.Dispose();
        _webServer?.Dispose();
        _reader?.Dispose();
        _cts?.Dispose();
    }

    private WebServer CreateWebServer(string url)
    {
        return new WebServer(o => o
                .WithUrlPrefix(url)
                .WithMode(HttpListenerMode.EmbedIO))
            .WithModule(new ActionModule("/", HttpVerbs.Any, HandleRequestAsync));
    }

    private async Task HandleRequestAsync(IHttpContext context)
    {
        try
        {
            context.Response.Headers["Access-Control-Allow-Origin"] = "*";
            context.Response.Headers["Cache-Control"] = "no-cache, no-store";

            if (context.Request.HttpVerb == HttpVerbs.Options)
            {
                context.Response.Headers["Access-Control-Allow-Methods"] = "GET, OPTIONS";
                context.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
                context.Response.StatusCode = 204;
                return;
            }

            var path = context.RequestedPath.TrimEnd('/');

            if (path == "" || path == "/")
            {
                await HandleRoot(context);
            }
            else if (path == "/api/info")
            {
                await HandleGetInfo(context);
            }
            else if (path == "/api/tables")
            {
                await HandleGetTables(context);
            }
            else if (path == "/api/changes")
            {
                await HandleChanges(context);
                return; // SSE — don't close the response in the catch block
            }
            else if (path == "/api/query")
            {
                await HandleQuery(context);
            }
            else if (path.StartsWith("/api/tables/") && path.EndsWith("/schema"))
            {
                var tableName = ExtractTableName(path, "/api/tables/", "/schema");
                await HandleGetSchema(context, tableName);
            }
            else if (path.StartsWith("/api/tables/"))
            {
                var tableName = ExtractTableName(path, "/api/tables/", null);
                await HandleGetRows(context, tableName);
            }
            else
            {
                await WriteErrorResponse(context, 404, "Not found");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error processing request");
            try
            {
                await WriteErrorResponse(context, 500, ex.Message);
            }
            catch
            {
                // Response may already be closed
            }
        }
    }

    private static string ExtractTableName(string path, string prefix, string? suffix)
    {
        var name = path[prefix.Length..];
        if (suffix is not null && name.EndsWith(suffix))
        {
            name = name[..^suffix.Length];
        }

        return Uri.UnescapeDataString(name);
    }

    private async Task HandleRoot(IHttpContext context)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = "SqliteInspector.Maui.Assets.inspector.html";

        await using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            await WriteErrorResponse(context, 404, "inspector.html not found");
            return;
        }

        using var sr = new StreamReader(stream);
        var html = await sr.ReadToEndAsync();

        await context.SendStringAsync(html, "text/html; charset=utf-8", Encoding.UTF8);
    }

    private async Task HandleGetInfo(IHttpContext context)
    {
        var fullPath = Path.GetFullPath(_options.DatabasePath);
        var fileSize = new FileInfo(fullPath).Length;
        var sqliteVersion = await _reader!.GetSqliteVersionAsync();

        var info = new DatabaseInfo(fullPath, fileSize, sqliteVersion);
        await WriteJsonResponse(context, info);
    }

    private async Task HandleGetTables(IHttpContext context)
    {
        var tables = await _reader!.GetTablesAsync();
        await WriteJsonResponse(context, tables);
    }

    private async Task HandleGetRows(IHttpContext context, string tableName)
    {
        try
        {
            var query = context.GetRequestQueryData();
            var offset = int.TryParse(query["offset"], out var o) ? o : 0;
            var limit = int.TryParse(query["limit"], out var l) ? l : 100;

            var result = await _reader!.GetRowsAsync(tableName, offset, limit);
            await WriteJsonResponse(context, result);
        }
        catch (ArgumentException ex)
        {
            await WriteErrorResponse(context, 404, ex.Message);
        }
    }

    private async Task HandleGetSchema(IHttpContext context, string tableName)
    {
        try
        {
            var schema = await _reader!.GetSchemaAsync(tableName);
            await WriteJsonResponse(context, schema);
        }
        catch (ArgumentException ex)
        {
            await WriteErrorResponse(context, 404, ex.Message);
        }
    }

    private async Task HandleQuery(IHttpContext context)
    {
        var sql = context.GetRequestQueryData()["sql"];
        if (string.IsNullOrWhiteSpace(sql))
        {
            await WriteErrorResponse(context, 400, "Missing 'sql' query parameter");
            return;
        }

        try
        {
            var result = await _reader!.ExecuteQueryAsync(sql);
            await WriteJsonResponse(context, result);
        }
        catch (InvalidOperationException ex)
        {
            await WriteErrorResponse(context, 400, ex.Message);
        }
    }

    private async Task HandleChanges(IHttpContext context)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.SendChunked = true;
        context.Response.Headers["Cache-Control"] = "no-cache";

        // Send an SSE comment to flush headers to the client immediately
        var comment = Encoding.UTF8.GetBytes(": connected\n\n");
        await context.Response.OutputStream.WriteAsync(comment);
        await context.Response.OutputStream.FlushAsync();

        _changeDetector!.AddClient(context.Response.OutputStream);

        // Keep the connection alive until the server shuts down
        var tcs = new TaskCompletionSource();
        _cts!.Token.Register(() => tcs.TrySetResult());

        await tcs.Task;
    }

    private async Task WriteJsonResponse<T>(IHttpContext context, T data)
    {
        var json = JsonSerializer.Serialize(data, _jsonOptions);
        await context.SendStringAsync(json, "application/json; charset=utf-8", Encoding.UTF8);
    }

    private static async Task WriteErrorResponse(IHttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        var json = JsonSerializer.Serialize(new { error = message });
        await context.SendStringAsync(json, "application/json; charset=utf-8", Encoding.UTF8);
    }
}
