using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Reflag.Internal;

namespace Reflag;

public static class ReflagServiceCollectionExtensions
{
    public static IServiceCollection AddReflag(
        this IServiceCollection services,
        ReflagClientOptions options)
    {
        ThrowHelpers.ThrowIfNull(services, nameof(services));
        ThrowHelpers.ThrowIfNull(options, nameof(options));

        return services.AddReflag(_ => options);
    }

    public static IServiceCollection AddReflag(
        this IServiceCollection services,
        Func<IServiceProvider, ReflagClientOptions> configure)
    {
        ThrowHelpers.ThrowIfNull(services, nameof(services));
        ThrowHelpers.ThrowIfNull(configure, nameof(configure));

        services.AddSingleton(sp => new ReflagClient(configure(sp)));
        services.AddSingleton<IHostedService, ReflagInitializationHostedService>();
        return services;
    }

    private sealed class ReflagInitializationHostedService(ReflagClient client) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            return client.InitializeAsync(cancellationToken);
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            using var shutdownFlushCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            shutdownFlushCancellationTokenSource.CancelAfter(ReflagConstants.EndFlushTimeout);
            await client.FlushOnShutdownAsync(shutdownFlushCancellationTokenSource.Token).ConfigureAwait(false);
        }
    }
}
