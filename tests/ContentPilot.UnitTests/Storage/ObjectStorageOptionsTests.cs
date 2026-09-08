using ContentPilot.Infrastructure.Storage;
using Shouldly;

namespace ContentPilot.UnitTests.Storage;

/// <summary>
/// Endpoint configuration has two traps that only surface at download time, far from the
/// cause: a missing scheme (the SDK silently assumes https) and signing against an
/// internal hostname a browser cannot resolve. Both are settled here instead.
/// </summary>
public sealed class ObjectStorageOptionsTests
{
    [Fact]
    public void Signing_falls_back_to_the_server_endpoint_when_no_public_one_is_set()
    {
        var options = new ObjectStorageOptions { ServiceUrl = "http://minio:9000" };

        options.EffectivePresignUrl.ShouldBe("http://minio:9000");
    }

    [Fact]
    public void Signing_uses_the_public_endpoint_when_it_differs_from_the_server_one()
    {
        var options = new ObjectStorageOptions
        {
            ServiceUrl = "http://minio:9000",
            PublicServiceUrl = "https://assets.example.com",
        };

        // The host is part of the signature, so the URL must be signed against the
        // endpoint the viewer reaches — it cannot be rewritten afterwards.
        options.EffectivePresignUrl.ShouldBe("https://assets.example.com");
        options.UsesPlainHttp.ShouldBeFalse();
    }

    [Fact]
    public void An_empty_public_endpoint_is_treated_as_unset()
    {
        var options = new ObjectStorageOptions { ServiceUrl = "http://minio:9000", PublicServiceUrl = "   " };

        options.EffectivePresignUrl.ShouldBe("http://minio:9000");
    }

    [Fact]
    public void Real_aws_needs_no_endpoint_at_all()
    {
        var options = new ObjectStorageOptions { ServiceUrl = null };

        options.HasValidServiceUrl.ShouldBeTrue();
        options.EffectivePresignUrl.ShouldBeNull();
        options.UsesPlainHttp.ShouldBeFalse();
    }

    [Theory]
    [InlineData("minio:9000")]
    [InlineData("localhost:9000")]
    [InlineData("ftp://minio:9000")]
    [InlineData("not a url")]
    public void An_endpoint_without_a_usable_scheme_fails_validation_at_startup(string endpoint)
    {
        new ObjectStorageOptions { ServiceUrl = endpoint }.HasValidServiceUrl.ShouldBeFalse();
        new ObjectStorageOptions { PublicServiceUrl = endpoint }.HasValidServiceUrl.ShouldBeFalse();
    }

    [Theory]
    [InlineData("http://minio:9000", true)]
    [InlineData("https://assets.example.com", false)]
    public void Plain_http_is_detected_from_the_signing_endpoint(string endpoint, bool expected) =>
        new ObjectStorageOptions { ServiceUrl = endpoint }.UsesPlainHttp.ShouldBe(expected);

    [Fact]
    public void The_presigned_url_ceiling_defaults_to_fifteen_minutes()
    {
        var options = new ObjectStorageOptions();

        options.MaxPresignedUrlLifetime.ShouldBe(TimeSpan.FromMinutes(15));
        options.CreateBucketIfMissing.ShouldBeFalse("Creating buckets implicitly is a development-only convenience.");
    }
}
