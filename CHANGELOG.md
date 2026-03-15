# Changelog

All notable changes to SqliteInspector.Maui are documented here.

## [Unreleased]

### Added
- Initial release as a standalone NuGet package
- Built-in single-page web UI for browsing SQLite databases
- Table listing with row counts
- Paginated row browsing with offset/limit
- Column schema inspection (type, nullable, primary key, default value)
- Read-only ad-hoc SQL query execution
- Real-time change detection via FileSystemWatcher + polling fallback
- SSE (Server-Sent Events) for live browser updates
- Automatic port negotiation when default port is busy
- `#if DEBUG` guards — zero footprint in Release builds
- DI extension methods: `AddSqliteInspector()` and `StartSqliteInspector()`
