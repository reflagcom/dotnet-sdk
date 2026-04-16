using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Reflag;

namespace Reflag.MinimalApiExample;

public static class ReflagMinimalApiExampleApp
{
    public static WebApplication BuildApp(string[] args, Action<WebApplicationBuilder>? configureBuilder = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configureBuilder?.Invoke(builder);

        // These are automatically loaded from the environment, so only included here for
        // reference purposes
        var secretKey = builder.Configuration["REFLAG_SECRET_KEY"];
        var apiBaseUrl = builder.Configuration["REFLAG_API_BASE_URL"];

        // Example-app-only fallback provider configuration
        var fileFallbackDirectory = builder.Configuration["REFLAG_FALLBACK_FILE_DIRECTORY"];
        var azureBlobContainer = builder.Configuration["REFLAG_FALLBACK_AZURE_BLOB_CONTAINER"];
        var azureBlobPrefix = builder.Configuration["REFLAG_FALLBACK_AZURE_BLOB_PREFIX"];

        IFlagsFallbackProvider? flagsFallbackProvider = null;
        var fallbackProvider = "none";

        if (!string.IsNullOrWhiteSpace(fileFallbackDirectory))
        {
            flagsFallbackProvider = ReflagFallbackProviders.File(new FileFallbackProviderOptions
            {
                Directory = fileFallbackDirectory,
            });
            fallbackProvider = "file";
        }
        else if (!string.IsNullOrWhiteSpace(azureBlobContainer))
        {
            flagsFallbackProvider = ReflagFallbackProviders.AzureBlob(new AzureBlobFallbackProviderOptions
            {
                ConnectionString = builder.Configuration["AZURE_STORAGE_CONNECTION_STRING"],
                ContainerName = azureBlobContainer,
                BlobNamePrefix = azureBlobPrefix,
            });
            fallbackProvider = "azure-blob";
        }

        builder.Services.AddReflag(sp =>
        {
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Reflag");

            return string.IsNullOrWhiteSpace(secretKey)
                ? new ReflagClientOptions
                {
                    Offline = true,
                    Logger = logger,
                    FlagsFallbackProvider = flagsFallbackProvider,
                    FlagOverrides = new Dictionary<string, bool>
                    {
                        ["demo-flag"] = true,
                        ["new-dashboard"] = false,
                    },
                }
                : new ReflagClientOptions
                {
                    SecretKey = secretKey,
                    ApiBaseUrl = string.IsNullOrWhiteSpace(apiBaseUrl) ? null : new Uri(apiBaseUrl),
                    Logger = logger,
                    FlagsFallbackProvider = flagsFallbackProvider,
                };
        });

        var app = builder.Build();
        var offlineDemoMode = string.IsNullOrWhiteSpace(secretKey);

        var tryUrls = new[]
        {
            "/flags/demo-flag",
            "/flags/new-dashboard?userId=user-123&companyId=company-456",
            "/flags/new-dashboard?context.user.id=user-123&context.company.id=company-456&context.company.plan=enterprise",
            "/bootstrap?context.user.id=user-123&context.user.name=bleh&context.company.id=company-456",
            "/bootstrap?context.user.id=user-123&context.user.attributes.plan=enterprise&context.other.environment=staging",
        };

        app.MapGet("/", () => Results.Ok(new
        {
            message = "Reflag .NET SDK minimal API example",
            mode = offlineDemoMode ? "offline-demo" : "live",
            fallbackProvider,
            tryUrls,
        }));

        app.MapGet("/flags/{key}", (HttpRequest request, string key, ReflagClient client) =>
        {
            var bound = client.BindClient(ExampleContextQueryParser.Build(
                request,
                defaultUserId: "demo-user",
                defaultUserEmail: "demo@example.com",
                defaultEnvironment: app.Environment.EnvironmentName));

            var enabled = bound.GetFlag(key);
            return Results.Ok(new
            {
                key,
                enabled,
                context = bound.Context,
            });
        });

        app.MapGet("/bootstrap", (HttpRequest request, ReflagClient client) =>
        {
            var bound = client.BindClient(ExampleContextQueryParser.Build(request, defaultUserId: "demo-user"));
            var bootstrapped = bound.GetFlagsForBootstrap();
            return Results.Ok(bootstrapped);
        });


        return app;
    }

}
