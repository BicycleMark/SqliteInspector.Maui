# SqliteInspector Sample App

A minimal MAUI app that demonstrates SqliteInspector.Maui in action. The app manages a simple "Notes" list backed by SQLite — add and delete notes, then watch the inspector's browser UI update in real time.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- MAUI workload installed:
  ```bash
  dotnet workload install maui
  ```
- Android SDK (for Android target) or Xcode (for iOS target)

## Build & Run

### Android (emulator or USB device)

```bash
dotnet build samples/SqliteInspector.Sample -t:Run -f net10.0-android
```

### iOS (simulator)

```bash
dotnet build samples/SqliteInspector.Sample -t:Run -f net10.0-ios
```

## Using the Inspector

1. **Launch the app** — three seed notes appear automatically
2. **Open your browser** to `http://localhost:8271`
   - You'll see the inspector UI showing the `Notes` table
3. **Tap "+ Add Note"** in the app — enter a title
   - The new row appears in the browser within ~2 seconds
4. **Swipe left on a note** to delete it
   - The row disappears from the browser UI automatically

### Android Emulator: Port Forwarding

The inspector listens on `localhost` inside the emulator. Forward the port to your host machine:

```bash
adb forward tcp:8271 tcp:8271
```

Then open `http://localhost:8271` in your host browser.

### iOS Simulator

No port forwarding needed — the simulator shares the host network. Open `http://localhost:8271` directly.

## How It Works

The sample registers SqliteInspector in `MauiProgram.cs`:

```csharp
var dbPath = Path.Combine(FileSystem.AppDataDirectory, "notes.db");

builder.Services.AddSqliteInspector(opts =>
{
    opts.DatabasePath = dbPath;
});

var app = builder.Build();
app.Services.StartSqliteInspector();
```

- `AddSqliteInspector()` registers the inspector server (DEBUG builds only)
- `StartSqliteInspector()` starts the embedded HTTP server on port 8271
- The inspector polls the database every 2 seconds and pushes changes to the browser via SSE

## Project Structure

```
samples/SqliteInspector.Sample/
├── SqliteInspector.Sample.csproj   # MAUI app with ProjectReference to SqliteInspector.Maui
├── MauiProgram.cs                  # DI setup + inspector registration
├── App.xaml / App.xaml.cs          # Application shell
├── MainPage.xaml / MainPage.xaml.cs # Notes list UI
├── NoteDatabase.cs                 # SQLite data access (Microsoft.Data.Sqlite)
└── Platforms/
    ├── Android/                    # Android entry points + manifest
    └── iOS/                        # iOS entry point + Info.plist
```
