// ============================================================================
// E2E для Android — сохранение книг в «Загрузки» (MediaStore) или выбранную папку (SAF), открыть/поделиться/удалить
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================

using Android.Content;
using Android.Provider;
using E2E.Droid.Services;
using AUri = Android.Net.Uri;
using AEnvironment = Android.OS.Environment;

namespace E2E.Droid;

/// <summary>
/// Сохранение в «Загрузки/Elib2Ebook» через MediaStore (без разрешений, Android 10+)
/// или в папку, выбранную через системный диалог (Storage Access Framework).
/// </summary>
/// <remarks>
/// Почему не просто File.Copy в /sdcard/Download: начиная с Android 10 приложениям запрещено писать
/// в общие папки по пути (scoped storage). Разрешённых путей два:
/// <list type="bullet">
/// <item>MediaStore — системная «база» общих файлов. В коллекцию Downloads можно добавлять свои файлы
/// без каких-либо разрешений; удалять и менять — только свои (до переустановки приложения).</item>
/// <item>SAF (Storage Access Framework) — пользователь сам выбирает папку в системном диалоге и выдаёт
/// приложению постоянный доступ к ней. Так можно писать хоть в папку Syncthing, хоть на SD-карту.</item>
/// </list>
/// Все файлы адресуются content://-ссылками (SavedFile.Uri), а не путями.
/// </remarks>
public sealed class AndroidBookStorage(AppSettings settings) : IBookStorage
{
    /// <summary>Подпапка в «Загрузках» по умолчанию.</summary>
    private const string DefaultFolder = "Elib2Ebook";

    /// <summary>Права, которые берём на выбранную папку (и отпускаем при смене папки).</summary>
    private const ActivityFlags TreeFlags = ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission;

    /// <summary>Для запуска чужих экранов лучше текущая Activity; если её нет — контекст приложения + NewTask.</summary>
    private static Context Context => Platform.CurrentActivity ?? Platform.AppContext;

    private static ContentResolver Resolver => Platform.AppContext.ContentResolver!;

    public string DestinationLabel
        => HasTree() ? settings.SaveFolderName : $"Загрузки/{DefaultFolder}";

