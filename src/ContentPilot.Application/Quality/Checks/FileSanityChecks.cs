using ContentPilot.Domain.Quality;
using ContentPilot.Rendering.Contracts;

namespace ContentPilot.Application.Quality.Checks;

/// <summary>
/// The unglamorous checks that catch whole classes of silent failure: wrong canvas, blank
/// frame, a font that never loaded, a page that tried to phone home. None of them cost
/// anything, and each one has a failure mode that every later gate would happily pass.
/// </summary>
internal static class FileSanityChecks
{
    public static IEnumerable<QaFinding> Run(DeterministicQaInput input)
    {
        var (expectedWidth, expectedHeight) = input.AspectRatio.Dimensions();

        if (input.Report.Width != expectedWidth || input.Report.Height != expectedHeight)
        {
            yield return QaFinding.Deterministic(
                QaFindingCode.WrongDimensions,
                QaSeverity.Blocking,
                $"Rendered at {input.Report.Width}x{input.Report.Height}; {input.AspectRatio} is {expectedWidth}x{expectedHeight}.",
                measured: input.Report.Width,
                threshold: expectedWidth);
        }

        if (input.File is { } file)
        {
            if (file.Width != expectedWidth || file.Height != expectedHeight)
            {
                yield return QaFinding.Deterministic(
                    QaFindingCode.WrongDimensions,
                    QaSeverity.Blocking,
                    $"The encoded file is {file.Width}x{file.Height}; {input.AspectRatio} is {expectedWidth}x{expectedHeight}.",
                    measured: file.Width,
                    threshold: expectedWidth);
            }

            if (file.Bytes < input.Options.MinPlausibleBytes)
            {
                // A 1080-wide PNG this small is a blank frame or a render that lost its
                // content. Cheap to check, and it fails loudly instead of shipping a
                // beautifully composed empty rectangle.
                yield return QaFinding.Deterministic(
                    QaFindingCode.ImplausibleFileSize,
                    QaSeverity.Blocking,
                    $"The render is only {file.Bytes:N0} bytes, below the {input.Options.MinPlausibleBytes:N0} byte floor for a real image.",
                    measured: file.Bytes,
                    threshold: input.Options.MinPlausibleBytes);
            }
            else if (file.Bytes > input.Options.MaxPlausibleBytes)
            {
                yield return QaFinding.Deterministic(
                    QaFindingCode.ImplausibleFileSize,
                    QaSeverity.Minor,
                    $"The render is {file.Bytes / 1_000_000.0:F1} MB, above the {input.Options.MaxPlausibleBytes / 1_000_000.0:F1} MB delivery ceiling.",
                    measured: file.Bytes,
                    threshold: input.Options.MaxPlausibleBytes);
            }
        }

        foreach (var family in input.ExpectedFonts)
        {
            if (input.Report.FontsLoaded.Contains(family, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            // A missing family falls back to something plausible and the image still looks
            // like a design — just not this brand's. Nothing downstream would notice.
            yield return QaFinding.Deterministic(
                QaFindingCode.FontFallback,
                QaSeverity.Major,
                $"Font family '{family}' did not resolve; the render fell back to another face.",
                measured: null);
        }

        foreach (var request in input.Report.BlockedRequests)
        {
            // The network is blocked, so nothing loaded. That the page wanted to is still a
            // defect: it means a template referenced something it cannot have.
            yield return QaFinding.Deterministic(
                QaFindingCode.BlockedNetworkRequest,
                QaSeverity.Major,
                $"The template attempted a network request to '{request}', which the renderer blocked.");
        }
    }
}
