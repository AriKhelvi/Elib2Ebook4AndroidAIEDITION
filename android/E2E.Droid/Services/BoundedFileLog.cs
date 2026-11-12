using System.Text;
using Elib2Ebook.DomainServices.Misc;

namespace E2E.Droid.Services;

/// <summary>Журнал на диске с ограниченным размером; сбой записи не должен ломать приложение.</summary>
public sealed class BoundedFileLog(string path, int maxBytes = 1024 * 1024)
{
    private readonly object _sync = new();
    public string Path { get; } = path;

    public void Reset()
    {
        lock (_sync)
        {
            try { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!); File.WriteAllText(Path, ""); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public void Append(string text)
    {
        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.AppendAllText(Path, LogText.Clean(text) + Environment.NewLine, new UTF8Encoding(false));
                if (new FileInfo(Path).Length <= maxBytes) return;
                // Редкая компактизация до половины лимита, чтобы не переписывать весь файл на каждой строке.
                var lines = Read().Reverse().ToList();
                var retained = new List<string>();
                var size = 0;
                foreach (var line in lines)
                {
                    size += Encoding.UTF8.GetByteCount(line) + 2;
                    if (size > maxBytes / 2) break;
                    retained.Add(line);
                }
                retained.Reverse();
                File.WriteAllLines(Path, retained, new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public IReadOnlyList<string> Read()
    {
        lock (_sync)
        {
            try
            {
                if (!File.Exists(Path)) return [];
                using var stream = File.OpenRead(Path);
                if (stream.Length > maxBytes)
                    stream.Seek(Math.Max(0, stream.Length - maxBytes), SeekOrigin.Begin);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                if (stream.Position > 0) reader.ReadLine();
                var lines = new List<string>();
                while (reader.ReadLine() is { } line) lines.Add(LogText.Clean(line));
                return lines;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        }
    }
}
