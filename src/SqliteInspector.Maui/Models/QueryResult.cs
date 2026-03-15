namespace SqliteInspector.Maui.Models;

public record QueryResult(IReadOnlyList<string> ColumnNames, IReadOnlyList<Dictionary<string, object?>> Rows, long TotalRows);
