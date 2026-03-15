using SqliteInspector.Maui;

namespace SqliteInspector.Sample;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        var dbPath = Path.Combine(FileSystem.AppDataDirectory, "notes.db");

        builder.Services.AddSingleton(new NoteDatabase(dbPath));
        builder.Services.AddTransient<MainPage>();

        builder.Services.AddSqliteInspector(opts =>
        {
            opts.DatabasePath = dbPath;
        });

        var app = builder.Build();

        app.Services.StartSqliteInspector();

        return app;
    }
}
