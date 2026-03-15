# Operating the SQLite Inspector

SqliteInspector.Maui is a lightweight, in-app database browser for .NET MAUI apps. It runs an embedded HTTP server (EmbedIO) inside your app during Debug builds, serving a web UI for browsing tables, viewing schemas, running queries, and receiving live updates via SSE.

## Prerequisites

- .NET 10 SDK
- A MAUI app with a SQLite database
- A browser on the same machine (or port forwarding for devices)

## Quick Start

### 1. Add the package

```bash
dotnet add src/apps/YourApp package SqliteInspector.Maui
```

### 2. Register in MauiProgram.cs

```csharp
using SqliteInspector.Maui;

// Register (DEBUG only — no-op in Release)
builder.Services.AddSqliteInspector(opts =>
{
    opts.DatabasePath = Path.Combine(FileSystem.AppDataDirectory, "app.db");
});

var app = builder.Build();

// Start the HTTP server (DEBUG only — no-op in Release)
app.Services.StartSqliteInspector();
```

### 3. Open the inspector

Run your app in Debug mode and open your browser to:

```
http://localhost:8271
```

## Configuration

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `DatabasePath` | `string` | `""` | **Required.** Absolute path to the SQLite database file. |
| `Port` | `int` | `8271` | HTTP port for the inspector web UI. |
| `MaxPortRetries` | `int` | `5` | Consecutive ports to try if the default is busy. |
| `AutoRefreshSeconds` | `int` | `2` | Polling interval (seconds) for change detection. |

### PicPins example

PicPins reads the port from user settings, allowing runtime configuration via the Settings page:

```csharp
builder.Services.AddSqliteInspector(opts =>
{
    opts.DatabasePath = dbPath;
    opts.Port = settingsService.SqliteInspectorPort ?? ISettingsService.DefaultSqliteInspectorPort;
});
```

## Platform Setup

### iOS Simulator

No extra setup needed. The Simulator shares the host network.

```
http://localhost:8271
```

### Android Emulator

The Android emulator runs in its own network. Use ADB port forwarding to access the inspector from your host browser:

```bash
adb forward tcp:8271 tcp:8271
```

Then open `http://localhost:8271` as usual.

To remove the forwarding:

```bash
adb forward --remove tcp:8271
```

### Physical iOS Device (USB)

Use `iproxy` from `libimobiledevice`:

```bash
brew install libimobiledevice
iproxy 8271 8271
```

Then open `http://localhost:8271`.

### Physical Android Device (USB)

Same as emulator — ADB forwarding works over USB:

```bash
adb forward tcp:8271 tcp:8271
```

## Using the Inspector UI

### Table Browser

The left sidebar lists all tables with row counts. Click a table to browse its rows with pagination (100 rows per page).

### Schema View

Click "Schema" on any table to see column definitions: name, type, primary key, nullable, and default values.

### SQL Queries

The query bar at the top accepts read-only `SELECT` queries. Mutation keywords (`INSERT`, `UPDATE`, `DELETE`, `DROP`, etc.) are rejected.

Examples:

```sql
SELECT * FROM Pins WHERE Latitude > 30.0
SELECT Name, COUNT(*) FROM Tags GROUP BY Name
SELECT p.Title, t.Name FROM Pins p JOIN PinTags pt ON p.Id = pt.PinId JOIN Tags t ON pt.TagId = t.Id
```

### Live Updates

The inspector connects via Server-Sent Events (SSE) for real-time updates. When you add, edit, or delete data in the app, the browser refreshes automatically — no manual reload needed.

The toolbar shows the database filename and file size.

## API Endpoints

All endpoints are available at `http://localhost:{port}`:

| Method | Path | Description |
|--------|------|-------------|
| GET | `/` | Inspector web UI (single-page HTML) |
| GET | `/api/info` | Database file path, size, SQLite version |
| GET | `/api/tables` | List all tables with row counts |
| GET | `/api/tables/{name}` | Paginated rows (`?offset=0&limit=100`) |
| GET | `/api/tables/{name}/schema` | Column definitions |
| GET | `/api/query?sql=SELECT...` | Execute a read-only SQL query |
| GET | `/api/changes` | SSE stream of table change events |

### curl examples

