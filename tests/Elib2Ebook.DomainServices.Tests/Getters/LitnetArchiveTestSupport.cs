using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Elib2Ebook.ExternalServices.Litnet.Getters;

namespace Elib2Ebook.DomainServices.Tests.Getters;

internal static class LitnetArchiveTestSupport
{
    // Параметр публичного протокола, не пользовательские реквизиты.
    internal static string AppKey => (string)typeof(LitnetGetterBase)
        .GetField("SECRET", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue();

    internal static byte[] Encrypt(string json)
    {
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            gzip.Write(Encoding.UTF8.GetBytes(json));
        return EncryptBytes(compressed.ToArray());
    }

    internal static byte[] EncryptBytes(byte[] plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = Encoding.UTF8.GetBytes(AppKey);
        var prefixed = new byte[16 + plaintext.Length];
        plaintext.CopyTo(prefixed, 16);
        // Первый блок отбрасывается протоколом: нулевой IV fixture не влияет на содержимое после него.
        return aes.EncryptCbc(prefixed, new byte[16]);
    }

    internal static byte[] Archive(IEnumerable<(string Name, byte[] Bytes)> entries)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var entry in entries)
            {
                using var target = zip.CreateEntry(entry.Name).Open();
                target.Write(entry.Bytes ?? []);
            }
        return output.ToArray();
    }

    internal static HttpResponseMessage Response(IEnumerable<(long Id, string Json)> chapters, HttpStatusCode status = HttpStatusCode.OK)
    {
        if (status != HttpStatusCode.OK) return new(status) { Content = new StringContent("{}") };
        var content = new ByteArrayContent(Archive(chapters.Select(chapter => ($"{chapter.Id}.gz", Encrypt(chapter.Json)))));
        content.Headers.ContentType = new("application/zip");
        return new(status) { Content = content };
    }
}
