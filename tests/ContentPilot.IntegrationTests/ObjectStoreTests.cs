using System.Net;
using System.Text;
using ContentPilot.Application.Abstractions;
using ContentPilot.IntegrationTests.Infrastructure;
using ContentPilot.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace ContentPilot.IntegrationTests;

/// <summary>
/// Storage against real MinIO. An in-memory fake would not prove the two things that
/// actually matter: that a presigned URL works from outside the process, and that the
/// bucket refuses anonymous reads.
/// </summary>
[Collection(ContentPilotCollection.Name)]
public sealed class ObjectStoreTests(ContentPilotFixture fixture)
{
    private static readonly Guid Tenant = Guid.NewGuid();

    [DockerFact]
    public async Task An_object_round_trips()
    {
        var store = Store();
        var key = ObjectKey.ForTenant(Tenant, $"diagnostics/{Guid.NewGuid():N}.txt");
        const string body = "ContentPilot round trip.";

        await PutAsync(store, key, body);

        (await store.ExistsAsync(key)).ShouldBeTrue();

        await using var stream = await store.GetAsync(key);
        using var reader = new StreamReader(stream);

        (await reader.ReadToEndAsync()).ShouldBe(body);
    }

    [DockerFact]
    public async Task A_missing_object_reports_itself_clearly()
    {
        var store = Store();
        var key = ObjectKey.ForTenant(Tenant, "does/not/exist.png");

        (await store.ExistsAsync(key)).ShouldBeFalse();
        await Should.ThrowAsync<FileNotFoundException>(() => store.GetAsync(key));
    }

    [DockerFact]
    public async Task A_presigned_url_grants_temporary_read_access()
    {
        var store = Store();
        var key = ObjectKey.ForTenant(Tenant, $"diagnostics/{Guid.NewGuid():N}.txt");
        const string body = "signed content";

        await PutAsync(store, key, body);

        var url = await store.GetPresignedReadUrlAsync(key, TimeSpan.FromMinutes(5));

        // Signed against the configured endpoint scheme, not the signer's https default.
        url.Scheme.ShouldBe("http", $"presigned url was: {url}");

        using var http = new HttpClient();
        var response = await http.GetAsync(url);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldBe(body);
    }

    [DockerFact]
    public async Task An_unsigned_request_is_refused()
    {
        var store = Store();
        var key = ObjectKey.ForTenant(Tenant, $"diagnostics/{Guid.NewGuid():N}.txt");
        await PutAsync(store, key, "private");

        var signed = await store.GetPresignedReadUrlAsync(key, TimeSpan.FromMinutes(5));
        var unsigned = new Uri(signed.GetLeftPart(UriPartial.Path));

        using var http = new HttpClient();
        var response = await http.GetAsync(unsigned);

        // Buckets are private without exception; a leaked key on its own is worthless.
        response.IsSuccessStatusCode.ShouldBeFalse();
    }

    [DockerFact]
    public async Task A_presigned_url_lifetime_beyond_the_ceiling_is_refused()
    {
        var store = Store();
        var key = ObjectKey.ForTenant(Tenant, "diagnostics/whatever.txt");

        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => store.GetPresignedReadUrlAsync(key, TimeSpan.FromDays(7)));
    }

    [DockerFact]
    public async Task Listing_is_scoped_by_prefix()
    {
        var store = Store();
        var tenant = Guid.NewGuid();
        var other = Guid.NewGuid();

        await PutAsync(store, ObjectKey.ForTenant(tenant, "assets/a.txt"), "a");
        await PutAsync(store, ObjectKey.ForTenant(tenant, "assets/b.txt"), "b");
        await PutAsync(store, ObjectKey.ForTenant(other, "assets/c.txt"), "c");

        var keys = new List<string>();

        await foreach (var key in store.ListAsync(ObjectKey.TenantPrefix(tenant)))
        {
            keys.Add(key.Value);
        }

        keys.Count.ShouldBe(2);
        keys.ShouldAllBe(k => k.StartsWith(ObjectKey.TenantPrefix(tenant)));
    }

    [DockerFact]
    public async Task Deleting_removes_the_object()
    {
        var store = Store();
        var key = ObjectKey.ForTenant(Tenant, $"diagnostics/{Guid.NewGuid():N}.txt");

        await PutAsync(store, key, "temp");
        await store.DeleteAsync(key);

        (await store.ExistsAsync(key)).ShouldBeFalse();
    }

    private IObjectStore Store()
    {
        var scope = fixture.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IObjectStore>();
    }

    private static async Task PutAsync(IObjectStore store, ObjectKey key, string body)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(body));
        await store.PutAsync(key, stream, "text/plain; charset=utf-8");
    }
}
