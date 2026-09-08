using ContentPilot.Rendering.Contracts;
using ContentPilot.Renderer.Engine;
using Shouldly;

namespace ContentPilot.RendererTests;

/// <summary>
/// Property tests rather than pixel goldens. Committed baselines only mean something when
/// they are produced by the same Chromium and font set as production, so they are generated
/// inside the renderer image; what is asserted here holds on any host.
/// </summary>
[Collection(RenderCollection.Name)]
public sealed class TemplateRenderTests(RenderFixture fixture)
{
    [RenderFact]
    public async Task Every_template_renders_at_every_ratio_it_claims_to_support()
    {
        foreach (var manifest in fixture.Catalog.Manifests)
        {
            foreach (var ratio in manifest.AspectRatios)
            {
                var request = SampleRequests.For(manifest, ratio);
                var response = await fixture.Renderer.RenderAsync(request, CancellationToken.None);

                fixture.Save($"{manifest.TemplateId}-{ratio}", response.Image);

                var (expectedWidth, expectedHeight) = ratio.Dimensions();

                response.Report.Width.ShouldBe(expectedWidth);
                response.Report.Height.ShouldBe(expectedHeight);
                response.Image.ToBytes().Length.ShouldBeGreaterThan(10_000, "A near-empty file means nothing painted.");
            }
        }
    }

    [RenderFact]
    public async Task No_slot_overflows_when_copy_is_within_budget()
    {
        foreach (var manifest in fixture.Catalog.Manifests)
        {
            var request = SampleRequests.AtBudget(manifest, manifest.AspectRatios[0]);
            var response = await fixture.Renderer.RenderAsync(request, CancellationToken.None);

            var overflowing = response.Report.Slots.Where(s => s.Overflows).Select(s => s.SlotId).ToArray();

            // This is the contract between the manifest and the copywriter. If copy that
            // exactly fills the budget clips, the budget is wrong, not the copy.
            overflowing.ShouldBeEmpty(
                $"{manifest.TemplateId} clipped {string.Join(", ", overflowing)} with copy at its declared budget.");
        }
    }

    [RenderFact]
    public async Task Fonts_resolve_and_nothing_leaves_the_page()
    {
        var manifest = fixture.Catalog.Get("phone-floating").Manifest;
        var request = SampleRequests.For(manifest, AspectRatio.FourFive);

        var response = await fixture.Renderer.RenderAsync(request, CancellationToken.None);

        // A missing family silently falls back to a system face and produces a subtly wrong
        // image that QA may well pass, so both are asserted rather than assumed.
        response.Report.FontsLoaded.ShouldContain("Inter");
        response.Report.FontsLoaded.ShouldContain("Archivo");
        response.Report.BlockedRequests.ShouldBeEmpty("A template referenced something external.");
    }

    [RenderFact]
    public async Task An_immutable_screenshot_is_never_covered()
    {
        foreach (var templateId in new[] { "phone-floating", "feature-highlight" })
        {
            var manifest = fixture.Catalog.Get(templateId).Manifest;
            var request = SampleRequests.For(manifest, manifest.AspectRatios[0]);

            var response = await fixture.Renderer.RenderAsync(request, CancellationToken.None);

            var slot = response.Report.Slots.Single(s => s.SlotId == "screenshot");
            var declared = manifest.FindAssetSlot("screenshot")!;

            slot.Occlusion.ShouldBeLessThanOrEqualTo(
                declared.MaxOcclusion,
                $"{templateId} paints {slot.Occlusion:P1} of the product screenshot over.");

            response.Masks.ShouldContainKey("screenshot");
        }
    }

    [RenderFact]
    public async Task Text_keeps_readable_contrast_against_what_is_actually_behind_it()
    {
        foreach (var manifest in fixture.Catalog.Manifests)
        {
            var request = SampleRequests.For(manifest, manifest.AspectRatios[0]);
            var response = await fixture.Renderer.RenderAsync(request, CancellationToken.None);

            foreach (var slot in response.Report.Slots.Where(s => s.ContrastRatio is not null))
            {
                // WCAG AA for large text. Every text slot in these templates is large.
                slot.ContrastRatio!.Value.ShouldBeGreaterThan(
                    3.0,
                    $"{manifest.TemplateId}/{slot.SlotId} renders at {slot.ContrastRatio:F1}:1.");
            }
        }
    }

    [RenderFact]
    public async Task The_same_request_renders_the_same_bytes()
    {
        var manifest = fixture.Catalog.Get("hook-overlay").Manifest;
        var request = SampleRequests.For(manifest, AspectRatio.FourFive);

        var first = await fixture.Renderer.RenderAsync(request, CancellationToken.None);
        var second = await fixture.Renderer.RenderAsync(request, CancellationToken.None);

        // Everything downstream assumes this: golden tests, fidelity thresholds, and the
        // ability to re-render a stored spec and get the same asset back.
        second.Image.Base64.ShouldBe(first.Image.Base64, "Rendering is not deterministic.");
    }

