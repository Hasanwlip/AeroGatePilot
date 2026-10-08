using AeroGatePilot.Portal;

namespace AeroGatePilot.Tests;

public class TemplateRendererTests
{
    [Fact]
    public void Encodes_values_and_keeps_raw_values()
    {
        var html = TemplateRenderer.Render(
            "<h1>{{title}}</h1>{{{raw}}}",
            new Dictionary<string, string> { ["title"] = "<b>Café</b>", ["raw"] = "<i>x</i>" },
            new HashSet<string>());
        Assert.Equal("<h1>&lt;b&gt;Caf&#233;&lt;/b&gt;</h1><i>x</i>", html);
    }

    [Fact]
    public void Conditional_blocks_including_nested()
    {
        const string template = "<!--IF:a-->A<!--IF:b-->B<!--ENDIF:b--><!--ENDIF:a--><!--IF:c-->C<!--ENDIF:c-->";
        Assert.Equal("AB", TemplateRenderer.Render(template, new Dictionary<string, string>(), new HashSet<string> { "a", "b" }));
        Assert.Equal("A", TemplateRenderer.Render(template, new Dictionary<string, string>(), new HashSet<string> { "a" }));
        Assert.Equal("C", TemplateRenderer.Render(template, new Dictionary<string, string>(), new HashSet<string> { "b", "c" }));
    }

    [Fact]
    public void Unknown_placeholders_render_empty()
    {
        Assert.Equal("[]", TemplateRenderer.Render("[{{missing}}]", new Dictionary<string, string>(), new HashSet<string>()));
    }
}
