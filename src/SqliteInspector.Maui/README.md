# SqliteInspector.Maui

A lightweight SQLite database inspector for .NET MAUI apps. Browse tables, run queries, and monitor changes via a built-in web UI during development.

## Features

- **Built-in web UI** — browse tables, view schemas, and paginate rows from your browser
- **Live change detection** — SSE-based auto-refresh when database content changes
- **Ad-hoc queries** — run read-only SELECT queries from the inspector UI
- **Zero footprint in Release** — all registration is wrapped in `#if DEBUG`; Release builds get a no-op
- **Port negotiation** — automatically tries the next port if the default is busy
- **No dependencies on your app** — standalone package with no coupling to your domain models

## Installation

```shell
dotnet add package SqliteInspector.Maui
```

## Quick Start

In your `MauiProgram.cs`:

```csharp
using SqliteInspector.Maui;

var builder = MauiApp.CreateBuilder();
builder.UseMauiApp<App>();

// Register the inspector (DEBUG only — no-op in Release)
builder.Services.AddSqliteInspector(opts =>
{
    opts.DatabasePath = Path.Combine(FileSystem.AppDataDirectory, "app.db");
});

var app = builder.Build();

// Start the HTTP server (DEBUG only — no-op in Release)
app.Services.StartSqliteInspector();

return app;
```

Then open your browser to `http://localhost:8271` while the app is running in Debug mode.

## Configuration

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `DatabasePath` | `string` | `""` | **Required.** Absolute path to the SQLite database file. |
| `Port` | `int` | `8271` | HTTP port for the inspector web UI. |
| `MaxPortRetries` | `int` | `5` | Number of consecutive ports to try if the default is busy. |
| `AutoRefreshSeconds` | `int` | `2` | Polling interval (seconds) for change detection fallback. |

## API Endpoints

The inspector exposes these endpoints on `http://localhost:{port}`:

| Method | Path | Description |
|--------|------|-------------|
| GET | `/` | Inspector web UI (single-page HTML) |
| GET | `/api/info` | Database file path, size, SQLite version |
| GET | `/api/tables` | List all tables with row counts |
| GET | `/api/tables/{name}` | Paginated rows (`?offset=0&limit=100`) |
| GET | `/api/tables/{name}/schema` | Column definitions (name, type, PK, nullable) |
| GET | `/api/query?sql=SELECT...` | Execute a read-only SQL query |
| GET | `/api/changes` | SSE stream of table change events |

## How It Works

1. `AddSqliteInspector()` registers `SqliteInspectorOptions` and `DbInspectorServer` in DI (DEBUG only)
2. `StartSqliteInspector()` starts an EmbedIO `WebServer` on the configured port
3. Each request opens a fresh SQLite connection (`Pooling=false`, `Mode=ReadWrite`) to avoid stale WAL snapshots
4. A `ChangeDetector` polls the database and broadcasts SSE events to connected browsers
5. Only `SELECT` queries are allowed — mutation keywords are rejected

## Requirements

- .NET 10+
- Any MAUI target platform (Android, iOS, macOS, Windows)

## License

MIT
