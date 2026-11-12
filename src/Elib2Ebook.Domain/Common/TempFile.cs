namespace Elib2Ebook.Domain.Common;

public class TempFile : IDisposable
{
    public string Name;

    public string Extension;

    public Uri Url { get; set; }

    public string Directory { get; set; }

    public byte[] Content => File.Exists(FilePath) ? File.ReadAllBytes(FilePath) : [];

    public string FullName => $"{Name}{Extension}";

    public string FilePath => Path.Combine(Directory, FullName);

    public long Size => Content.Length;

    private TempFile(Uri url, string directory, string name, string extension)
    {
        Directory = directory;
        Url = url;
        Name = name;
        Extension = extension;
    }

    public static async Task<TempFile> Create(Uri url, string directory, string fullName, byte[] content)
    {
        return await Create(url, directory, Path.GetFileNameWithoutExtension(fullName), Path.GetExtension(fullName), content);
    }

    public static async Task<TempFile> Create(Uri url, string directory, string fullName, Stream stream)
    {
        return await Create(url, directory, Path.GetFileNameWithoutExtension(fullName), Path.GetExtension(fullName), stream);
    }

    public static async Task<TempFile> Create(Uri url, string directory, string name, string extension, byte[] content)
    {
        var image = new TempFile(url, directory, name, extension);
        await File.WriteAllBytesAsync(image.FilePath, content);
        return image;
    }

    public static async Task<TempFile> Create(Uri url, string directory, string name, string extension, Stream stream)
    {
        if (stream.CanSeek && stream.Length == 0)
        {
            return null;
        }

        var tempFile = new TempFile(url, directory, name, extension);
        var completed = false;
        try
        {
            long length;
            await using (var file = File.Create(tempFile.FilePath))
            {
                await stream.CopyToAsync(file);
                length = file.Length;
            }
            if (length == 0) return null;
            // Владение передаётся вызывающему только после успешного закрытия потока.
            completed = true;
            return tempFile;
        }
        finally
        {
            if (!completed) tempFile.Dispose();
        }
    }

    public Stream GetStream()
    {
        return File.OpenRead(FilePath);
    }

    public void Dispose()
    {
        if (File.Exists(FilePath))
        {
            File.Delete(FilePath);
        }
    }
}
