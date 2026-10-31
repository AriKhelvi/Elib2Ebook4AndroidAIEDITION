// ============================================================================
// E2E для Android — сохранение книг в «Загрузки» (MediaStore) или выбранную папку (SAF), открыть/поделиться/удалить
// ----------------------------------------------------------------------------
// [AI-EDITION] Сгенерировано ИИ (Claude, Anthropic) под присмотром человека. Стабильность не гарантируется — см. android/README.md.
// ============================================================================

using System.Runtime.Versioning;
using Android.Content;
using Android.Media;
using Android.Provider;
using E2E.Droid.Services;
using AUri = Android.Net.Uri;
using AEnvironment = Android.OS.Environment;

namespace E2E.Droid;

/// <summary>
/// Сохранение в «Загрузки/Elib2Ebook» (Android 10+ — через MediaStore без разрешений, Android 7–9 — по пути
/// с разрешением на запись) или в папку, выбранную через системный диалог (Storage Access Framework).
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
/// На Android 7–9 коллекции Downloads в MediaStore ещё нет — там файл копируется в /sdcard/Download/Elib2Ebook
/// по пути (разрешение WRITE_EXTERNAL_STORAGE спрашивается перед первой загрузкой, см. EnsureWriteAccessAsync).
/// На некоторых читалках системный выбор папки вырезан — тогда остаются «Загрузки» (см. PickFolderAsync).
/// <para>Файлы адресуются ссылками (SavedFile.Uri): content:// для MediaStore и SAF, file:// для Android 7–9.
/// Наружу (в читалку, «Поделиться») file:// отдавать нельзя — такие ссылки заворачиваются в content://
/// через FileProvider (см. ShareableUri).</para>
/// </remarks>
public sealed class AndroidBookStorage(AppSettings settings) : IBookStorage
{
    /// <summary>Подпапка в «Загрузках» по умолчанию.</summary>
    private const string DefaultFolder = "Elib2Ebook";

    /// <summary>Адрес FileProvider из AndroidManifest.xml (пути — Resources/xml/e2e_file_paths.xml).</summary>
    private const string FileProviderAuthority = "e2e.droid.files";

    /// <summary>Права, которые берём на выбранную папку (и отпускаем при смене папки).</summary>
    private const ActivityFlags TreeFlags = ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission;

    /// <summary>Android 10+: «Загрузки» через MediaStore. Иначе (Android 7–9) — по пути.</summary>
    [SupportedOSPlatformGuard("android29.0")]
    private static bool UseMediaStore => OperatingSystem.IsAndroidVersionAtLeast(29);

    /// <summary>Для запуска чужих экранов лучше текущая Activity; если её нет — контекст приложения + NewTask.</summary>
    private static Context Context => Platform.CurrentActivity ?? Platform.AppContext;

    private static ContentResolver Resolver => Platform.AppContext.ContentResolver!;

    public string DestinationLabel
        => HasTree() ? settings.SaveFolderName : $"Загрузки/{DefaultFolder}";

