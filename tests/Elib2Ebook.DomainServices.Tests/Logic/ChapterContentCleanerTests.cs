using Elib2Ebook.DomainServices.Extensions;
using Elib2Ebook.DomainServices.Misc;

namespace Elib2Ebook.DomainServices.Tests.Logic;

public class ChapterContentCleanerTests
{
    [Theory]
    [InlineData("<h1>Глава 1</h1>")]
    [InlineData("<h2> Глава&nbsp;1\n</h2>")]
    [InlineData("<div><section><h4><b>Глава 1</b></h4></section></div>")]
    public void RemovesOnlyMatchingLeadingHeading(string heading)
    {
        var doc = (heading + "<p>Текст.</p><h2>Подзаголовок</h2><h1>Глава 1</h1><p>Продолжение.</p>").AsHtmlDoc();

        ChapterContentCleaner.RemoveLeadingTitle(doc.DocumentNode, "Глава 1");

        Assert.Contains("<h2>Подзаголовок</h2>", doc.DocumentNode.InnerHtml);
        Assert.Contains("<h1>Глава 1</h1><p>Продолжение.</p>", doc.DocumentNode.InnerHtml);
        Assert.Equal(1, doc.DocumentNode.Descendants().Count(n => n.Name.StartsWith('h') && n.InnerText == "Глава 1"));
    }

    [Theory]
    [InlineData("<p>Глава 1</p>")]
    [InlineData("<h2>Другой заголовок</h2>")]
    [InlineData("<p>Эпиграф</p><h1>Глава 1</h1>")]
    [InlineData("<h1>Глава 1<img src='image.png'></h1>")]
    [InlineData("<h1><a href='https://author.example/'>Глава 1</a></h1>")]
    public void PreservesProseDifferentHeadingsAndMedia(string html)
    {
        var doc = html.AsHtmlDoc();
        var original = doc.DocumentNode.InnerHtml;
        ChapterContentCleaner.RemoveLeadingTitle(doc.DocumentNode, "Глава 1");
        Assert.Equal(original, doc.DocumentNode.InnerHtml);
    }

    [Fact]
    public void KeepsAnchorsWhenHeadingIsRemoved()
    {
        var doc = "<h1 id='chapter'><a name='old'></a><span id='part'>Глава 1</span></h1><p><a href='#part'>Назад</a></p>".AsHtmlDoc();
        ChapterContentCleaner.RemoveLeadingTitle(doc.DocumentNode, "Глава 1");
        Assert.Empty(doc.DocumentNode.Descendants("h1"));
        Assert.NotNull(doc.GetElementbyId("chapter"));
        Assert.NotNull(doc.GetElementbyId("part"));
        Assert.Contains(doc.DocumentNode.Descendants("a"), a => a.GetAttributeValue("name", "") == "old");
        Assert.Contains("href='#part'", doc.DocumentNode.InnerHtml.Replace('"', '\''));
    }

    [Fact]
    public void FindsVisibleTitleInsideCompleteHtmlDocument()
    {
        var doc = "<html><head><title>Книга</title><style>h2{color:red}</style></head><body><h2>Глава 1</h2><p>Текст</p><h2>Подзаголовок</h2></body></html>".AsHtmlDoc();
        ChapterContentCleaner.RemoveLeadingTitle(doc.DocumentNode, "Глава 1");
        Assert.Equal("Подзаголовок", Assert.Single(doc.DocumentNode.Descendants("h2")).InnerText);
        Assert.Equal("Книга", Assert.Single(doc.DocumentNode.Descendants("title")).InnerText);
    }

    [Fact]
    public void RemovesExactTrailingSourceAmongExplicitAliasesOnly()
    {
        var doc = "<p>Текст.</p><p>http://mirror.example/book/1/2</p>".AsHtmlDoc();
        ChapterContentCleaner.RemoveTrailingSourceLink(doc.DocumentNode,
            new Uri("https://main.example/book/1/2"), new Uri("https://mirror.example/book/1/2"));
        Assert.Equal("<p>Текст.</p>", doc.DocumentNode.InnerHtml);
    }

    [Theory]
    [InlineData("<p>https://main.example/book/1/3</p>")]
    [InlineData("<p>Источник: https://main.example/book/1/2</p>")]
    [InlineData("<p>https://main.example/book/1/2</p><p>Продолжение</p>")]
    [InlineData("<p>https://author.example/book/1/2</p>")]
    public void KeepsOtherLinksAndAuthorText(string html)
    {
        var doc = html.AsHtmlDoc();
        var original = doc.DocumentNode.InnerHtml;
        ChapterContentCleaner.RemoveTrailingSourceLink(doc.DocumentNode, new Uri("https://main.example/book/1/2"));
        Assert.Equal(original, doc.DocumentNode.InnerHtml);
    }
}