    [RenderFact]
    public async Task Shrink_to_fit_absorbs_a_near_miss_and_says_so()
    {
        var manifest = fixture.Catalog.Get("safe-mode").Manifest;
        var slot = manifest.FindTextSlot("headline")!;

        var request = SampleRequests.For(manifest, AspectRatio.FourFive) with
        {
            Text = new Dictionary<string, string>
            {
                ["eyebrow"] = "Za salone",
                ["headline"] = new string('M', slot.MaxChars), // widest glyph, worst case
                ["subhead"] = "Rezervacije koje se same potvrđuju.",
                ["cta"] = "Probaj besplatno",
            },
        };

        var response = await fixture.Renderer.RenderAsync(request, CancellationToken.None);
        var measured = response.Report.Slots.Single(s => s.SlotId == "headline");

        measured.Overflows.ShouldBeFalse("Shrink-to-fit exists precisely so this does not clip.");
        measured.FontSizePx.ShouldBeGreaterThanOrEqualTo(slot.MinFontPx);
    }

    [RenderFact]
    public async Task Croatian_diacritics_render_rather_than_falling_back()
    {
        var manifest = fixture.Catalog.Get("safe-mode").Manifest;

        var request = SampleRequests.For(manifest, AspectRatio.FourFive) with
        {
            Language = "hr",
            Text = new Dictionary<string, string>
            {
                ["eyebrow"] = "Čišćenje",
                ["headline"] = "Šišanje, njega i đir",
                ["subhead"] = "Termini bez čekanja i bez poruka.",
                ["cta"] = "Rezerviraj",
            },
        };

        var response = await fixture.Renderer.RenderAsync(request, CancellationToken.None);
        fixture.Save("safe-mode-croatian", response.Image);

        // Tofu boxes would still "render"; what proves coverage is that the latin-ext face
        // loaded and the line did not overflow its box.
        response.Report.FontsLoaded.ShouldContain("Archivo");
        response.Report.Slots.Single(s => s.SlotId == "headline").Overflows.ShouldBeFalse();
    }

    [RenderFact]
    public async Task Copy_over_budget_is_refused_rather_than_clipped()
    {
        var manifest = fixture.Catalog.Get("phone-floating").Manifest;
        var slot = manifest.FindTextSlot("headline")!;

        var request = SampleRequests.For(manifest, AspectRatio.FourFive) with
        {
            Text = new Dictionary<string, string>
            {
                ["headline"] = new string('a', slot.MaxChars + 1),
                ["subhead"] = "Fits fine.",
                ["cta"] = "Book now",
            },
        };

        var ex = await Should.ThrowAsync<RenderFailedException>(
            () => fixture.Renderer.RenderAsync(request, CancellationToken.None));

        ex.Message.ShouldContain("headline");
    }

    [RenderFact]
    public async Task Croatian_gets_a_tighter_budget_than_english()
    {
        var manifest = fixture.Catalog.Get("phone-floating").Manifest;
        var slot = manifest.FindTextSlot("headline")!;

        slot.BudgetFor("hr").ShouldBeLessThan(slot.BudgetFor("en"));

        var request = SampleRequests.For(manifest, AspectRatio.FourFive) with
        {
            Language = "hr",
            Text = new Dictionary<string, string>
            {
                ["headline"] = new string('a', slot.BudgetFor("en")),
                ["subhead"] = "Fits fine.",
                ["cta"] = "Rezerviraj",
            },
        };

        // Same string, legal in English, refused in Croatian: German-style compounds and
        // Croatian inflection run longer for the same meaning.
        await Should.ThrowAsync<RenderFailedException>(
            () => fixture.Renderer.RenderAsync(request, CancellationToken.None));
    }

    [RenderFact]
    public async Task A_spec_pinned_to_an_older_template_version_is_refused()
    {
        var manifest = fixture.Catalog.Get("testimonial").Manifest;

        var request = SampleRequests.For(manifest, AspectRatio.FourFive) with
        {
            TemplateVersion = manifest.Version + 1,
        };

        var ex = await Should.ThrowAsync<RenderFailedException>(
            () => fixture.Renderer.RenderAsync(request, CancellationToken.None));

        ex.Message.ShouldContain("version");
    }

    [RenderFact]
    public async Task An_unsupported_aspect_ratio_is_refused()
    {
        var manifest = fixture.Catalog.Get("feature-highlight").Manifest;
        manifest.Supports(AspectRatio.NineSixteen).ShouldBeFalse();

        var request = SampleRequests.For(manifest, AspectRatio.FourFive) with
        {
            AspectRatio = AspectRatio.NineSixteen,
        };

        await Should.ThrowAsync<RenderFailedException>(
            () => fixture.Renderer.RenderAsync(request, CancellationToken.None));
    }
}