    /// <summary>
    /// Переносит всё, что ядро собрало в рабочей папке задания (книги, обложку, подпапки с доп. файлами),
    /// в папку сохранения, сохраняя структуру подпапок. Работает в фоновом потоке.
    /// </summary>
    public Task<IReadOnlyList<SavedFile>> SaveTreeAsync(string sourceDir, Func<SavedFile, SavedFile> decorate,
        CancellationToken cancellationToken = default, IProgress<StorageProgress> progress = null)
    {
        return Task.Run<IReadOnlyList<SavedFile>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tree = HasTree() ? AUri.Parse(settings.SaveFolderUri) : null;
            var result = new List<SavedFile>();
            // Кэш принадлежит одному сохранению: не устаревает при смене папки или разрешений.
            var directories = new Dictionary<string, AUri>(StringComparer.Ordinal);
            long copiedBytes = 0;
            long totalBytes = 0;
            var completedFiles = 0;

            // Сначала файлы книг из корня, затем доп. файлы по подпапкам
            var files = Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories)
                .Select(path =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return (path, folder: GetFolder(sourceDir, path));
                })
                .OrderBy(f => f.folder.Length)
                .ThenBy(f => f.path, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                totalBytes += new FileInfo(file.path).Length;
            }
            void Report(long bytes)
            {
                copiedBytes += bytes;
                progress?.Report(new StorageProgress(completedFiles, files.Count, copiedBytes, totalBytes));
            }
            Report(0);

            // URI храним отдельно от результата: декоратор и чтение метаданных тоже могут упасть.
            var created = new List<AUri>();
            try
            {
                foreach (var (path, folder) in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(path);
                    var uri = tree != null
                        ? SaveToTree(tree, folder, name, path, directories, cancellationToken, Report)
                        : UseMediaStore
                            ? SaveToDownloads(folder, name, path, cancellationToken, Report)
                            : SaveToLegacyDownloads(folder, name, path, cancellationToken, Report);
                    created.Add(uri);

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
                    completedFiles++;
                    Report(0);
                }

                cancellationToken.ThrowIfCancellationRequested();
                return result;
            }
            catch
            {
                // Текущий недописанный файл удаляет SaveTo*. Здесь откатываем уже готовые файлы
                // этого вызова; существующие файлы и папки не затрагиваем.
                foreach (var uri in created)
                {
                    Delete(new SavedFile { Uri = uri.ToString() });
                }
                throw;
            }
        });
    }

    /// <summary>
    /// Android 7–9 пишут в «Загрузки» по пути — для этого нужно разрешение на запись в память.
    /// Android 10+ (MediaStore) и выбранная пользователем папка (SAF) обходятся без него.
    /// </summary>
    public async Task<bool> EnsureWriteAccessAsync()
    {
        if (UseMediaStore || HasTree())
        {
            return true;
        }

        return await MainThread.InvokeOnMainThreadAsync(async () =>
            await Permissions.CheckStatusAsync<Permissions.StorageWrite>() == PermissionStatus.Granted
            || await Permissions.RequestAsync<Permissions.StorageWrite>() == PermissionStatus.Granted);
    }

    /// <summary>Системный диалог «Выберите папку» (SAF). Доступ к папке сохраняется и после перезапуска.</summary>
    /// <exception cref="InvalidOperationException">
    /// На устройстве нет системного выбора папки — так бывает на читалках с урезанным Android.
    /// </exception>
    public async Task<bool> PickFolderAsync()
    {
        var intent = new Intent(Intent.ActionOpenDocumentTree);
        intent.AddFlags(TreeFlags | ActivityFlags.GrantPersistableUriPermission | ActivityFlags.GrantPrefixUriPermission);

        Intent data;
        try
        {
            data = await ActivityResults.StartAsync(intent);
        }
        catch (ActivityNotFoundException)
        {
            throw new InvalidOperationException(
                $"На этом устройстве нет системного выбора папки. Книги сохраняются в «Загрузки/{DefaultFolder}» — " +
                "их видно в файловом менеджере и по USB.");
        }
        var uri = data?.Data;
        if (uri == null)
        {
            return false; // пользователь нажал «Назад»
        }

        // Провайдер может вернуть меньше прав, чем мы запросили. Не берём права, которых он не выдал.
        var grantedFlags = data.Flags & TreeFlags;
        if ((grantedFlags & ActivityFlags.GrantWriteUriPermission) == 0)
        {
            throw new InvalidOperationException("Выбранная папка не разрешает запись. Выберите другую папку");
        }

        // Без TakePersistableUriPermission доступ пропал бы после перезапуска приложения.
        // Старую папку сохраняем, пока постоянное разрешение на новую не получено.
        try
        {
            Resolver.TakePersistableUriPermission(uri, grantedFlags);
        }
        catch (Java.Lang.SecurityException)
        {
            throw new InvalidOperationException(
                "Не удалось сохранить доступ к выбранной папке. Выберите её повторно или укажите другую папку");
        }
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
            if (LocalPath(file) is { } path)
            {
                return File.Exists(path);
            }

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
            if (LocalPath(file) is { } path)
            {
                // Android 7–9: файл по пути; сканер уберёт его из индекса (иначе он ещё виден по USB)
                File.Delete(path);
                MediaScannerConnection.ScanFile(Platform.AppContext, [path], null, null);
                return true;
            }

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
        var uri = ShareableUri(file);
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

        var uris = files.Select(ShareableUri).ToList();
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
    /// Ссылка, которую можно отдать другому приложению. file:// (Android 7–9) Android запрещает передавать наружу
    /// (FileUriExposedException), поэтому такие ссылки заворачиваются в content:// через FileProvider.
    /// </summary>
    private static AUri ShareableUri(SavedFile file)
    {
        return LocalPath(file) is { } path
            ? AndroidX.Core.Content.FileProvider.GetUriForFile(Platform.AppContext, FileProviderAuthority, new Java.IO.File(path))!
            : AUri.Parse(file.Uri)!;
    }

    /// <summary>Путь к файлу, если он сохранён по пути (file://, Android 7–9), иначе null.</summary>
    private static string LocalPath(SavedFile file)
    {
        var uri = AUri.Parse(file.Uri);
        return uri?.Scheme == "file" ? uri.Path : null;
    }

    /// <summary>
    /// Android 7–9: копирование в /sdcard/Download/Elib2Ebook[/подпапка] по пути (нужно разрешение на запись,
    /// см. EnsureWriteAccessAsync). Имена при совпадении нумеруются так же, как это делает MediaStore.
    /// </summary>
    private static AUri SaveToLegacyDownloads(string folder, string name, string sourcePath, CancellationToken cancellationToken, Action<long> report)
    {
#pragma warning disable CA1422 // устарело в Android 10 — но это как раз ветка для Android 7–9
        var downloads = AEnvironment.GetExternalStoragePublicDirectory(AEnvironment.DirectoryDownloads)!.AbsolutePath;
#pragma warning restore CA1422
        var dir = Path.Combine([downloads, DefaultFolder, .. folder.Split('/', StringSplitOptions.RemoveEmptyEntries)]);
        Directory.CreateDirectory(dir);

        var target = UniquePath(dir, name);
        var created = false;
        try
        {
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
            {
                created = true;
                CopyTo(output, sourcePath, cancellationToken, report);
            }
            // Без сканирования файл не виден по USB (MTP) и в части файловых менеджеров до перезагрузки
            MediaScannerConnection.ScanFile(Platform.AppContext, [target], null, null);
            return AUri.FromFile(new Java.IO.File(target))!;
        }
        catch
        {
            try { if (created) File.Delete(target); } catch { /* Сохраняем исходную ошибку копирования. */ }
            throw;
        }
    }

    /// <summary>«Книга.epub» уже есть → «Книга (1).epub», «Книга (2).epub»…</summary>
    private static string UniquePath(string dir, string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        var path = Path.Combine(dir, name);
        for (var i = 1; File.Exists(path) || Directory.Exists(path); i++)
        {
            path = Path.Combine(dir, $"{stem} ({i}){extension}");
        }

        return path;
    }

    /// <summary>
    /// Android 10+: запись в «Загрузки/Elib2Ebook[/подпапка]» через MediaStore.
    /// IsPending=1 прячет файл от других приложений, пока он не дописан; после копирования снимаем флаг.
    /// </summary>
    [SupportedOSPlatform("android29.0")]
    private static AUri SaveToDownloads(string folder, string name, string sourcePath, CancellationToken cancellationToken, Action<long> report)
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
            CopyTo(uri, sourcePath, cancellationToken, report);

            values.Clear();
            values.Put(MediaStore.IMediaColumns.IsPending, 0);
            Resolver.Update(uri, values, null, null);
            return uri;
        }
        catch
        {
            // Не оставляем в «Загрузках» недописанный пустой файл
            try { Resolver.Delete(uri, null, null); } catch { /* Сохраняем исходную ошибку. */ }
            throw;
        }
    }

    /// <summary>Запись в выбранную пользователем папку (SAF): подпапки создаются по пути, затем сам файл.</summary>
    private static AUri SaveToTree(AUri tree, string folder, string name, string sourcePath, Dictionary<string, AUri> directories, CancellationToken cancellationToken, Action<long> report)
    {
        var parent = DocumentsContract.BuildDocumentUriUsingTree(tree, DocumentsContract.GetTreeDocumentId(tree))!;
        var key = string.Empty;
        foreach (var segment in folder.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            key += "/" + segment;
            if (!directories.TryGetValue(key, out var cached))
            {
                cached = FindOrCreateDirectory(tree, parent, segment, cancellationToken);
                directories.Add(key, cached);
            }
            parent = cached;
        }
        cancellationToken.ThrowIfCancellationRequested();

        var uri = DocumentsContract.CreateDocument(Resolver, parent, MimeTypes.ForStorage(name), name)
                  ?? throw new IOException($"Не удалось создать файл {name} в папке {folder}");
        try
        {
            CopyTo(uri, sourcePath, cancellationToken, report);
            return uri;
        }
        catch
        {
            try { DocumentsContract.DeleteDocument(Resolver, uri); } catch { /* Сохраняем исходную ошибку. */ }
            throw;
        }
    }

    /// <summary>
    /// В SAF нет «создать, если нет» — перебираем детей папки и ищем подпапку с нужным именем,
    /// иначе создаём (повторное CreateDocument создало бы «Имя (1)»).
    /// </summary>
    private static AUri FindOrCreateDirectory(AUri tree, AUri parent, string name, CancellationToken cancellationToken)
    {
        var children = DocumentsContract.BuildChildDocumentsUriUsingTree(tree, DocumentsContract.GetDocumentId(parent))!;
        using (var cursor = Resolver.Query(children,
                   [DocumentsContract.Document.ColumnDocumentId, DocumentsContract.Document.ColumnDisplayName, DocumentsContract.Document.ColumnMimeType],
                   null, null, null))
        {
            while (cursor != null && cursor.MoveToNext())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (cursor.GetString(1) == name && cursor.GetString(2) == DocumentsContract.Document.MimeTypeDir)
                {
                    return DocumentsContract.BuildDocumentUriUsingTree(tree, cursor.GetString(0))!;
                }
            }
        }

        return DocumentsContract.CreateDocument(Resolver, parent, DocumentsContract.Document.MimeTypeDir, name)
               ?? throw new IOException($"Не удалось создать папку {name}");
    }

    private static void CopyTo(AUri uri, string sourcePath, CancellationToken cancellationToken, Action<long> report)
    {
        using var output = Resolver.OpenOutputStream(uri, "w") ?? throw new IOException("Нет доступа на запись");
        CopyTo(output, sourcePath, cancellationToken, report);
    }

    private static void CopyTo(System.IO.Stream output, string sourcePath, CancellationToken cancellationToken, Action<long> report)
    {
        using var input = File.OpenRead(sourcePath);
        var buffer = new byte[81920];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = input.Read(buffer, 0, buffer.Length);
            if (count == 0) break;
            output.Write(buffer, 0, count);
            report(count);
        }
        output.Flush();
        cancellationToken.ThrowIfCancellationRequested();
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
        if (uri.Scheme == "file")
        {
            return Path.GetFileName(uri.Path);
        }

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
