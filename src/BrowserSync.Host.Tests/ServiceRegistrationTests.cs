using BrowserSync.Core.Sync;
using BrowserSync.Host;
using BrowserSync.Host.Duplicates;
using BrowserSync.Host.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BrowserSync.Host.Tests;

/// <summary>
/// Builds the real DI container the host uses (<see cref="ServiceRegistration.AddBrowserSyncServices"/>)
/// and resolves every service from it. This is deliberately NOT a test that constructs
/// <see cref="SyncEngine"/> directly — the bug this guards against (a missing `TimeProvider`
/// registration) only showed up when the host tried to resolve `SyncEngine` through the
/// container on a real WebSocket message; every `SyncEngine` unit test that `new`s it up
/// directly passed regardless.
/// </summary>
public class ServiceRegistrationTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder().Build();
        services.AddBrowserSyncServices(configuration, "Data Source=:memory:");
        return services.BuildServiceProvider();
    }

    [Fact]
    public void SyncEngine_ResolvesFromContainer()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var engine = scope.ServiceProvider.GetRequiredService<SyncEngine>();

        Assert.NotNull(engine);
    }

    [Fact]
    public void ReconciliationHostedService_ResolvesAsSingletonAndAsHostedService()
    {
        using var provider = BuildProvider();

        var direct = provider.GetRequiredService<ReconciliationHostedService>();
        var hostedServices = provider.GetServices<IHostedService>().ToList();

        Assert.Contains(hostedServices, s => ReferenceEquals(s, direct));
    }

    [Fact]
    public void TombstonePruningHostedService_ResolvesAsHostedService()
    {
        using var provider = BuildProvider();

        var hostedServices = provider.GetServices<IHostedService>().ToList();

        Assert.Contains(hostedServices, s => s is TombstonePruningHostedService);
    }

    [Fact]
    public void DuplicateReportStore_ResolvesAsSingleton()
    {
        using var provider = BuildProvider();

        var a = provider.GetRequiredService<DuplicateReportStore>();
        var b = provider.GetRequiredService<DuplicateReportStore>();

        Assert.Same(a, b);
    }

    [Fact]
    public void PendingDeletionTracker_ResolvesAsSingleton()
    {
        // Must be a singleton: it debounces "item missing" observations across the fresh
        // SyncEngine instance created for every message, so two separate reconciliation passes
        // need to see the SAME tracker instance for the two-pass confirmation to work at all.
        using var provider = BuildProvider();

        var a = provider.GetRequiredService<PendingDeletionTracker>();
        var b = provider.GetRequiredService<PendingDeletionTracker>();

        Assert.Same(a, b);
    }

    [Fact]
    public void HostSettings_BindsDefaultsWhenConfigurationSectionIsAbsent()
    {
        using var provider = BuildProvider();

        var settings = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<HostSettings>>().Value;

        Assert.Equal(4, settings.ReconciliationIntervalMinutes);
        Assert.Equal(30, settings.TombstoneRetentionDays);
    }
}
