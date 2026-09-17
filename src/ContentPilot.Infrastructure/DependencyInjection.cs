using Amazon.Runtime;
using Amazon.S3;
using ContentPilot.Application.Abstractions;
using ContentPilot.Application.Capabilities;
using ContentPilot.Infrastructure.Ai;
using ContentPilot.Infrastructure.Assets;
using ContentPilot.Infrastructure.Branding;
using ContentPilot.Infrastructure.Campaigns;
using ContentPilot.Application.Jobs;
using ContentPilot.Infrastructure.Jobs;
using ContentPilot.Infrastructure.Persistence;
using ContentPilot.Infrastructure.Rendering;
using ContentPilot.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ContentPilot.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Wires persistence, storage, the queue and the shared primitives. Hosts differ only
    /// in whether they consume jobs, so the composition stays identical across processes.
    /// </summary>
    public static IServiceCollection AddContentPilotInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<JobQueueOptions>()
            .Bind(configuration.GetSection(JobQueueOptions.SectionName))
            .ValidateOnStart();

        services.AddOptions<ObjectStorageOptions>()
            .Bind(configuration.GetSection(ObjectStorageOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.Bucket), "ObjectStorage:Bucket must be configured.")
            .Validate(o => o.HasValidServiceUrl, "ObjectStorage:ServiceUrl must be an absolute http/https URL.")
            .ValidateOnStart();

        services.TryAddSingleton<IClock, SystemClock>();

        // One instance per scope, shared by both interfaces: the edge sets the tenant,
        // everything downstream in that scope reads it.
        services.TryAddScoped<TenantContext>();
        services.TryAddScoped<IMutableTenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.TryAddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());

        services.AddDbContext<AppDbContext>((sp, builder) =>
        {
            var connectionString = configuration.GetConnectionString("Postgres")
                ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");

            // No EnableRetryOnFailure on purpose. The retrying execution strategy
            // forbids user-initiated transactions, and this system is built on them:
            // the job lease, and every "job outcome + state change" commit, are explicit
            // transactions. Transient database failures are handled where they belong —
            // the job fails its attempt, backs off, and the reaper reclaims the lease.
            builder
                .UseNpgsql(connectionString)
                .UseSnakeCaseNamingConvention();

            if (configuration.GetValue("Persistence:SensitiveDataLogging", false))
            {
                builder.EnableSensitiveDataLogging();
            }
        });

        services.TryAddScoped<IUnitOfWork>(sp => sp.GetRequiredService<AppDbContext>());
        services.TryAddScoped<IJobQueue, PostgresJobQueue>();

        services.AddSingleton<IAmazonS3>(sp => BuildS3Client(sp, presigning: false));

        // Signed URLs must be signed against the endpoint the viewer can reach, because
        // the host is part of the signature and cannot be swapped afterwards.
        services.AddSingleton(sp => new S3Presigner(BuildS3Client(sp, presigning: true)));

        services.TryAddScoped<IObjectStore, S3ObjectStore>();

        // Brand Brain. The reader is the read-only capability surface agents will consume;
        // the library is the write path and stays off that surface.
        services.TryAddSingleton<IImageIngestor, ImageIngestor>();
        services.TryAddScoped<IBrandBrainReader, BrandBrainReader>();
        services.TryAddScoped<AssetLibrary>();
        services.TryAddScoped<GoldenTenantSeeder>();
        services.TryAddScoped<IAssetContentResolver, AssetContentResolver>();
        services.TryAddScoped<CampaignStarter>();
        services.TryAddScoped<Packaging.CampaignPackager>();
        services.TryAddScoped<Packaging.CampaignZipBuilder>();
        services.TryAddScoped<Content.ContentHistoryRecorder>();

        services.AddContentPilotAi(configuration);
        services.AddContentPilotRendererClient(configuration);
        Email.EmailServiceCollectionExtensions.AddContentPilotEmail(services, configuration);

        return services;
    }

    private static IAmazonS3 BuildS3Client(IServiceProvider sp, bool presigning)
    {
        var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ObjectStorageOptions>>().Value;
        var endpoint = presigning ? options.EffectivePresignUrl : options.ServiceUrl;

        var config = new AmazonS3Config
        {
            ForcePathStyle = options.ForcePathStyle,
            AuthenticationRegion = options.Region,
        };

        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            config.ServiceURL = endpoint;
            config.UseHttp = new Uri(endpoint).Scheme == Uri.UriSchemeHttp;
        }
        else
        {
            config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(options.Region);
        }

        return string.IsNullOrWhiteSpace(options.AccessKey)
            ? new AmazonS3Client(config)
            : new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), config);
    }

    /// <summary>
    /// Registers everything that consumes work. The API omits this: it enqueues, the
    /// Worker executes, and neither can drift into doing the other's job by accident.
    /// </summary>
    public static IServiceCollection AddContentPilotJobProcessing(this IServiceCollection services)
    {
        services.AddScoped<IJobHandler, PingJobHandler>();
        services.AddScoped<IJobHandler, ContentItemWorkflowJobHandler>();
        services.AddScoped<IJobHandler, CampaignWorkflowJobHandler>();
        services.AddScoped<IJobHandler, CampaignTriggerScanJobHandler>();
        services.AddScoped<IJobHandler, CampaignTriggerReconcileJobHandler>();
        services.AddScoped<IJobHandler, RetentionJobHandler>();

        services.AddHostedService<JobDispatcher>();
        services.AddHostedService<JobReaper>();

        return services;
    }
}
