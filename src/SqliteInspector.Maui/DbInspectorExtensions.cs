using Microsoft.Extensions.DependencyInjection;

namespace SqliteInspector.Maui;

public static class DbInspectorExtensions
{
    public static IServiceCollection AddSqliteInspector(
        this IServiceCollection services,
        Action<SqliteInspectorOptions> configureOptions)
    {
#if DEBUG
        var options = new SqliteInspectorOptions();
        configureOptions(options);

        services.AddSingleton(options);
        services.AddSingleton<DbInspectorServer>();
#endif
        return services;
    }

    /// <summary>
    /// Starts the SqliteInspector server. Call after building the app.
    /// MAUI does not auto-start IHostedService, so this must be called explicitly.
    /// </summary>
    public static void StartSqliteInspector(this IServiceProvider services)
    {
#if DEBUG
        var server = services.GetService<DbInspectorServer>();
        if (server is not null)
        {
            _ = server.StartAsync();
        }
#endif
    }
}
