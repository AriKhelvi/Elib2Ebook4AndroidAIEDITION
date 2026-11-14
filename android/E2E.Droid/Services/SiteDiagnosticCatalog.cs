namespace E2E.Droid.Services;

/// <summary>Проверяется доступная часть примера, а не полнота всего сайта. Пропуск не считается успехом.</summary>
public sealed record SiteDiagnosticCase(
    string Id, string SiteHost, string BookUrl, string Limitation, string SkipReason, bool Images = false, int StartChapter = 1)
{
    public bool CanRun => string.IsNullOrWhiteSpace(SkipReason)
        && Uri.TryCreate(BookUrl, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http";
}

/// <summary>
/// Версионированные реальные примеры. Источник — docs/CONTENT-AUDIT-2026-11-12.md
/// (матрица и дополнение 13 ноября), а также сообщения пользователей 13–14 ноября.
/// Наличие ссылки не обещает текущий доступ: сеть, защита, платные главы и удаление книги проверяются при запуске.
/// Все неизвестные и выключенные сайты получают отдельную строку пропуска с причиной.
/// </summary>
public static class SiteDiagnosticCatalog
{
    public const string Version = "2026-11-14.2";
    private const string NoExample = "Подтверждённый адрес отдельной книги пока не найден; проверка скачивания пропущена.";
    private const string WholeBook = "Загрузчик может получить файл или текст всей книги до выделения первой главы; действует общий таймаут случая.";
    private const string TokenRequired = "Загрузчик требует личный token ещё до получения книги; этот сценарий запускается без входа.";
    private const string ImagesOnly = "Комикс: проверяются изображения в CBZ, непустой текст не требуется.";

    /// <summary>Все известные примеры и пробелы каталога, включая сайты, отключённые в конкретном APK.</summary>
    public static IReadOnlyList<SiteDiagnosticCase> Samples { get; } = Array.AsReadOnly(new[]
    {
        Sample("acomics.ru", "https://acomics.ru/~slightly-damned/1", ImagesOnly + " Возможна региональная заглушка.", images: true),
        Sample("author.today", "https://author.today/work/673792", "Доступ к главам может требовать входа или подтверждения возраста."),
        Sample("bookinbook.ru", "https://bookinbook.ru/book?id=196", "Историческая ссылка; текущая доступность не подтверждена."),
        Sample("bookriver.ru", "https://bookriver.ru/book/innamatveeva-otlichnitsa-i-plokhish"),
        Missing("bookinist.pw"),
        Sample("books.yandex.ru", "https://books.yandex.ru/books/Xk1d5Skg", WholeBook, TokenRequired, suffix: "books"),
        Sample("books.yandex.ru", "https://books.yandex.ru/comicbooks/oF30TijM", ImagesOnly, TokenRequired, images: true, suffix: "comicbooks"),
        Missing("bookstab.ru", "При поиске 12 ноября вместо книжного каталога отображалась сторонняя страница."),
        Sample("bookstime.ru", "https://bookstime.ru/book-card/2551", skip: "14 ноября пример вернул HTTP 404; главная страница содержит материалы о школьной программе вместо прежнего книжного каталога."),
        Sample("bookuruk.com", "https://bookuruk.com/book/tihona-dla-mazora-2"),
        Sample("boosty.to", "https://boosty.to/glvrdru", "Проверяется открытый блог. Загрузчик может запросить несколько постов; действует общий таймаут случая."),
        Sample("boovell.ru", "https://boovell.ru/story/e-e-a-ya-gde/"),
        Missing("dark-novels.ru"),
        Sample("desu.me", "https://desu.uno/manga/one-piece.12/", ImagesOnly, "Найденный пример находится на другом домене desu.uno; текущий загрузчик его не распознаёт.", images: true),
        Sample("dreame.com", "https://www.dreame.com/story/2647351552-the-mc-president-s-forsaken-wife"),
        Sample("fanficus.com", "https://fanficus.com/post/6772443a9fb7520015fc4c18"),
        Missing("fb2.top", "При поиске 12 ноября каталог не ответил; адреса из синтетических тестов не используются."),
        Sample("ladylib.top", "https://ladylib.top/ty-ne-skazala-o-syne-877318"),
        Sample("ficbook.net", "https://ficbook.net/readfic/019ca9ba-8b70-77ce-80bf-6f88083d9b1a", "Сайт может потребовать вход, подтверждение возраста или проверку браузера."),
        Sample("fictionbook.ru", "https://fictionbook.ru/author/dmitriy-mamin-sibiryak/bratya_gordeeviy/", WholeBook),
        Missing("freedlit.space", "При поиске 12 ноября домен показывал страницу продажи, книжный каталог не найден."),
        Sample("bookhamster.ru", "https://bookhamster.ru/ranobe/pozhalujsta-pozvol-mne-lyubit-tebya-vechno/"),
        Sample("ifreedom.su", "https://ifreedom.su/ranobe/master-snova-ulybaetsya/"),
        Sample("hogwartsnet.ru", "https://hogwartsnet.ru/mfanf/ffshowfic.php?fid=89034"),
        Missing("eznovels.com"),
        Missing("hotnovelpub.com", "При поиске 12 ноября главная возвращала Redirecting без книжных ссылок."),
        Missing("lanovels.com"),
        Missing("lightnoveldaily.com"),
        Sample("hub-book.com", "https://hub-book.com/books/177062-anton-chehov-tatyana-repina/toread", WholeBook),
        Missing("i-gram.ru"),
        Sample("jaomix.ru", "https://jaomix.ru/ya-popal-v-neizvestnuyu-mne-mangu/"),
        Sample("hentailib.me", "https://hentailib.me/ru/33052--adabana", ImagesOnly + " Возможны ограничения доступа или возраста.", images: true),
        Sample("mangalib.me", "https://mangalib.me/ru/18406--nan-hao-shang-feng", ImagesOnly, images: true),
        Sample("mangalib.org", "https://mangalib.org/ru/18406--nan-hao-shang-feng", ImagesOnly, images: true),
        Sample("ranobelib.me", "https://ranobelib.me/ru/29609--mikkakan-no-koufuku"),
        Sample("v2.slashlib.me", "https://v2.slashlib.me/ru/23435--secret-alliance", ImagesOnly + " Возможны ограничения доступа или возраста.", images: true),
        Sample("libbox.ru", "https://libbox.ru/book/starik-i-more", WholeBook),
        Sample("libking.ru", "https://libking.ru/books/prose-/prose-rus-classic/10564-anton-chehov-hameleon.html", WholeBook),
        Sample("librebook.me", "https://1.librebook.me/the_metamorphosis", "Исторический пример на поддерживаемом загрузчиком поддомене."),
        Missing("libst.ru"),
        Sample("litlife.club", "https://litlife.club/books/449259", WholeBook),
        Sample("litgorod.ru", "https://litgorod.ru/books/view/75326"),
        Sample("litmarket.ru", "https://litmarket.ru/books/devochki-iz-proshloy-zhizni-1", WholeBook),
        Missing("litmir.me"),
        Sample("booknet.com", "https://booknet.com/es/book/intercambio-de-corazones-b459792"),
        Sample("booknet.ua", "https://booknet.ua/book/pokinuta-druzhina-b453844"),
        // Свежие примеры из жалоб: оглавление Bllate 100 + 65, цикл на карточке Litnet.
        Sample("litnet.com", "https://litnet.com/ru/book/v-obyatiyah-krasnoi-planety-b640815", "Пример из жалобы на зависание; сайт может ограничить гостевой доступ."),
        Sample("litres.ru", "https://www.litres.ru/book/aleksandr-pushkin/kapitanskaya-dochka-72130255/", WholeBook + " Без входа возможен только отрывок."),
        Sample("litsovet.ru", "https://litsovet.ru/books/1002083-vsyakoe-sluchaetsya", "Карточка и текст первой главы проверены 14 ноября; прежний пример вернул HTTP 404."),
        Missing("mangamammy.ru", "При поиске 12 ноября главная возвращала Redirecting без книжных ссылок.", images: true),
        Missing("mir-knig.com"),
        Sample("mybook.ru", "https://mybook.ru/author/aleksandr-sergeevich-pushkin/kapitanskaya-dochka/", WholeBook + " Возможен отрывок или требование входа."),
        Sample("neobook.org", "https://neobook.org/book/tSkACu7ymyK1azYk/", "Реальный пример из аудита; нынешняя доступность не подтверждена. Закрытые главы требуют входа."),
        Missing("noveltranslate.com", "При поиске 12 ноября главная не содержала ссылок на книги."),
        Missing("novelhall.com"),
        Missing("novelxo.com"),
        Missing("ru.novelxo.com"),
        Missing("online-knigi.com.ua"),
        Sample("prodaman.ru", "https://prodaman.ru/Anastasia-Klimova/books/Vsego-lish-18", WholeBook),
        Missing("ранобэ.рф"),
        Sample("ranobehub.org", "https://ranobehub.org/ranobe/947-alya-san-who-sits-besides-me-and-sometimes-murmurs-aff", "Первая часть состоит из иллюстраций; проверяется следующая текстовая глава через публичный читательский API.", startChapter: 2),
        Sample("manga.ovh", "https://v1.manga.ovh/content/v-temnote", ImagesOnly, "Найденный пример находится на поддомене v1.manga.ovh; текущий загрузчик его не распознаёт.", images: true),
        Missing("novel.ovh"),
        Sample("ranobes.com", "https://ranobes.com/ranobe/177387-mother-of-learning.html"),
        Missing("readmanga.live", images: true),
        Sample("readli.net", "https://readli.net/svyazannyie-nepriyatnosti-2/", WholeBook),
        Sample("remanga.org", "https://remanga.org/manga/rma-new-student-at-the-academy-is-a-hidden-disciple-of-the-legendary-school/main", ImagesOnly, images: true),
        Sample("renovels.org", "https://renovels.org/novel/novel-fffclass-trashero/main"),
        Missing("romfant.ru"),
        Sample("royalroad.com", "https://www.royalroad.com/fiction/190085/save-the-heroine"),
        Sample("bllate.org", "https://bllate.org/book/20455", "Оглавление из 165 глав; загрузка первой главы не подтверждает доступ к платным главам."),
        Sample("erolate.com", "https://erolate.com/book/3570", "Возможны ограничения доступа или возраста."),
        Missing("mlate.ru"),
        Sample("tl.rulate.ru", "https://tl.rulate.ru/book/273770", "Открытая глава проверена 14 ноября; прежний пример 209502 вернул HTTP 410. Первая глава не заменяет проверку пагинации после 100-й."),
        Sample("samlib.ru", "http://samlib.ru/o/obushnaja_j_a/inomirnoeputeshestviezemlaynki.shtml", "Страница произведения приходит целиком по HTTP."),
        Sample("stroki.mts.ru", "https://stroki.mts.ru/book/yevgeniy-onegin-387075", WholeBook, TokenRequired),
        Missing("bigliba.com"),
        Sample("topliba.com", "https://topliba.com/books/1052385"),
        Sample("twilightrussia.ru", "https://twilightrussia.ru/forum/37-38773-1"),
        Sample("wattpad.com", "https://www.wattpad.com/story/1001445-the-bro-code", "Первая часть — авторское объявление; она не доказывает доступ ко всем главам."),
        Sample("watt-pad.ru", "https://watt-pad.ru/story/355566", WholeBook),
        Sample("wuxiaworld.ru", "https://wuxiaworld.ru/puteshestvie-k-bessmertiyu-goblinteam/goblinteam-puteshestvie-k-bessmertiyu-glava-521/"),
        Missing("younettranslate.com", "При поиске 12 ноября главная возвращала Redirecting без подтверждённых страниц книг.")
    });

    public static IReadOnlyList<SiteDiagnosticCase> Entries => ForSites(SupportedSites.Sites);

    /// <summary>Одна строка на сайт, у Яндекс Книг две: обычная книга и отдельный загрузчик комиксов.</summary>
    public static IReadOnlyList<SiteDiagnosticCase> ForSites(IEnumerable<SiteEntry> sites)
    {
        ArgumentNullException.ThrowIfNull(sites);
        var examples = Samples.ToLookup(sample => sample.SiteHost, StringComparer.Ordinal);
        var result = new List<SiteDiagnosticCase>();
        foreach (var group in sites.GroupBy(site => Host(site.Url), StringComparer.Ordinal))
        {
            var site = group.First();
            var reason = !string.IsNullOrWhiteSpace(site.DisabledReason)
                ? "Сайт отключён в этой сборке: " + site.DisabledReason
                : !site.HasGetter ? "Загрузчик сайта не включён в эту сборку." : null;
            var cases = examples[group.Key].ToArray();
            if (cases.Length == 0) cases = [Missing(group.Key)];
            result.AddRange(cases.Select(sample => reason == null ? sample : sample with { SkipReason = reason }));
        }
        return result.AsReadOnly();
    }

    private static SiteDiagnosticCase Sample(string host, string url, string limitation = null,
        string skip = null, bool images = false, string suffix = null, int startChapter = 1)
    {
        host = Host("https://" + host);
        return new(host + (suffix == null ? "" : "/" + suffix), host, url, limitation, skip, images, startChapter);
    }

    private static SiteDiagnosticCase Missing(string host, string limitation = null, bool images = false)
        => Sample(host, null, limitation, string.IsNullOrWhiteSpace(limitation) ? NoExample : NoExample + " " + limitation, images);

    private static string Host(string url)
    {
        var host = new Uri(url).IdnHost.ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }
}
