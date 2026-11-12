using Elib2Ebook.Domain.Common;

namespace Elib2Ebook.DomainServices.Tests.Getters;

public sealed class TempFileFailureTests
{
    [Fact]
    public async Task InterruptedStreamLeavesNoPartialFile()
    {
        var directory = Directory.CreateTempSubdirectory("e2e-stream-");
        try
        {
            using var input = new BrokenStream();
            await Assert.ThrowsAsync<IOException>(() => TempFile.Create(new Uri("https://example.com/image"), directory.FullName, "image.png", input));
            Assert.Empty(directory.GetFiles());
        }
        finally { directory.Delete(true); }
    }

    private sealed class BrokenStream : MemoryStream
    {
        public override bool CanSeek => false;
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken token)
        {
            await destination.WriteAsync(new byte[] { 1, 2, 3 }, token);
            throw new IOException("Соединение оборвалось");
        }
    }
}
