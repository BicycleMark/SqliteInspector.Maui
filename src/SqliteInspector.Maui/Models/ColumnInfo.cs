namespace SqliteInspector.Maui.Models;

public record ColumnInfo(int Cid, string Name, string Type, bool NotNull, string? DefaultValue, bool IsPrimaryKey);
