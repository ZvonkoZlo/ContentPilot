using ContentPilot.Rendering.Contracts;
using ContentPilot.Renderer.Engine;
using ContentPilot.Renderer.Video;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace ContentPilot.RendererTests.Video;

public sealed class ReelManifestTests
{
    private readonly ReelTemplateCatalog _catalog = CreateCatalog();

    [Fact]
    public void Three_reel_templates_load_and_exactly_one_is_safe_mode()
    {
        _catalog.Manifests.Count.ShouldBe(3);
        _catalog.Manifests.Count(m => m.SafeMode).ShouldBe(1);
        _catalog.Manifests.Select(m => m.TemplateId).ShouldBe(
            ["reel-before-after", "reel-feature-tour", "reel-problem-solution"],
            ignoreOrder: true);
    }

    [Fact]
    public void Default_scene_timelines_produce_fifteen_seconds()
    {
        foreach (var manifest in _catalog.Manifests)
        {
            var spec = ReelSamples.For(manifest);

            spec.DurationSeconds.ShouldBe(15, tolerance: 0.002);
            ReelSpecValidator.Validate(spec, manifest, 12 * 1024 * 1024);
        }
    }

    [Fact]
    public void Every_scene_has_its_own_slot_budget_and_readable_duration()
    {
        foreach (var scene in _catalog.Manifests.SelectMany(m => m.Scenes))
        {
            scene.TextSlots.ShouldNotBeEmpty();
            scene.TextSlots.ShouldAllBe(slot => slot.MaxChars > 0 && slot.MaxLines > 0);
            scene.MinDurationSeconds.ShouldBeGreaterThanOrEqualTo(1.2);
        }
    }

    [Fact]
    public void Over_budget_copy_is_rejected_before_rendering()
    {
        var manifest = _catalog.Get("reel-problem-solution");
        var spec = ReelSamples.For(manifest);
        var first = spec.Scenes[0];
        var slot = manifest.Scenes[0].TextSlots.Single(s => s.Id == "headline");
        var scenes = spec.Scenes.ToArray();
        scenes[0] = first with
        {
            Text = new Dictionary<string, string>(first.Text)
            {
                ["headline"] = new string('x', slot.MaxChars + 1),
            },
        };

        var exception = Should.Throw<ReelRenderException>(() =>
            ReelSpecValidator.Validate(spec with { Scenes = scenes }, manifest, 12 * 1024 * 1024));

        exception.Message.ShouldContain("headline");
    }

    [Fact]
    public void Missing_product_screenshot_is_rejected_before_rendering()
    {
        var manifest = _catalog.Get("reel-feature-tour");
        var spec = ReelSamples.For(manifest);
        var scenes = spec.Scenes.ToArray();
        scenes[1] = scenes[1] with { Assets = new Dictionary<string, ImagePayload>() };

        var exception = Should.Throw<ReelRenderException>(() =>
            ReelSpecValidator.Validate(spec with { Scenes = scenes }, manifest, 12 * 1024 * 1024));

        exception.Message.ShouldContain("screenshot");
    }

    internal static ReelTemplateCatalog CreateCatalog()
    {
        var root = FindRepositoryRoot();
        var options = Options.Create(new RendererOptions
        {
            TemplateDirectory = Path.Combine(root, "src", "ContentPilot.Renderer", "Templates"),
        });

        return new ReelTemplateCatalog(options, NullLogger<ReelTemplateCatalog>.Instance);
    }

    internal static string FindRepositoryRoot()
    {
        var configured = Environment.GetEnvironmentVariable("CONTENTPILOT_REPOSITORY_ROOT");

        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(Path.Combine(configured, "ContentPilot.slnx")))
        {
            return configured;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ContentPilot.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
