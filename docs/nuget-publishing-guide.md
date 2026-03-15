# NuGet Publishing Guide for SqliteInspector.Maui

Complete reference for publishing this package to nuget.org — including every pitfall encountered and how to avoid it.

## Prerequisites

| Requirement | Details |
|-------------|---------|
| **NUGET_API_KEY** | GitHub repo secret → Settings → Secrets → Actions. Get the key from [nuget.org API keys](https://www.nuget.org/account/apikeys). Scope it to push for the `SqliteInspector.Maui` package. |
| **GitHub token permissions** | The publish workflow needs `contents: write` to create GitHub Releases. This is set in `publish.yml`. |
| **MinVer** | Versions are derived automatically from git tags. No version numbers in csproj files. |

## How the Pipeline Works

```
git tag v0.2.0 && git push origin v0.2.0
         │
         ▼
  publish.yml triggers (on push tags: 'v*')
         │
         ├── checkout (fetch-depth: 0 — full history for MinVer)
         ├── setup .NET 10
         ├── restore (library + tests only, NOT the full solution)
         ├── build Debug (tests exercise #if DEBUG code)
         ├── test
         ├── pack Release → produces .nupkg + .snupkg
         ├── push to nuget.org (with --skip-duplicate)
         └── create GitHub Release (with .nupkg + .snupkg attached)
```

## Directory.Build.props — Required Properties

These properties in `Directory.Build.props` are **all required** for a healthy NuGet package:

```xml
<!-- SourceLink + deterministic builds + symbols -->
<PropertyGroup>
  <PublishRepositoryUrl>true</PublishRepositoryUrl>
  <EmbedUntrackedSources>true</EmbedUntrackedSources>
  <DebugType>portable</DebugType>
  <IncludeSymbols>true</IncludeSymbols>
  <SymbolPackageFormat>snupkg</SymbolPackageFormat>
  <ContinuousIntegrationBuild Condition="'$(CI)' == 'true'">true</ContinuousIntegrationBuild>
</PropertyGroup>
```

| Property | Why it matters |
|----------|---------------|
| `PublishRepositoryUrl` | Embeds the repo URL in the NuGet package metadata |
| `EmbedUntrackedSources` | Includes generated files in SourceLink so debuggers can find them |
| `DebugType=portable` | Generates portable PDB files (required for `.snupkg` generation) |
| `IncludeSymbols=true` | Tells `dotnet pack` to produce a symbols package |
| `SymbolPackageFormat=snupkg` | Produces `.snupkg` (NuGet symbol server format) instead of legacy `.symbols.nupkg` |
| `ContinuousIntegrationBuild` | Normalizes file paths in CI so builds are deterministic/reproducible |

**Why not `DebugType=embedded`?** Embedded PDBs bake symbols into the DLL itself. This works for consumers, but NuGet's symbol server cannot index them — so the package health check reports "missing symbols". Use `portable` + `.snupkg` instead.

### SourceLink

```xml
<ItemGroup>
  <PackageReference Include="Microsoft.SourceLink.GitHub" Version="8.0.0">
    <PrivateAssets>all</PrivateAssets>
    <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
  </PackageReference>
</ItemGroup>
```

SourceLink embeds git commit info and source file URLs into the PDB. This lets consumers step into your source code in Visual Studio/Rider without cloning the repo.

### MinVer

```xml
<PropertyGroup>
  <MinVerDefaultPreReleaseIdentifiers>preview.0</MinVerDefaultPreReleaseIdentifiers>
  <MinVerTagPrefix>v</MinVerTagPrefix>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="MinVer" Version="6.0.0">
    <PrivateAssets>all</PrivateAssets>
    <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
  </PackageReference>
</ItemGroup>
```

MinVer reads the git tag at build time and sets the package version automatically. Tag `v0.2.0` → version `0.2.0`. No tags → defaults to `0.0.0-preview.0`.

## publish.yml — Key Details

### Only build library + tests, not the full solution

The sample app targets MAUI platforms (Android/iOS) which require workloads not available on `ubuntu-latest`. The publish workflow must scope its build:

```yaml
- name: Restore
  run: |
    dotnet restore src/SqliteInspector.Maui
    dotnet restore tests/SqliteInspector.Maui.Tests

- name: Build (Debug)
  run: |
    dotnet build src/SqliteInspector.Maui --no-restore
    dotnet build tests/SqliteInspector.Maui.Tests --no-restore
```

**Do NOT** use `dotnet build SqliteInspector.slnx` in the publish workflow.

### Push with --skip-duplicate

```yaml
- name: Push to NuGet
  run: dotnet nuget push "./nupkg/*.nupkg" --source https://api.nuget.org/v3/index.json --api-key ${{ secrets.NUGET_API_KEY }} --skip-duplicate
```

`--skip-duplicate` prevents the workflow from failing if you re-tag a version that was already pushed. The `.snupkg` is auto-pushed alongside the `.nupkg` by `dotnet nuget push` — no separate push command needed.

### GitHub Release must include both files

```yaml
- name: Create GitHub Release
  uses: softprops/action-gh-release@v2    # v2 only — v3 does not exist
  with:
    files: |
      ./nupkg/*.nupkg
      ./nupkg/*.snupkg
    generate_release_notes: true
```

### Permissions block

```yaml
permissions:
  contents: write
```

Without this, the `GITHUB_TOKEN` defaults to read-only and the GitHub Release step fails with 403.

### Full checkout for MinVer

```yaml
- uses: actions/checkout@v5
  with:
    fetch-depth: 0    # full history — MinVer needs tags
```

A shallow checkout (`fetch-depth: 1`) means MinVer can't see tags and falls back to `0.0.0-preview.0`.

## NuGet Package Health Checklist

After publishing, check your package at `https://www.nuget.org/packages/SqliteInspector.Maui`. The health panel should show:

| Check | Expected | What fixes it |
|-------|----------|---------------|
| **SourceLink** | Green | `Microsoft.SourceLink.GitHub` + `PublishRepositoryUrl` |
| **Symbols** | Green | `DebugType=portable` + `IncludeSymbols` + `SymbolPackageFormat=snupkg` |
| **Deterministic** | Green | `ContinuousIntegrationBuild=true` in CI |
| **Compiler Flags** | Green | SourceLink + deterministic build properties combined |

If any check is red, the fix is in `Directory.Build.props` — not the workflow.

## Release Workflow

### Stable release

```bash
# 1. Ensure main is up to date and CI passes
# 2. Tag from main
git checkout main
git pull
git tag v0.2.0
git push origin v0.2.0
```

### Prerelease

```bash
git tag v0.2.0-preview.1
git push origin v0.2.0-preview.1
```

Prerelease packages are hidden on nuget.org by default. Consumers see them only with "Include prerelease" checked.

### Version scheme

| Tag | NuGet version | Type |
|-----|---------------|------|
| `v0.2.0` | 0.2.0 | Stable |
| `v0.2.0-preview.1` | 0.2.0-preview.1 | Prerelease |
| `v1.0.0` | 1.0.0 | Major stable |

## Verifying Locally Before Tagging

```bash
# Build + test
dotnet build SqliteInspector.slnx
dotnet test SqliteInspector.slnx

# Pack and confirm both files are produced
dotnet pack src/SqliteInspector.Maui/SqliteInspector.Maui.csproj -c Release -o ./nupkg
ls ./nupkg/
# Should show: SqliteInspector.Maui.X.Y.Z.nupkg
#              SqliteInspector.Maui.X.Y.Z.snupkg
```

## Troubleshooting

| Problem | Cause | Fix |
|---------|-------|-----|
| Workflow doesn't trigger | Tag doesn't match `v*` pattern | Ensure tag starts with `v` (e.g., `v0.2.0`) |
| Build fails in CI | Full solution includes MAUI sample | Build only `src/` and `tests/` projects |
| `dotnet nuget push` 403 | `NUGET_API_KEY` secret missing or expired | Regenerate key at nuget.org, update repo secret |
| GitHub Release 403 | Missing `permissions: contents: write` | Add to workflow YAML |
| Package version is `0.0.0-preview.0` | Shallow checkout or no tags | Use `fetch-depth: 0` in checkout |
| Push fails on re-tag | Version already exists on nuget.org | Use `--skip-duplicate` flag |
| Symbols health check red | Using `DebugType=embedded` | Switch to `portable` + `IncludeSymbols` + `SymbolPackageFormat=snupkg` |
| Deterministic check red | Missing `ContinuousIntegrationBuild` | Add `<ContinuousIntegrationBuild Condition="'$(CI)' == 'true'">true</ContinuousIntegrationBuild>` |
| `action-gh-release@v3` fails | v3 does not exist | Use `@v2` |
| Prerelease not visible on nuget.org | Expected behavior | Check "Include prerelease" or click Versions tab |

## Lessons Learned (Chronological)

1. **MAUI workloads aren't on ubuntu-latest** — never build the full solution in CI workflows that run on standard runners.
2. **GitHub Actions default token is read-only** — explicitly set `permissions: contents: write` for release creation.
3. **Always verify action versions exist** — `softprops/action-gh-release@v3` was assumed to exist but didn't.
4. **Use `--skip-duplicate`** — re-tagging after a workflow fix is common; without this flag the push fails.
5. **NuGet health requires all four properties** — SourceLink, deterministic, portable PDBs, and `.snupkg` are all separate checks.
6. **`embedded` PDBs ≠ symbol server support** — embedded PDBs work for local debugging but NuGet's symbol server needs a `.snupkg` built from portable PDBs.
7. **MinVer needs full git history** — `fetch-depth: 0` is not optional.
8. **Test in Debug, pack in Release** — `#if DEBUG` code needs Debug build to exercise, but the NuGet package should be Release-optimized.
