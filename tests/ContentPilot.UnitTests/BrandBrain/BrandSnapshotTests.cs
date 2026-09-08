using ContentPilot.Application.Brand;
using ContentPilot.Domain.Branding;
using Shouldly;

namespace ContentPilot.UnitTests.BrandBrain;

/// <summary>
/// The snapshot is what makes a campaign explainable months later, and that rests entirely
/// on it being deterministic. If the hash moves for reasons unrelated to the brand, the
/// version history stops meaning anything.
/// </summary>
public sealed class BrandSnapshotTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_same_brain_hashes_identically()
    {
        var first = BrandBrainAssembler.Assemble(Fixture.Input(), Now);
        var second = BrandBrainAssembler.Assemble(Fixture.Input(), Now);

        second.ComputeHash().ShouldBe(first.ComputeHash());
    }

    [Fact]
    public void Row_order_does_not_change_the_hash()
    {
        var input = Fixture.Input();
        var shuffled = input with
        {
            Facts = input.Facts.Reverse().ToArray(),
            Personas = input.Personas.Reverse().ToArray(),
            Assets = input.Assets.Reverse().ToArray(),
        };

        // Otherwise every campaign would create a new brand version simply because the
        // database returned rows in a different order.
        BrandBrainAssembler.Assemble(shuffled, Now).ComputeHash()
            .ShouldBe(BrandBrainAssembler.Assemble(input, Now).ComputeHash());
    }

    [Fact]
    public void A_real_change_moves_the_hash()
    {
        var before = BrandBrainAssembler.Assemble(Fixture.Input(), Now).ComputeHash();

        var input = Fixture.Input();
        var extra = new ProductFact(Fixture.TenantId, Fixture.BrandId, "new-fact", "Something else is true.", FactCategory.Feature);

        var after = BrandBrainAssembler.Assemble(input with { Facts = [.. input.Facts, extra] }, Now).ComputeHash();

        after.ShouldNotBe(before);
    }

    [Fact]
    public void A_snapshot_round_trips_through_json()
    {
        var snapshot = BrandBrainAssembler.Assemble(Fixture.Input(), Now);

        var restored = BrandSnapshot.FromJson(snapshot.ToJson());

        restored.ComputeHash().ShouldBe(snapshot.ComputeHash());
        restored.Facts.Count.ShouldBe(snapshot.Facts.Count);
        restored.Voice.ForbiddenClaims.ShouldBe(snapshot.Voice.ForbiddenClaims);
    }

    [Fact]
    public void An_expired_fact_is_not_visible_to_agents()
    {
        var input = Fixture.Input();
        var expired = new ProductFact(Fixture.TenantId, Fixture.BrandId, "launch-offer", "Half price in March.", FactCategory.Pricing);
        expired.SetValidity(Now.AddMonths(-6), Now.AddMonths(-3));

        var snapshot = BrandBrainAssembler.Assemble(input with { Facts = [.. input.Facts, expired] }, Now);

        // A fact an agent can see is a fact it may cite, so expiry is enforced by omission
        // rather than by asking the model to check dates.
        snapshot.Facts.ShouldNotContain(f => f.Key == "launch-offer");
    }

    [Fact]
    public void A_private_fact_is_not_visible_to_agents()
    {
        var input = Fixture.Input();
        var internalFact = new ProductFact(Fixture.TenantId, Fixture.BrandId, "churn", "Monthly churn is 4%.", FactCategory.Company);
        internalFact.SetVisibility(isPublic: false);

        var snapshot = BrandBrainAssembler.Assemble(input with { Facts = [.. input.Facts, internalFact] }, Now);

        snapshot.Facts.ShouldNotContain(f => f.Key == "churn");
    }

    [Fact]
    public void Archived_assets_and_derived_variants_are_hidden_from_the_director()
    {
        var input = Fixture.Input();

        var archived = Fixture.Asset("old-logo.png", AssetKind.Logo);
        archived.Archive();

        var thumbnail = BrandAsset.Derived(input.Assets.First(), "thumb", "k", "image/png", "sha-thumb", 100, 48, 48);

        var snapshot = BrandBrainAssembler.Assemble(
            input with { Assets = [.. input.Assets, archived, thumbnail] }, Now);

        snapshot.Assets.ShouldNotContain(a => a.FileName == "old-logo.png");
        snapshot.Assets.Count.ShouldBe(input.Assets.Count);
    }

    [Fact]
    public void The_primary_persona_comes_first()
    {
        var snapshot = BrandBrainAssembler.Assemble(Fixture.Input(), Now);

        snapshot.Personas[0].IsPrimary.ShouldBeTrue();
    }

    [Fact]
    public void A_brand_with_no_profile_still_produces_a_usable_snapshot()
    {
        var snapshot = BrandBrainAssembler.Assemble(Fixture.Input() with { Profile = null }, Now);

        // Onboarding is incremental; a half-filled brand must not crash the pipeline.
        snapshot.Visual.PrimaryColor.ShouldNotBeNullOrWhiteSpace();
        snapshot.Voice.Summary.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Diagnostics_name_what_would_produce_bad_content()
    {
        var empty = BrandBrainAssembler.Assemble(
            Fixture.Input() with { Profile = null, Facts = [], Personas = [], Assets = [] }, Now);

        var warnings = BrandBrainAssembler.Diagnose(empty);

        warnings.ShouldContain(w => w.Contains("product facts"));
        warnings.ShouldContain(w => w.Contains("personas"));
        warnings.ShouldContain(w => w.Contains("screenshots"));
    }

    [Fact]
    public void A_complete_brand_raises_no_warnings()
    {
        var snapshot = BrandBrainAssembler.Assemble(Fixture.Input(), Now);

        BrandBrainAssembler.Diagnose(snapshot).ShouldBeEmpty();
    }

    [Fact]
    public void An_invalid_colour_is_reported_rather_than_reaching_a_stylesheet()
    {
        var visual = new VisualIdentity { PrimaryColor = "red; } body { display:none" };

        visual.Validate().ShouldNotBeEmpty();
    }
}
