using Sockless.Port.Volumes;

namespace Sockless.Port.Core.Tests;

public sealed class VolumeSyntaxTests
{
    [Theory]
    [InlineData("/conf/app.json", "JSON")]
    [InlineData("/compose.YAML", "YAML")]
    [InlineData("/a.yml", "YAML")]
    [InlineData("/app.ini", null)]
    [InlineData("/.env", null)]
    public void Knows_files_by_extension(string path, string? language)
    {
        Assert.Equal(language, VolumeSyntax.Language(path));
        Assert.Equal(language is null, VolumeSyntax.Check(path, "x") is null);
    }

    [Theory]
    [InlineData("{\"a\": [1, 2]}")]
    [InlineData("[]")]
    public void Accepts_valid_json(string text) => Assert.Empty(VolumeSyntax.Check("/a.json", text)!);

    [Fact]
    public void Names_the_line_of_a_json_error()
    {
        var problem = Assert.Single(VolumeSyntax.Check("/a.json", "{\n  \"a\": 1,\n  \"b\": }\n")!);

        Assert.Equal(3, problem.Line);
        Assert.DoesNotContain("LineNumber", problem.Message);
        Assert.NotEmpty(problem.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"a\": 1,}")]
    [InlineData("// comment\n{}")]
    public void Json_is_strict(string text) => Assert.NotEmpty(VolumeSyntax.Check("/a.json", text)!);

    [Theory]
    [InlineData("services:\n  web:\n    image: nginx\n")]
    [InlineData("a: 1\n---\nb: [1, 2]\n")]
    [InlineData("")]
    [InlineData("{\"json\": \"is yaml\"}")]
    [InlineData("base: &b {x: 1}\nother: *b\n")]
    public void Accepts_valid_yaml(string text) => Assert.Empty(VolumeSyntax.Check("/a.yaml", text)!);

    [Theory]
    [InlineData("a: 1\n b: 2\n  - c\n", 2)]
    [InlineData("a: [1, 2\nb: 3\n", null)]
    [InlineData("a: 1\na: 2\n", 2)]
    public void Names_the_line_of_a_yaml_error(string text, int? line)
    {
        var problem = Assert.Single(VolumeSyntax.Check("/a.yml", text)!);

        if (line is not null)
            Assert.Equal(line, problem.Line);
        Assert.DoesNotMatch(@"^\(Line:", problem.Message);
        Assert.NotEmpty(problem.Message);
    }
}
