namespace SqliteInspector.Maui.Models;

public record TableSchema(string TableName, IReadOnlyList<ColumnInfo> Columns);