    /// <summary>
    /// Переносит всё, что ядро собрало в рабочей папке задания (книги, обложку, подпапки с доп. файлами),
    /// в папку сохранения, сохраняя структуру подпапок. Работает в фоновом потоке.
    /// </summary>
    public Task<IReadOnlyList<SavedFile>> SaveTreeAsync(string sourceDir, Func<SavedFile, SavedFile> decorate)
    {
        return Task.Run<IReadOnlyList<SavedFile>>(() =>
        {
            var tree = HasTree() ? AUri.Parse(settings.SaveFolderUri) : null;
            var result = new List<SavedFile>();

            // Сначала файлы книг из корня, затем доп. файлы по подпапкам
            var files = Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories)
                .Select(path => (path, folder: GetFolder(sourceDir, path)))
                .OrderBy(f => f.folder.Length)
                .ThenBy(f => f.path, StringComparer.OrdinalIgnoreCase);

            foreach (var (path, folder) in files)
            {
                var name = Path.GetFileName(path);
                var uri = tree != null
                    ? SaveToTree(tree, folder, name, path)
                    : SaveToDownloads(folder, name, path);

                result.Add(decorate(new SavedFile
                {
                    Uri = uri.ToString(),
                    // Если файл с таким именем уже был, система сама переименует в «Книга (1).epub» —
                    // поэтому настоящее имя спрашиваем у неё, а не берём исходное
                    Name = QueryDisplayName(uri) ?? name,
                    Folder = folder,
                    Mime = MimeTypes.ForIntent(name),
                    Size = new FileInfo(path).Length
                }));
            }

            return result;
        });
    }

    /// <summary>Системный диалог «Выберите папку» (SAF). Доступ к папке сохраняется и после перезапуска.</summary>
    public async Task<bool> PickFolderAsync()
    {
        var intent = new Intent(Intent.ActionOpenDocumentTree);
        intent.AddFlags(TreeFlags | ActivityFlags.GrantPersistableUriPermission | ActivityFlags.GrantPrefixUriPermission);

        var data = await ActivityResults.StartAsync(intent);
        var uri = data?.Data;
        if (uri == null)
        {
            return false; // пользователь нажал «Назад»
        }

        // Без TakePersistableUriPermission доступ пропал бы после перезапуска приложения
        Resolver.TakePersistableUriPermission(uri, TreeFlags);
        // Права на старую папку больше не нужны — отпускаем (у Android есть лимит на число таких прав)
        ReleaseTree(except: uri);

        settings.SaveFolderName = QueryTreeName(uri);
        settings.SaveFolderUri = uri.ToString();
        return true;
    }

    /// <summary>Возврат к «Загрузки/Elib2Ebook».</summary>
    public void ResetFolder()
    {
        ReleaseTree(except: null);
        settings.SaveFolderUri = string.Empty;
        settings.SaveFolderName = string.Empty;
    }

    /// <summary>
    /// Файл ещё на месте? Пользователь мог удалить его в «Файлах» — тогда во вкладке «Книги»
    /// показываем пометку «файл удалён». Любая ошибка доступа тоже считается «нет файла».
    /// </summary>
    public bool Exists(SavedFile file)
    {
        try
        {
            using var cursor = Resolver.Query(AUri.Parse(file.Uri)!, [IOpenableColumns.DisplayName], null, null, null);
            return cursor != null && cursor.MoveToFirst();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Удаление файла. Для MediaStore это работает только с файлами, созданными этой установкой приложения:
    /// после переустановки Android считает их «чужими», и удалить их можно только руками в «Файлах».
    /// </summary>
    public bool Delete(SavedFile file)
    {
        try
        {
            var uri = AUri.Parse(file.Uri)!;
            // Ссылки SAF удаляются через DocumentsContract, ссылки MediaStore — через ContentResolver
            return DocumentsContract.IsDocumentUri(Platform.AppContext, uri)
                ? DocumentsContract.DeleteDocument(Resolver, uri)
                : Resolver.Delete(uri, null, null) > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// «Открыть»: запускает приложение-читалку, которое Android считает подходящим для этого типа файла.
    /// GrantReadUriPermission даёт читалке временный доступ именно к этому файлу.
    /// </summary>
    public bool Open(SavedFile file)
    {
        var uri = AUri.Parse(file.Uri)!;
        // Читалки регистрируют fb2 под разными типами — пробуем от точного к общему
        foreach (var mime in new[] { file.Mime ?? MimeTypes.ForIntent(file.Name), "application/octet-stream", "*/*" }.Distinct())
        {
            var intent = new Intent(Intent.ActionView);
            intent.SetDataAndType(uri, mime);
            intent.AddFlags(ActivityFlags.GrantReadUriPermission);
            if (Context is not Android.App.Activity)
            {
                intent.AddFlags(ActivityFlags.NewTask);
            }

            try
            {
                Context.StartActivity(intent);
                return true;
            }
            catch (ActivityNotFoundException)
            {
                // следующий тип
            }
        }

        return false;
    }

    /// <summary>
    /// «Поделиться» — системное меню отправки: Telegram, почта, Send to Kindle, облако читалки и т.д.
    /// Один файл — ACTION_SEND, несколько — ACTION_SEND_MULTIPLE.
    /// </summary>
    public void Share(IReadOnlyCollection<SavedFile> files)
    {
        if (files.Count == 0)
        {
            return;
        }

        var uris = files.Select(f => AUri.Parse(f.Uri)!).ToList();
        var mimes = files.Select(f => f.Mime ?? MimeTypes.ForIntent(f.Name)).Distinct().ToList();

        Intent intent;
        if (uris.Count == 1)
        {
            intent = new Intent(Intent.ActionSend);
            intent.PutExtra(Intent.ExtraStream, uris[0]);
        }
        else
        {
            intent = new Intent(Intent.ActionSendMultiple);
            intent.PutParcelableArrayListExtra(Intent.ExtraStream, uris.Cast<Android.OS.IParcelable>().ToList());
        }

        intent.SetType(mimes.Count == 1 ? mimes[0] : "*/*");
        intent.PutExtra(Intent.ExtraSubject, files.First().BookTitle ?? files.First().Name);

        // Права на чтение выдаются получателю через ClipData — без неё некоторые приложения (особенно при
        // нескольких файлах) получают ссылки, но не могут их открыть
        var clip = ClipData.NewRawUri(files.First().Name, uris[0])!;
        foreach (var uri in uris.Skip(1))
        {
            clip.AddItem(new ClipData.Item(uri));
        }

        intent.ClipData = clip;
        intent.AddFlags(ActivityFlags.GrantReadUriPermission);

        var chooser = Intent.CreateChooser(intent, "Отправить книгу")!;
        if (Context is not Android.App.Activity)
        {
            chooser.AddFlags(ActivityFlags.NewTask);
        }

        Context.StartActivity(chooser);
    }

    /// <summary>
    /// Запись в «Загрузки/Elib2Ebook[/подпапка]» через MediaStore.
    /// IsPending=1 прячет файл от других приложений, пока он не дописан; после копирования снимаем флаг.
    /// </summary>
    private static AUri SaveToDownloads(string folder, string name, string sourcePath)
    {
        var relativePath = string.Join('/', new[] { AEnvironment.DirectoryDownloads, DefaultFolder, folder }.Where(s => !string.IsNullOrEmpty(s))) + "/";

        var values = new ContentValues();
        values.Put(MediaStore.IMediaColumns.DisplayName, name);
        values.Put(MediaStore.IMediaColumns.MimeType, MimeTypes.ForStorage(name));
        values.Put(MediaStore.IMediaColumns.RelativePath, relativePath);
        values.Put(MediaStore.IMediaColumns.IsPending, 1);

        var uri = Resolver.Insert(MediaStore.Downloads.ExternalContentUri!, values)
                  ?? throw new IOException($"Не удалось создать файл {name} в папке Загрузки");
        try
        {
            CopyTo(uri, sourcePath);

            values.Clear();
            values.Put(MediaStore.IMediaColumns.IsPending, 0);
            Resolver.Update(uri, values, null, null);
            return uri;
        }
        catch
        {
            // Не оставляем в «Загрузках» недописанный пустой файл
            Resolver.Delete(uri, null, null);
            throw;
        }
    }

    /// <summary>Запись в выбранную пользователем папку (SAF): подпапки создаются по пути, затем сам файл.</summary>
    private static AUri SaveToTree(AUri tree, string folder, string name, string sourcePath)
    {
        var parent = DocumentsContract.BuildDocumentUriUsingTree(tree, DocumentsContract.GetTreeDocumentId(tree))!;
        foreach (var segment in folder.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            parent = FindOrCreateDirectory(tree, parent, segment);
        }

        var uri = DocumentsContract.CreateDocument(Resolver, parent, MimeTypes.ForStorage(name), name)
                  ?? throw new IOException($"Не удалось создать файл {name} в папке {folder}");
        try
        {
            CopyTo(uri, sourcePath);
            return uri;
        }
        catch
        {
            DocumentsContract.DeleteDocument(Resolver, uri);
            throw;
        }
    }

    /// <summary>
    /// В SAF нет «создать, если нет» — перебираем детей папки и ищем подпапку с нужным именем,
    /// иначе создаём (повторное CreateDocument создало бы «Имя (1)»).
    /// </summary>
    private static AUri FindOrCreateDirectory(AUri tree, AUri parent, string name)
    {
        var children = DocumentsContract.BuildChildDocumentsUriUsingTree(tree, DocumentsContract.GetDocumentId(parent))!;
        using (var cursor = Resolver.Query(children,
                   [DocumentsContract.Document.ColumnDocumentId, DocumentsContract.Document.ColumnDisplayName, DocumentsContract.Document.ColumnMimeType],
                   null, null, null))
        {
            while (cursor != null && cursor.MoveToNext())
            {
                if (cursor.GetString(1) == name && cursor.GetString(2) == DocumentsContract.Document.MimeTypeDir)
                {
                    return DocumentsContract.BuildDocumentUriUsingTree(tree, cursor.GetString(0))!;
                }
            }
        }

        return DocumentsContract.CreateDocument(Resolver, parent, DocumentsContract.Document.MimeTypeDir, name)
               ?? throw new IOException($"Не удалось создать папку {name}");
    }

    private static void CopyTo(AUri uri, string sourcePath)
    {
        using var output = Resolver.OpenOutputStream(uri, "w") ?? throw new IOException("Нет доступа на запись");
        using var input = File.OpenRead(sourcePath);
        input.CopyTo(output);
    }

    /// <summary>Выбрана ли своя папка и есть ли к ней ещё доступ (иначе тихо возвращаемся к «Загрузкам»).</summary>
    private bool HasTree()
    {
        if (string.IsNullOrEmpty(settings.SaveFolderUri))
        {
            return false;
        }

        // Если доступ к папке отозван (например, её удалили), откатываемся на «Загрузки»
        var uri = settings.SaveFolderUri;
        var granted = Resolver.PersistedUriPermissions.Any(p => p.Uri?.ToString() == uri && p.IsWritePermission);
        if (!granted)
        {
            settings.SaveFolderUri = string.Empty;
            settings.SaveFolderName = string.Empty;
        }

        return granted;
    }

    private void ReleaseTree(AUri except)
    {
        foreach (var permission in Resolver.PersistedUriPermissions)
        {
            if (permission.Uri != null && !permission.Uri.Equals(except))
            {
                try
                {
                    Resolver.ReleasePersistableUriPermission(permission.Uri, TreeFlags);
                }
                catch
                {
                    // уже отозвано
                }
            }
        }
    }

    private static string QueryDisplayName(AUri uri)
    {
        try
        {
            using var cursor = Resolver.Query(uri, [IOpenableColumns.DisplayName], null, null, null);
            return cursor != null && cursor.MoveToFirst() ? cursor.GetString(0) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Человекочитаемое имя выбранной папки для подписи «Сохраняется в: …».</summary>
    private static string QueryTreeName(AUri tree)
    {
        var document = DocumentsContract.BuildDocumentUriUsingTree(tree, DocumentsContract.GetTreeDocumentId(tree));
        var name = document == null ? null : QueryDisplayName(document);
        if (!string.IsNullOrEmpty(name))
        {
            return name;
        }

        // primary:Books/Fantasy → Books/Fantasy
        var id = DocumentsContract.GetTreeDocumentId(tree) ?? tree.ToString();
        return id.Contains(':') ? id[(id.IndexOf(':') + 1)..] : id;
    }

    /// <summary>Подпапка файла относительно корня рабочей папки, через «/» («» — файл в корне).</summary>
    private static string GetFolder(string root, string path)
    {
        var folder = Path.GetRelativePath(root, Path.GetDirectoryName(path)!);
        return folder == "." ? string.Empty : folder.Replace('\\', '/');
    }
}
