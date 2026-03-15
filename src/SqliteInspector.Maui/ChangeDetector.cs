using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SqliteInspector.Maui.Models;

namespace SqliteInspector.Maui;

public sealed class ChangeDetector : IDisposable
{
    private readonly SqliteReader _reader;
    private readonly string _databasePath;
    private readonly int _pollIntervalSeconds;
    private readonly ILogger _logger;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly List<Stream> _clients = new();
    private readonly object _clientLock = new();

    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private Dictionary<string, long> _lastSnapshot = new();

    public ChangeDetector(
        SqliteReader reader,
        string databasePath,
        int pollIntervalSeconds,
        ILogger logger)
    {
        _reader = reader;
        _databasePath = databasePath;
        _pollIntervalSeconds = pollIntervalSeconds;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Take initial snapshot before starting watchers
        try
        {
            var tables = await _reader.GetTablesAsync();
            _lastSnapshot = tables.ToDictionary(t => t.Name, t => t.RowCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to take initial snapshot");
        }

        var pollMs = _pollIntervalSeconds * 1000;
        _logger.LogInformation("ChangeDetector started, polling every {Ms}ms", pollMs);
        _pollTask = Task.Run(async () =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(pollMs, _cts.Token);
                    await CheckAndBroadcastAsync();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Poll error");
                }
            }
        }, _cts.Token);
    }

    public async Task StopAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
        }

        if (_pollTask is not null)
        {
            try
            {
                await _pollTask;
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }

        lock (_clientLock)
        {
            foreach (var client in _clients)
            {
                try { client.Close(); } catch { }
            }
            _clients.Clear();
        }
    }

    public void AddClient(Stream outputStream)
    {
        lock (_clientLock)
        {
            _clients.Add(outputStream);
            _logger.LogInformation("SSE client connected, total clients: {Count}", _clients.Count);
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();

        lock (_clientLock)
        {
            foreach (var client in _clients)
            {
                try { client.Close(); } catch { }
            }
            _clients.Clear();
        }
    }

    internal async Task CheckAndBroadcastAsync()
    {
        try
        {
            var tables = await _reader.GetTablesAsync();
            var currentSnapshot = tables.ToDictionary(t => t.Name, t => t.RowCount);
            var counts = string.Join(", ", currentSnapshot.Select(kv => $"{kv.Key}={kv.Value}"));
            _logger.LogDebug("Poll: {Counts}", counts);

            if (!SnapshotsEqual(_lastSnapshot, currentSnapshot))
            {
                _logger.LogInformation("Change detected, broadcasting to {Count} clients", _clients.Count);
                _lastSnapshot = currentSnapshot;
                await BroadcastAsync(tables);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking for changes");
        }
    }

    private static bool SnapshotsEqual(
        Dictionary<string, long> previous,
        Dictionary<string, long> current)
    {
        if (previous.Count != current.Count)
            return false;

        foreach (var (name, count) in current)
        {
            if (!previous.TryGetValue(name, out var prevCount) || prevCount != count)
                return false;
        }

        return true;
    }

    private async Task BroadcastAsync(IReadOnlyList<TableInfo> tables)
    {
        var payload = new
        {
            tables = tables.Select(t => new { t.Name, t.RowCount }),
            timestamp = DateTime.UtcNow.ToString("o"),
        };

        var json = JsonSerializer.Serialize(payload, _jsonOptions);
        var sseData = $"event: change\ndata: {json}\n\n";
        var bytes = Encoding.UTF8.GetBytes(sseData);

        List<Stream> snapshot;
        lock (_clientLock)
        {
            snapshot = new List<Stream>(_clients);
        }

        var disconnected = new List<Stream>();

        foreach (var client in snapshot)
        {
            try
            {
                await client.WriteAsync(bytes);
                await client.FlushAsync();
            }
            catch
            {
                disconnected.Add(client);
            }
        }

        if (disconnected.Count > 0)
        {
            lock (_clientLock)
            {
                foreach (var client in disconnected)
                {
                    _clients.Remove(client);
                }
            }
        }
    }
}
