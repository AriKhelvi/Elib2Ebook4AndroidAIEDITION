using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text;
using Elib2Ebook.Domain.Book;
using Elib2Ebook.ExternalServices.DarkNovels;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public class DarkNovelsConcurrencyTests
{
    [Fact]
    public async Task ParallelGettersDecodeChaptersWithoutMutatingSharedCharacterMap()
    {
        const string alphabet = "аАбБвВгГдДеЕёЁжЖзЗиИйЙкКлЛмМнНоОпПрРсСтТуУфФхХцЦчЧшШщЩъЪыЫьЬэЭюЮяЯ";
        var encoded = new string(Enumerable.Range(0, alphabet.Length).Select(index => (char)(13338 + index * 38)).ToArray());
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(archive.CreateEntry("chapter.html").Open(), new UTF8Encoding(false)))
            writer.Write("<p>" + encoded + "</p>");
        var payload = buffer.ToArray();
        var sourceType = typeof(DarkNovelsGetter).Assembly.GetType("Elib2Ebook.ExternalServices.DarkNovels.Types.DarkNovelsChapter")!;
        var fill = typeof(DarkNovelsGetter).GetMethod("FillChapter", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 64).Select(_ => Task.Run(async () =>
        {
            await gate.Task;
            using var getter = new DarkNovelsGetter(AfAuditSupport.Config(new AfAuditSupport.Handler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) })));
            var source = Activator.CreateInstance(sourceType)!;
            sourceType.GetProperty("Id")!.SetValue(source, 1);
            var chapter = new Chapter { Title = "Глава" };
            await (Task)fill.Invoke(getter, ["123", source, chapter])!;
            return chapter.Content;
        })).ToArray();

        gate.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, result => Assert.Equal("<p>" + alphabet + "</p>", result));
    }
}
