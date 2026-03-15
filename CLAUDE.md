# CLAUDE.md

## Project Overview

SqliteInspector.Maui is a **standalone** lightweight SQLite database inspector for .NET MAUI apps. It runs an embedded HTTP server (EmbedIO) inside the app during Debug builds, serving a web UI for browsing tables, viewing schemas, running queries, and receiving live updates via SSE.

**This package has ZERO external project dependencies.** It is a standalone reusable NuGet package. Never add references to any other project.

## Repo Structure

```
SqliteInspector.Maui/
├── src/SqliteInspector.Maui/          # NuGet package source
├── tests/SqliteInspector.Maui.Tests/  # Unit tests (xUnit)
├── tools/dotnet-sqlite-inspect/       # Companion CLI tool (placeholder)
├── samples/SqliteInspector.Sample/    # Sample app (future)
├── docs/operating-guide.md            # Full operating guide
├── SqliteInspector.slnx               # Solution file
├── Directory.Build.props              # Shared package versions + MinVer
├── CHANGELOG.md                       # Release history
└── .github/workflows/
    ├── ci.yml                         # Build + test on push/PR
    └── publish.yml                    # Pack + push to nuget.org on tag
```

## Common Commands

```bash
# Build
dotnet build SqliteInspector.slnx

# Test
dotnet test SqliteInspector.slnx

# Pack
dotnet pack src/SqliteInspector.Maui/SqliteInspector.Maui.csproj -c Release -o ./nupkg
```

## Git Conventions

- Do NOT include `Co-Authored-By` lines in commits
- Commit messages: imperative mood, concise

## Versioning & Publishing

- **MinVer** with plain `v` prefix (e.g., `v0.1.0`, `v1.0.0`)
- Tagging a `v*` tag triggers GitHub Actions to pack and push to nuget.org
- `NUGET_API_KEY` must be set as a GitHub repo secret

```bash
# Release example
git tag v0.1.0
git push origin v0.1.0
```

## Package Versions

All NuGet package versions are defined as MSBuild properties in the root `Directory.Build.props`. Never hardcode versions in csproj files. When adding a new dependency, add a version property to `Directory.Build.props` first.

## Technical Decisions

| Item | Choice |
|------|--------|
| .NET Version | .NET 10 |
| HTTP Server | EmbedIO 3.x |
| SQLite Access | Microsoft.Data.Sqlite |
| Test Framework | xUnit |
| Mocking | NSubstitute |
| Assertions | FluentAssertions (v7.x) |
| Solution Format | .slnx |

## Architecture Notes

- All DI registration is wrapped in `#if DEBUG` — Release builds get no-ops
- Each request opens a fresh SQLite connection (`Pooling=false`) to avoid stale WAL snapshots
- Only `SELECT` queries allowed — mutation keywords are rejected
- Port negotiation: tries configured port, then increments up to `MaxPortRetries`
- `ChangeDetector` polls row counts/checksums and broadcasts via SSE
- The web UI is a single embedded HTML file (`Assets/inspector.html`)

## Key Files

| File | Purpose |
|------|---------|
| `DbInspectorServer.cs` | EmbedIO web server, routing, request handling |
| `SqliteReader.cs` | Database queries with per-query connections |
| `ChangeDetector.cs` | Poll-based change detection + SSE broadcast |
| `DbInspectorExtensions.cs` | DI registration (`#if DEBUG` gated) |
| `Assets/inspector.html` | Single-page web UI (embedded resource) |
| `SqliteInspectorOptions.cs` | Configuration options |
