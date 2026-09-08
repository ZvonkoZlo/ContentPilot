using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using ContentPilot.Application.Abstractions;
using ContentPilot.Application.Jobs;
using ContentPilot.Infrastructure;
using ContentPilot.Infrastructure.Jobs;
using ContentPilot.Infrastructure.Persistence;
using ContentPilot.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ContentPilot.TestSupport;
using Testcontainers.Minio;
using Testcontainers.PostgreSql;

namespace ContentPilot.IntegrationTests.Infrastructure;

/// <summary>
/// One Postgres and one MinIO container shared by the whole suite. The schema is created
/// by the real migrations, so the tests also prove the migrations apply cleanly.
/// </summary>
public sealed class ContentPilotFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;
    private MinioContainer? _minio;
    private ServiceProvider? _services;

    public bool Started { get; private set; }

    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>Exposed so an API-level test can boot the real host against these containers.</summary>
    public IReadOnlyDictionary<string, string?> Settings { get; private set; } =
        new Dictionary<string, string?>();

    public const string Bucket = "contentpilot-tests";

    public async Task InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        // Image pinned deliberately: the same major version the compose stack runs, so
        // partial indexes and SKIP LOCKED behave identically here and in production.
        _postgres = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("contentpilot")
            .WithUsername("contentpilot")
            .WithPassword("contentpilot")
            .Build();

        _minio = new MinioBuilder("minio/minio:latest").Build();

        await Task.WhenAll(_postgres.StartAsync(), _minio.StartAsync());

        ConnectionString = _postgres.GetConnectionString();

        var settings = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = ConnectionString,
                ["ObjectStorage:Bucket"] = Bucket,
                ["ObjectStorage:ServiceUrl"] = MinioUrl(_minio),
                ["ObjectStorage:AccessKey"] = _minio.GetAccessKey(),
                ["ObjectStorage:SecretKey"] = _minio.GetSecretKey(),
                ["ObjectStorage:ForcePathStyle"] = "true",
                ["Jobs:LeaseDuration"] = "00:00:30",
                ["Jobs:MaxAttempts"] = "3",
            };

        Settings = settings;

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning));
        // The real composition root, not a test-only one: the point is to exercise
        // the wiring the API and Worker actually use.
        services.AddContentPilotInfrastructure(configuration);
        services.AddScoped<IJobHandler, PingJobHandler>();

        _services = services.BuildServiceProvider();

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.MigrateAsync();
        }

        await CreateBucketAsync(_minio);

        Started = true;
    }

    /// <summary>
    /// The container reports host:port with no scheme, and the AWS SDK then assumes
    /// https and fails on TLS. Same trap the production options validation now catches.
    /// </summary>
    private static string MinioUrl(MinioContainer minio)
    {
        var raw = minio.GetConnectionString();

        return raw.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? raw : $"http://{raw}";
    }

    private static async Task CreateBucketAsync(MinioContainer minio)
    {
        var config = new AmazonS3Config
        {
            ServiceURL = MinioUrl(minio),
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
        };

        using var client = new AmazonS3Client(
            new BasicAWSCredentials(minio.GetAccessKey(), minio.GetSecretKey()), config);

        await client.PutBucketAsync(new PutBucketRequest { BucketName = Bucket });
    }

    /// <summary>Opens a fresh DI scope, mirroring what a request or a job gets.</summary>
    public AsyncServiceScope CreateScope() =>
        (_services ?? throw new InvalidOperationException("Fixture was not started.")).CreateAsyncScope();

    public async Task DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }

        if (_minio is not null)
        {
            await _minio.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class ContentPilotCollection : ICollectionFixture<ContentPilotFixture>
{
    public const string Name = "contentpilot";
}
