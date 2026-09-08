using ContentPilot.Renderer.Endpoints;
using ContentPilot.Renderer.Engine;
using ContentPilot.Renderer.Imaging;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<RendererOptions>()
    .Bind(builder.Configuration.GetSection(RendererOptions.SectionName))
    .Validate(o => o.DeviceScaleFactor is >= 1 and <= 4, "Renderer:DeviceScaleFactor must be between 1 and 4.")
    .Validate(o => o.MaxConcurrentRenders > 0, "Renderer:MaxConcurrentRenders must be positive.")
    .ValidateOnStart();

// Fonts and templates are loaded once and validated at startup. A missing font or a
// malformed manifest must stop the process, not surface as a subtly wrong image later.
builder.Services.AddSingleton(sp => new FontLibrary(
    ResolvePath(sp.GetRequiredService<IOptions<RendererOptions>>().Value.FontDirectory),
    sp.GetRequiredService<ILogger<FontLibrary>>()));

builder.Services.AddSingleton<TemplateCatalog>();
builder.Services.AddSingleton<DocumentBuilder>();
builder.Services.AddSingleton<BrowserPool>();
builder.Services.AddSingleton<ContrastAnalyzer>();
builder.Services.AddSingleton<FidelityComparer>();
builder.Services.AddSingleton<ImageRenderService>();

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

// Payloads carry base64 assets, so the default form limits are irrelevant but the JSON
// ceiling is not: a 12 MB screenshot inflates to roughly 16 MB of base64.
builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(o =>
    o.SerializerOptions.PropertyNameCaseInsensitive = true);

builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 64 * 1024 * 1024);

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Readiness is not liveness: the process answers /health long before Chromium has been
// launched and the first page has rendered.
app.MapGet("/health/ready", async (TemplateCatalog catalog, FontLibrary fonts) =>
    Results.Ok(new
    {
        status = "ready",
        templates = catalog.Manifests.Select(m => $"{m.TemplateId}@{m.Version}").ToArray(),
        fonts = fonts.Families,
        chromium = await Task.FromResult(Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH") ?? "default"),
    }));

app.MapRenderEndpoints();

app.Run();

static string ResolvePath(string configured) =>
    Path.IsPathRooted(configured)
        ? configured
        : Path.Combine(AppContext.BaseDirectory, configured);

/// <summary>Exposed so the renderer tests can boot the real service.</summary>
public partial class Program;
