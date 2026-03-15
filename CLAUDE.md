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
└── .github/
    ├── ISSUE_TEMPLATE/
    │   ├── bug_report.yml             # Bug report form
    │   ├── feature_request.yml        # Feature request form
    │   └── config.yml                 # Disable blank issues
    ├── pull_request_template.md       # PR template
    └── workflows/
        ├── ci.yml                     # Build + test on push/PR
        └── publish.yml                # Pack + push to nuget.org on tag
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

### Branching

- Branch naming: `feature/#N-short-description` or `fix/#N-short-description` (e.g., `feature/#1-sample-app`)
- Always branch from `main`
- PRs must reference the issue with `Closes #N` in the body

### Issues

- This repo is the **single source of truth** — all issues live on GitHub
- If a fieldplatform GitLab issue applies here, migrate it (create on GitHub, close on GitLab with a link)
- No issue mirroring — once migrated, the GitLab issue is closed

### Templates

GitHub auto-fills templates when creating issues and PRs via the web UI.

| Template | Location | Triggers on |
|----------|----------|-------------|
| Bug Report | `.github/ISSUE_TEMPLATE/bug_report.yml` | New Issue → "Bug Report" |
| Feature Request | `.github/ISSUE_TEMPLATE/feature_request.yml` | New Issue → "Feature Request" |
| PR Template | `.github/pull_request_template.md` | Every new PR |

**Blank issues are disabled** — all issues must use a template.

#### Creating issues via CLI

Bug report:
```bash
gh issue create --label bug --title "Inspector page blank on Android 15" --body "$(cat <<'EOF'
### Package Version
0.1.0-preview.3

### Platform
Android

### .NET Version
10.0.101

### Description
**What happened:** The inspector page shows a blank white screen after...
**Expected:** The browser should display the table list

### Steps to Reproduce
1. Register AddSqliteInspector() in MauiProgram.cs
2. Build and run on Android 15 emulator (API 35)
3. adb forward tcp:8271 tcp:8271
4. Open http://localhost:8271

### Logs / Stack Trace
```
No errors in logcat
```
EOF
)"
```

Feature request:
```bash
gh issue create --label enhancement --title "Export query results as CSV" --body "$(cat <<'EOF'
### Summary
Add a "Download CSV" button to query results in the inspector UI.

### Use Case
When debugging data issues, sharing query results with teammates who
don't have the inspector running locally.

### Proposed API / Approach
Add a GET /api/export?table=Notes&format=csv endpoint.

### Scope
Medium — new endpoint or UI feature
EOF
)"
```

#### Creating PRs via CLI

The PR template auto-fills. Populate all sections:
```bash
gh pr create --title "Fix blank page on Android 15" --body "$(cat <<'EOF'
## Summary
Fix inspector HTML not loading on Android 15 due to WebView security policy.

## Closes
Closes #5

## Changes
| File | Change |
|------|--------|
| `DbInspectorServer.cs` | Add Content-Security-Policy header |

## Test Plan
- [x] `dotnet build SqliteInspector.slnx` — builds with 0 errors
- [x] `dotnet test SqliteInspector.slnx` — all tests pass
- [ ] Deploy to Android 15 emulator, verify inspector loads
EOF
)"
```

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
