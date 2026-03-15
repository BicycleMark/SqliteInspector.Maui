using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SqliteInspector.Maui.Tests;

public class DbInspectorExtensionsTests
{
    [Fact]
    public void AddSqliteInspector_RegistersOptions()
    {
        var services = new ServiceCollection();

        services.AddSqliteInspector(opts =>
        {
            opts.DatabasePath = "/tmp/test.db";
            opts.Port = 9999;
        });

        var provider = services.BuildServiceProvider();
        var options = provider.GetService<SqliteInspectorOptions>();

        options.Should().NotBeNull();
        options!.DatabasePath.Should().Be("/tmp/test.db");
        options.Port.Should().Be(9999);
    }

    [Fact]
    public void AddSqliteInspector_RegistersServer()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSqliteInspector(opts =>
        {
            opts.DatabasePath = "/tmp/test.db";
        });

        var provider = services.BuildServiceProvider();
        var server = provider.GetService<DbInspectorServer>();

        server.Should().NotBeNull();
    }

    [Fact]
    public void AddSqliteInspector_ReturnsServiceCollection()
    {
        var services = new ServiceCollection();

        var result = services.AddSqliteInspector(opts =>
        {
            opts.DatabasePath = "/tmp/test.db";
        });

        result.Should().BeSameAs(services);
    }

    [Fact]
    public void StartSqliteInspector_NoServer_DoesNotThrow()
    {
        // Empty service provider with no SqliteInspector registered
        var services = new ServiceCollection().BuildServiceProvider();

        var act = () => services.StartSqliteInspector();

        act.Should().NotThrow();
    }
}