```bash
# List tables
curl http://localhost:8271/api/tables | jq

# Get rows with pagination
curl "http://localhost:8271/api/tables/Pins?offset=0&limit=10" | jq

# Run a query
curl "http://localhost:8271/api/query?sql=SELECT%20COUNT(*)%20FROM%20Pins" | jq

# Database info
curl http://localhost:8271/api/info | jq
```

## How It Works

1. `AddSqliteInspector()` registers `SqliteInspectorOptions` and `DbInspectorServer` in DI (`#if DEBUG` only)
2. `StartSqliteInspector()` starts an EmbedIO `WebServer` on the configured port
3. Each request opens a fresh SQLite connection (`Pooling=false`, `Mode=ReadWrite`) to avoid stale WAL snapshots
4. A `ChangeDetector` polls the database at the configured interval, comparing row counts and checksums, then broadcasts SSE events to connected browsers
5. Only `SELECT` queries are allowed — mutation keywords are rejected at the API level

## Troubleshooting

### Port conflict — browser shows wrong database

If you run both iOS Simulator and Android emulator simultaneously, both apps try port 8271. The first one wins; the second falls back to 8272+.

**Check which process holds the port:**

```bash
lsof -i :8271
```

**Fix:** Kill the process you don't need, or change the port in the app's Settings page. Check the app's debug log for the actual port:

```
SqliteInspector listening on http://localhost:8272/
```

### Android emulator — browser can't connect

Ensure ADB forwarding is set up and points to the correct port:

```bash
# Check existing forwards
adb forward --list

# Set up forwarding
adb forward tcp:8271 tcp:8271
```

If the app fell back to a different port, forward that port instead:

```bash
adb forward tcp:8272 tcp:8272
```

### No live updates

- Verify the SSE connection in browser DevTools (Network tab → filter "EventStream"). You should see `/api/changes` with a persistent connection.
- If the connection drops, the inspector auto-reconnects after 3 seconds.
- On Android, ensure the app is in the foreground — backgrounded apps may suspend the HTTP server.

### "Cannot access a disposed object" crash on Android

This is a known MAUI framework bug (`dotnet/maui#32458`) unrelated to the inspector. Add `EnableOnBackInvokedCallback = false` to your `MainActivity` Activity attribute as a workaround.

### Database locked errors

The inspector opens per-query connections with `Pooling=false` to avoid holding locks. If you see locking errors, check that your app isn't holding a long-running transaction.

## Architecture

```
┌─────────────────────────────────────────┐
│  Your MAUI App (Debug build)            │
│                                         │
│  ┌─────────────────────────────────┐    │
│  │  DbInspectorServer (EmbedIO)    │    │
│  │  ├── /           → inspector UI │    │
│  │  ├── /api/*      → JSON APIs   │    │
│  │  └── /api/changes → SSE stream │    │
│  └──────────┬──────────────────────┘    │
│             │                           │
│  ┌──────────▼──────────────────────┐    │
│  │  SqliteReader                   │    │
│  │  (per-query connections)        │    │
│  └──────────┬──────────────────────┘    │
│             │                           │
│  ┌──────────▼──────────────────────┐    │
│  │  ChangeDetector                 │    │
│  │  (poll + SSE broadcast)         │    │
│  └─────────────────────────────────┘    │
│             │                           │
│  ┌──────────▼──────────────────────┐    │
│  │  your-app.db (SQLite)           │    │
│  └─────────────────────────────────┘    │
└─────────────────────────────────────────┘
         │
         │ http://localhost:8271
         ▼
┌─────────────────┐
│  Browser        │
│  (Inspector UI) │
└─────────────────┘
```

## Key Files

| File | Purpose |
|------|---------|
| `src/tools/SqliteInspector.Maui/DbInspectorServer.cs` | EmbedIO web server, routing, request handling |
| `src/tools/SqliteInspector.Maui/SqliteReader.cs` | Database queries with per-query connections |
| `src/tools/SqliteInspector.Maui/ChangeDetector.cs` | Poll-based change detection + SSE broadcast |
| `src/tools/SqliteInspector.Maui/DbInspectorExtensions.cs` | DI registration (`#if DEBUG` gated) |
| `src/tools/SqliteInspector.Maui/Assets/inspector.html` | Single-page web UI (embedded resource) |
| `src/tools/SqliteInspector.Maui/SqliteInspectorOptions.cs` | Configuration options |
| `tests/tools/SqliteInspector.Maui.Tests/` | 52 unit tests |
