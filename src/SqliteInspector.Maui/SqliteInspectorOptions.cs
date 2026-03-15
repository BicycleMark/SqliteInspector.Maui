namespace SqliteInspector.Maui;

public class SqliteInspectorOptions
{
    public int Port { get; set; } = 8271;
    public int MaxPortRetries { get; set; } = 5;
    public string DatabasePath { get; set; } = string.Empty;
    public int AutoRefreshSeconds { get; set; } = 2;
}
