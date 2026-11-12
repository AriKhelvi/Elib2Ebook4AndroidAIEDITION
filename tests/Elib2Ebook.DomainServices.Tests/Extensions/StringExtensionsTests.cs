using Elib2Ebook.DomainServices.Extensions;

namespace Elib2Ebook.DomainServices.Tests.Extensions;

public class StringExtensionsTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("Hello World.txt", "Hello World.txt")]
    [InlineData("Книга 🐠 👩🏽‍💻.fb2", "Книга 🐠 👩🏽‍💻.fb2")]
    public void RemoveInvalidChars(string input, string expected)
    {
        Assert.Equal(expected, input.RemoveInvalidChars());
    }

    [Theory]
    [InlineData("test", "\"test\"")]
    public void CoverQuotes(string input, string expected)
    {
        Assert.Equal(expected, input.CoverQuotes());
    }

    [Theory]
    [InlineData("hello", 10, "hello")]
    [InlineData("hello world", 5, "hello")]
    [InlineData("A😀B", 2, "A")]
    [InlineData("A😀B", 3, "A😀")]
    [InlineData("A👩🏽‍💻B", 7, "A")]
    [InlineData("A👩🏽‍💻B", 8, "A👩🏽‍💻")]
    [InlineData("A🇷🇺B", 4, "A")]
    [InlineData("A🇷🇺B", 5, "A🇷🇺")]
    [InlineData("Aе\u0301B", 2, "A")]
    [InlineData("Aе\u0301B", 3, "Aе\u0301")]
    [InlineData("😀", 0, "")]
    public void Crop(string input, int len, string expected)
    {
        Assert.Equal(expected, input.Crop(len));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", "   ")]
    [InlineData("text", "text")]
    public void HtmlDecode(string input, string expected)
    {
        Assert.Equal(expected, input.HtmlDecode());
    }

    [Theory]
    [InlineData("text", "b", "<b>text</b>")]
    [InlineData("text", "", "text")]
    public void CoverTag(string input, string tag, string expected)
    {
        Assert.Equal(expected, input.CoverTag(tag));
    }

    [Theory]
    [InlineData("line1\nline2\tline3")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ReplaceNewLine(string input)
    {
        var result = input.ReplaceNewLine();
        if (input is null)
        {
            Assert.Null(result);
        }
        else
        {
            Assert.DoesNotContain("\n", result);
            Assert.DoesNotContain("\t", result);
        }
    }

    [Theory]
    [InlineData("a   b  c", "a b c")]
    [InlineData("", "")]
    public void CollapseWhitespace(string input, string expected)
    {
        Assert.Equal(expected, input.CollapseWhitespace());
    }

    [Fact]
    public void HtmlEncode()
    {
        var result = "a<b>c".HtmlEncode();
        Assert.Contains("&", result);
    }

    [Fact]
    public void AsUri()
    {
        Assert.Equal("https://example.com/", "https://example.com".AsUri().ToString());
    }

    [Theory]
    [InlineData("te\x00st", "test")]
    [InlineData("🐠 👩🏽‍💻 🇷🇺 ☀️", "🐠 👩🏽‍💻 🇷🇺 ☀️")]
    [InlineData("\u0001😀\u0002", "😀")]
    public void CleanInvalidXmlChars(string input, string expected)
    {
        Assert.Equal(expected, input.CleanInvalidXmlChars());
    }

    [Theory]
    [InlineData("<html><body><p>test</p></body></html>")]
    public void AsHtmlDoc(string html)
    {
        Assert.NotNull(html.AsHtmlDoc().DocumentNode);
    }

    [Theory]
    [InlineData("<html><body><p>test</p></body></html>")]
    public void AsXHtmlDoc(string html)
    {
        Assert.NotNull(html.AsXHtmlDoc());
    }
}
