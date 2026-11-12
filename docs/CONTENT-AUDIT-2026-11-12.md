# Проверка содержимого загрузчиков — 12 ноября 2026

Проверены исходники **95 классов** загрузчиков: **83 конкретных** и **12 общих абстрактных**. Для **57 конкретных загрузчиков** найден и запущен реальный пример; выполнена **61 попытка** с учётом дополнительных книг. Для остальных 26 индивидуальную книгу выбрать не удалось при описанном ниже состоянии доступа. Таймаут означает результат данного обращения, а не прекращение работы сайта.

## Точные результаты

| Единица учёта | Количество |
|---|---:|
| Конкретные загрузчики в инвентаре и матрице | 83 |
| Конкретные загрузчики с найденной книгой | 57 |
| Из них запущены настоящим getter | 57 |
| Текущие 57 пар getter + URL из итоговых manifests, для которых есть результат | 57 из 57 |
| Все попытки, включая дополнительные URL | 61 |
| Получен непустой текст | 17 попыток, 15 загрузчиков |
| Проверено непустых глав/частей текста | 32 |
| Изображения без текста при NoImage=true | 3 попытки, 3 загрузчика |
| Успешное исполнение, но возвращено 0 глав | 2 попытки: Jaomix и первый пример Remanga |
| Success=true в сыром отчёте, включая предыдущие две категории | 22 попытки |
| Завершились ошибкой/ограничением | 39 попыток, 38 загрузчиков |
| Индивидуальная книга не найдена, getter не запускался | 26 загрузчиков |

По лучшему доступному результату 83 загрузчика распределяются так: **15 с текстом + 3 изображения/N/A + 1 ноль глав/N/A + 38 с ошибкой + 26 без примера**. Remanga имеет две попытки: сначала ноль глав, затем две главы с изображениями. Это не два разных загрузчика. Wattpad.com и WattpadRu — два разных класса с одинаковым коротким именем.

39 неуспешных попыток: 19 таймаутов запроса по 18 секунд; 2 остановки по пределу запросов; 5 HTTP 403; 3 HTTP 404; 1 HTTP 503; 3 отказа без пользовательского token; 4 NullReferenceException; 1 ошибка пустого title; 1 некорректная ссылка с хостом `https`. Первый длинный пример Libking остановлен по пределу запросов, второй короткий — по таймауту: оба результата сохранены в матрице.

## Методика и границы вывода

Использованы реальные URL книг, постов и комиксов из открытых каталогов, API, поискового индекса и ранее найденных ссылок. Историческая ссылка BookInBook помечена отдельно. Прогон выполнял настоящий getter с `Start=1`, `End=2`, стандартной задержкой 0,6–1,5 с и `NoImage=true`. Отсутствие картинки в этом режиме не является дефектом комиксного загрузчика. Обложки могли загружаться; полноценная проверка изображений глав не выполнялась.

Предел одного HTTP-запроса — 18 секунд; общий предохранитель одного случая — 100 секунд и 45 запросов (отмена при 46-м). Некоторые загрузчики объединяют все страницы книги или игнорируют End: такие случаи ограничивались предохранителем. Предварительный поиск книг использовал другие пределы 15–25 секунд, указанные в строках без примера.

Пользовательские cookies, логины и токены не подставлялись. Платные главы не покупались; авторизация, CAPTCHA, TLS и ограничения доступа не обходились. Входной URL не гарантирует, что getter использует тот же домен: некоторые реализации приводят его к своему SystemUrl.

«Непустая глава» здесь означает полученный текст с `TextChars > 0`. Это не доказательство полноты книги, правильности перевода или отсутствия рекламного поста среди глав. Анализировались начальный структурный заголовок, его совпадение с названием главы, последний блок и ссылки в конце. При успешном текстовом результате создавались FB2 и EPUB и проверялась структура XML; **полная проверка по FB2 XSD не выполнялась**. Проверка первых двух глав не доказывает отсутствие дефекта дальше в книге или на другом произведении того же сайта.

На этих 32 непустых главах повтор начального названия и голая хвостовая самоссылка не обнаружены. В том числе проверены **три книги RanobeLib, всего пять глав**. Другие библиотечные источники в доступной выборке не подтвердили дополнительные хвостовые служебные ссылки. Авторская ссылка `https://w.tt/2F5tGdq` в первой части Wattpad.com «Purchase The Bro Code!» сохранена: это содержание автора, а не самоссылка скачиваемой главы.

## Полная матрица 83 конкретных загрузчиков

Число в колонке «Непустых глав» относится к лучшему выбранному результату строки; дополнительные книги и отказы перечислены в последней колонке. `0` при ошибке означает, что завершённого результата главы нет. `—` означает отсутствие запуска. Коды A1–A3, G1–G3, O1/O3 соответствуют партиям a-f, g-n, o-z (перечень файлов свидетельств ниже).

| № | Загрузчик | Сайт | Реальный URL выбранного примера | Непустых глав | Результат и дополнительные попытки |
|---:|---|---|---|---:|---|
| 1 | `Acomics.AcomicsGetter` | [acomics.ru](https://acomics.ru/) | [пример](https://acomics.ru/~slightly-damned/1) | 0 | NullReferenceException при разборе ответа; требуется диагностика разметки [A3]; при поиске главная возвращала региональную заглушку |
| 2 | `AuthorToday.AuthorTodayGetter` | [author.today](https://author.today/) | [пример](https://author.today/work/673792) | 0 | Таймаут запроса 18 с; содержимое не получено [A1] |
| 3 | `BookInBook.BookInBookGetter` | [bookinbook.ru](https://bookinbook.ru/) | [пример](https://bookinbook.ru/book?id=196) | 0 | Историческая ссылка; Таймаут запроса 18 с; содержимое не получено [A2] |
| 4 | `BookRiver.BookriverGetter` | [bookriver.ru](https://bookriver.ru/) | [пример](https://bookriver.ru/book/innamatveeva-otlichnitsa-i-plokhish) | 2 | Текст получен; FB2/EPUB созданы и структурно проверены [A1] |
| 5 | `Bookinist.BookinistGetter` | [bookinist.pw](https://bookinist.pw/) | Не найден | — | Не запускался: The request was canceled due to the configured HttpClient.Timeout of 25 seconds elapsing. Повторный обычный HTTP-запрос: The request was canceled due to the configured HttpClient.Timeout of 15 seconds elapsing. |
| 6 | `BooksYandex.BooksYandexBooksGetter` | [books.yandex.ru](https://books.yandex.ru/) | [пример](https://books.yandex.ru/books/Xk1d5Skg) | 0 | Нужен пользовательский token; вход не выполнялся [A1] |
| 7 | `BooksYandex.BooksYandexComicsGetter` | [books.yandex.ru](https://books.yandex.ru/) | [пример](https://books.yandex.ru/comicbooks/oF30TijM) | 0 | Нужен пользовательский token; вход не выполнялся [A2] |
| 8 | `Bookstab.BookstabGetter` | [bookstab.ru](https://bookstab.ru/) | Не найден | — | Не запускался: Локальный HTTP главной даёт таймаут; первичная страница https://bookstab.ru через веб-поиск показывает казино, книжный каталог отсутствует. |
| 9 | `Bookstime.BookstimeGetter` | [bookstime.ru](https://bookstime.ru/) | [пример](https://bookstime.ru/book-card/2551) | 0 | Таймаут запроса 18 с; содержимое не получено [A2] |
| 10 | `Bookuruk.BookurukGetter` | [bookuruk.com](https://bookuruk.com/) | [пример](https://bookuruk.com/book/tihona-dla-mazora-2) | 0 | NullReferenceException при разборе ответа; требуется диагностика разметки [A1] |
| 11 | `Boosty.BoostyGetter` | [boosty.to](https://boosty.to/) | [пример](https://boosty.to/glvrdru) | 0 | Таймаут запроса 18 с; содержимое не получено [A1] |
| 12 | `Boovel.BoovelGetter` | [boovell.ru](https://boovell.ru/) | [пример](https://boovell.ru/story/e-e-a-ya-gde/) | 2 | Текст получен; FB2/EPUB созданы и структурно проверены [A2] |
| 13 | `DarkNovels.DarkNovelsGetter` | [dark-novels.ru](https://dark-novels.ru/) | Не найден | — | Не запускался: Response status code does not indicate success: 503 (Service Unavailable). Повторный обычный HTTP-запрос: Response status code does not indicate success: 503 (Service Unavailable). |
| 14 | `Desu.DesuGetter` | [desu.me](https://desu.me/) | [пример](https://desu.uno/manga/one-piece.12/) | 0 | Таймаут запроса 18 с; содержимое не получено [A3] |
| 15 | `Dreame.DreameGetter` | [dreame.com](https://dreame.com/) | [пример](https://www.dreame.com/story/2647351552-the-mc-president-s-forsaken-wife) | 2 | Текст получен; FB2/EPUB созданы и структурно проверены [A1] |
| 16 | `FanFicus.FanFicusGetter` | [fanficus.com](https://fanficus.com/) | [пример](https://fanficus.com/post/6772443a9fb7520015fc4c18) | 0 | HTTP 503; содержимое не получено [A2] |
| 17 | `Fb2Top.Fb2TopGetter` | [fb2.top](https://fb2.top/) | Не найден | — | Не запускался: The request was canceled due to the configured HttpClient.Timeout of 25 seconds elapsing. Повторный обычный HTTP-запрос: The request was canceled due to the configured HttpClient.Timeout of 15 seconds elapsing. |
| 18 | `Fb2Top.LadyLibGetter` | [ladylib.top](https://ladylib.top/) | [пример](https://ladylib.top/ty-ne-skazala-o-syne-877318) | 0 | Таймаут запроса 18 с; содержимое не получено [A2] |
| 19 | `Ficbook.FicbookGetter` | [ficbook.net](https://ficbook.net/) | [пример](https://ficbook.net/readfic/019ca9ba-8b70-77ce-80bf-6f88083d9b1a) | 0 | HTTP 403; доступ к выбранному примеру не получен [A2] |
| 20 | `FictionBook.FictionBookGetter` | [fictionbook.ru](https://fictionbook.ru/) | [пример](https://fictionbook.ru/author/dmitriy-mamin-sibiryak/bratya_gordeeviy/) | 0 | NullReferenceException при разборе ответа; требуется диагностика разметки [A1] |
| 21 | `Freedlit.FreedlitGetter` | [freedlit.space](https://freedlit.space/) | Не найден | — | Не запускался: Свежий обычный HTTP200 содержит страницу «This domain is for sale», книжный каталог отсутствует. |
| 22 | `Freedom.BookHamsterGetter` | [bookhamster.ru](https://bookhamster.ru/) | [пример](https://bookhamster.ru/ranobe/pozhalujsta-pozvol-mne-lyubit-tebya-vechno/) | 2 | Текст получен; FB2/EPUB созданы и структурно проверены [A1] |
| 23 | `Freedom.FreedomGetter` | [ifreedom.su](https://ifreedom.su/) | [пример](https://ifreedom.su/ranobe/master-snova-ulybaetsya/) | 0 | Пустое название дошло до сборщика: ArgumentNullException(title) [A2] |
| 24 | `HogwartsNet.HogwartsNetGetter` | [hogwartsnet.ru](https://hogwartsnet.ru/) | [пример](https://hogwartsnet.ru/mfanf/ffshowfic.php?fid=89034) | 0 | Таймаут запроса 18 с; содержимое не получено [G2] |
| 25 | `HotNovelPub.EzNovelsGetter` | [eznovels.com](https://eznovels.com/) | Не найден | — | Не запускался: Публичная главная HTTP 200, title Loading..., без ссылок на каталог/книги. Поиск по домену не дал подтверждённых страниц книг. |
| 26 | `HotNovelPub.HotNovelPubGetter` | [hotnovelpub.com](https://hotnovelpub.com/) | Не найден | — | Не запускался: Публичная главная HTTP 200, title Redirecting..., только непрозрачная служебная ссылка. Поиск по домену не дал подтверждённых страниц книг. |
| 27 | `HotNovelPub.LaNovelsGetter` | [lanovels.com](https://lanovels.com/) | Не найден | — | Не запускался: Публичная главная: error. The request was canceled due to the configured HttpClient.Timeout of 16 seconds elapsing. Дополнительный поиск по точному домену не дал подтверждённых страниц книг. |
| 28 | `HotNovelPub.LightNovelDailyGetter` | [lightnoveldaily.com](https://lightnoveldaily.com/) | Не найден | — | Не запускался: Публичная главная: error. The request was canceled due to the configured HttpClient.Timeout of 16 seconds elapsing. Дополнительный поиск по точному домену не дал подтверждённых страниц книг. |
| 29 | `HubBook.HubBookGetter` | [hub-book.com](https://hub-book.com/) | [пример](https://hub-book.com/books/177062-anton-chehov-tatyana-repina/toread) | 0 | HTTP 403; доступ к выбранному примеру не получен [G3] |
| 30 | `IGram.IGramGetter` | [i-gram.ru](https://i-gram.ru/) | Не найден | — | Не запускался: Обычная главная: таймаут 16 секунд; web-fetch главной 502, каталога cache miss. Поисковый индекс показывает книги, но не выдаёт их индивидуальные URL; ID не выдумывались. |
| 31 | `Jaomix.JaomixGetter` | [jaomix.ru](https://jaomix.ru/) | [пример](https://jaomix.ru/ya-popal-v-neizvestnuyu-mne-mangu/) | 0 | N/A: getter вернул 0 глав; сборка файлов не доказывает получение книги [G3] |
| 32 | `LibSocial.HentaiLibGetter` | [hentailib.me](https://hentailib.me/) | [пример](https://hentailib.me/ru/33052--adabana) | 0 | HTTP 404 на книге/главе; содержимое не получено [G1] |
| 33 | `LibSocial.MangaLibGetter` | [mangalib.me](https://mangalib.me/) | [пример](https://mangalib.me/ru/29916--ag-yeog-uiending-eunjug-eumppun) | 0 | N/A: изображения; NoImage=true удалил img, текст не проверяется [G1] |
| 34 | `LibSocial.MangaLibOrgGetter` | [mangalib.org](https://mangalib.org/) | [пример](https://mangalib.org/ru/29916--ag-yeog-uiending-eunjug-eumppun) | 0 | N/A: изображения; NoImage=true удалил img, текст не проверяется [G1] |
| 35 | `LibSocial.RanobeLibGetter` | [ranobelib.me](https://ranobelib.me/) | [пример](https://ranobelib.me/ru/29609--mikkakan-no-koufuku) | 2 | Текст получен; FB2/EPUB созданы и структурно проверены [G3]; [другой пример](https://ranobelib.me/ru/26690--omniscient-readers-viewpoint-novel): 2 непустых глав, Текст получен; FB2/EPUB созданы и структурно проверены [G1]; [другой пример](https://ranobelib.me/ru/24263--about-the-reckless-girl-who-kept-challenging-a-reborn-man-like-me): 1 непустых глав, Текст получен; FB2/EPUB созданы и структурно проверены [G3] |
| 36 | `LibSocial.SlashLibGetter` | [v2.slashlib.me](https://v2.slashlib.me/) | [пример](https://v2.slashlib.me/ru/23435--secret-alliance) | 0 | HTTP 404 на книге/главе; содержимое не получено [G1] |
| 37 | `Libbox.LibboxGetter` | [libbox.ru](https://libbox.ru/) | [пример](https://libbox.ru/book/starik-i-more) | 0 | Таймаут запроса 18 с; содержимое не получено [G2] |
| 38 | `Libking.LibkingGetter` | [libking.ru](https://libking.ru/) | [пример](https://libking.ru/books/prose-/prose-rus-classic/10564-anton-chehov-hameleon.html) | 0 | Таймаут запроса 18 с; содержимое не получено [G2]; [другой пример](https://libking.ru/books/det-/det-action/1058249-aleksandr-bushkov-piranya-knigi-1-23.html): 0 непустых глав, Предел runner: 45 запросов, отмена на 46-м; результат не завершён [G1] |
| 39 | `Librebook.LibrebookGetter` | [librebook.me](https://librebook.me/) | [пример](https://1.librebook.me/the_metamorphosis) | 2 | Текст получен; FB2/EPUB созданы и структурно проверены [G2] |
| 40 | `Libst.LibstGetter` | [libst.ru](https://libst.ru/) | Не найден | — | Не запускался: Обычная главная: таймаут 16 секунд; web-fetch также таймаут. Поиск старого книжного маршрута не дал книг; индексируется спортивная статья на libst.ru/klubnyj-chempionat-mira-luchshie-komandy-planety-na-odnoj-arene/ — возможная смена назначения домена, не доказанная окончательно. |
| 41 | `LitLife.LitLifeGetter` | [litlife.club](https://litlife.club/) | [пример](https://litlife.club/books/449259) | 0 | HTTP 403; доступ к выбранному примеру не получен [G1] |
| 42 | `Litgorod.LitgorodGetter` | [litgorod.ru](https://litgorod.ru/) | [пример](https://litgorod.ru/books/view/75326) | 0 | Таймаут запроса 18 с; содержимое не получено [G2] |
| 43 | `Litmarket.LitmarketGetter` | [litmarket.ru](https://litmarket.ru/) | [пример](https://litmarket.ru/books/devochki-iz-proshloy-zhizni-1) | 2 | Текст получен; FB2/EPUB созданы и структурно проверены [G1] |
| 44 | `Litmir.LitmirGetter` | [litmir.me](https://litmir.me/) | Не найден | — | Не запускался: Обычная главная: таймаут 16 секунд; web-fetch URL недоступен; поиск site:litmir.me/bd не дал страниц книг. |
| 45 | `Litnet.BooknetComGetter` | [booknet.com](https://booknet.com/) | [пример](https://booknet.com/es/book/intercambio-de-corazones-b459792) | 2 | Текст получен; FB2/EPUB созданы и структурно проверены [G1] |
| 46 | `Litnet.BooknetUaGetter` | [booknet.ua](https://booknet.ua/) | [пример](https://booknet.ua/book/pokinuta-druzhina-b453844) | 2 | Текст получен; FB2/EPUB созданы и структурно проверены [G1] |
| 47 | `Litnet.LitnetGetter` | [litnet.com](https://litnet.com/) | [пример](https://litnet.com/ru/book/boss-ne-verit-v-lyubov-b622002) | 2 | Текст получен; FB2/EPUB созданы и структурно проверены [G1] |
| 48 | `Litres.LitresGetter` | [www.litres.ru](https://www.litres.ru/) | [пример](https://www.litres.ru/book/aleksandr-pushkin/kapitanskaya-dochka-72130255/) | 0 | Таймаут запроса 18 с; содержимое не получено [G2] |
| 49 | `Litsovet.LitsovetGetter` | [litsovet.ru](https://litsovet.ru/) | [пример](https://litsovet.ru/books/1002110-zhiv) | 0 | HTTP 404 на книге/главе; содержимое не получено [G3] |
| 50 | `MangaMammy.MangaMammyGetter` | [mangamammy.ru](https://mangamammy.ru/) | Не найден | — | Не запускался: Публичная главная HTTP 200, title Redirecting..., только непрозрачная служебная ссылка. Поиск site:mangamammy.ru/manga/ не дал страниц книг. |
| 51 | `MirKnig.MirKnigGetter` | [mir-knig.com](https://mir-knig.com/) | Не найден | — | Не запускался: Публичная главная HTTP 200, title Redirecting..., только непрозрачная служебная ссылка. Поиск site:mir-knig.com/view_ не дал страниц книг. |
| 52 | `MyBook.MyBookGetter` | [mybook.ru](https://mybook.ru/) | [пример](https://mybook.ru/author/aleksandr-sergeevich-pushkin/kapitanskaya-dochka/) | 0 | Таймаут запроса 18 с; содержимое не получено [G2] |
| 53 | `Neobook.NeobookGetter` | [neobook.org](http://neobook.org/) | [пример](https://neobook.org/book/tSkACu7ymyK1azYk/) | 0 | Таймаут запроса 18 с; содержимое не получено [G3] |
| 54 | `NovelTranslate.NovelTranslateGetter` | [noveltranslate.com](https://noveltranslate.com/) | Не найден | — | Не запускался: Публичная главная HTTP 200, пустой title, нет ссылок. Поиск по домену не дал подтверждённых страниц книг. |
| 55 | `Novelhall.NovelhallGetter` | [www.novelhall.com](https://www.novelhall.com/) | Не найден | — | Не запускался: Обычная главная и web-fetch: HTTP 403 Forbidden; поиск по домену не дал подтверждённой страницы книги. |
| 56 | `Novelxo.NovelxoGetter` | [novelxo.com](https://novelxo.com/) | Не найден | — | Не запускался: Публичная главная: error. The request was canceled due to the configured HttpClient.Timeout of 16 seconds elapsing. Дополнительный поиск по точному домену не дал подтверждённых страниц книг. |
| 57 | `Novelxo.NovelxoRuGetter` | [ru.novelxo.com](https://ru.novelxo.com/) | Не найден | — | Не запускался: Публичная главная: error. The request was canceled due to the configured HttpClient.Timeout of 16 seconds elapsing. Дополнительный поиск по точному домену не дал подтверждённых страниц книг. |
| 58 | `OnlineKnigi.OnlineKnigiGetter` | [online-knigi.com.ua](https://online-knigi.com.ua/) | Не найден | — | Не запускался: Главная проверена обычным HTTP: 403.  403 Forbidden - Nginx |
| 59 | `Prodaman.ProdamanGetter` | [prodaman.ru](https://prodaman.ru/) | [пример](https://prodaman.ru/Anastasia-Klimova/books/Vsego-lish-18) | 0 | Таймаут запроса 18 с; содержимое не получено [O1] |
| 60 | `Ranobe.RanobeGetter` | [ранобэ.рф](https://ранобэ.рф/) | Не найден | — | Не запускался: Главная проверена обычным HTTP: error. The request was canceled due to the configured HttpClient.Timeout of 15 seconds elapsing.  |
| 61 | `RanobeHub.RanobeHubGetter` | [ranobehub.org](https://ranobehub.org/) | [пример](https://ranobehub.org/ranobe/947-alya-san-who-sits-besides-me-and-sometimes-murmurs-aff) | 0 | NullReferenceException при разборе ответа; требуется диагностика разметки [O1]; HTTP 200 с NextFlight, отсутствуют ожидаемые html[data-id]/h1.header |
| 62 | `RanobeOvh.MangaOvhGetter` | [manga.ovh](https://manga.ovh/) | [пример](https://v1.manga.ovh/content/v-temnote) | 0 | Таймаут запроса 18 с; содержимое не получено [O1] |
| 63 | `RanobeOvh.NovelOvhGetter` | [novel.ovh](https://novel.ovh/) | Не найден | — | Не запускался: Главная проверена обычным HTTP: error. The request was canceled due to the configured HttpClient.Timeout of 15 seconds elapsing.  |
| 64 | `RanobesCom.RanobesComGetter` | [ranobes.com](https://ranobes.com/) | [пример](https://ranobes.com/ranobe/177387-mother-of-learning.html) | 0 | Таймаут запроса 18 с; содержимое не получено [O3] |
| 65 | `ReadManga.ReadMangaGetter` | [readmanga.live](https://readmanga.live/) | Не найден | — | Не запускался: Главная проверена обычным HTTP: 404.   |
| 66 | `Readli.ReadliGetter` | [readli.net](https://readli.net/) | [пример](https://readli.net/svyazannyie-nepriyatnosti-2/) | 0 | Предел runner: 45 запросов, отмена на 46-м; результат не завершён [O1] |
| 67 | `Renovels.RemangaGetter` | [remanga.org](https://remanga.org/) | [пример](https://remanga.org/manga/rma-new-student-at-the-academy-is-a-hidden-disciple-of-the-legendary-school/main) | 0 | N/A: изображения; NoImage=true удалил img, текст не проверяется [O3]; [другой пример](https://remanga.org/manga/nemanga-/main): 0 непустых глав, N/A: getter вернул 0 глав; сборка файлов не доказывает получение книги [O1] |
| 68 | `Renovels.RenovelsGetter` | [renovels.org](https://renovels.org/) | [пример](https://renovels.org/novel/novel-fffclass-trashero/main) | 0 | HTTP 403; доступ к выбранному примеру не получен [O3] |
| 69 | `Romfant.RomfantGetter` | [romfant.ru](https://romfant.ru/) | Не найден | — | Не запускался: Главная проверена обычным HTTP: error. The request was canceled due to the configured HttpClient.Timeout of 15 seconds elapsing.  |
| 70 | `RoyalRoad.RoyalRoadGetter` | [royalroad.com](https://royalroad.com/) | [пример](https://www.royalroad.com/fiction/190085/save-the-heroine) | 0 | HTTP 403; доступ к выбранному примеру не получен [O1] |
| 71 | `Rulate.BllateGetter` | [bllate.org](https://bllate.org/) | [пример](https://bllate.org/book/23172) | 2 | Текст получен; FB2/EPUB созданы и структурно проверены [O1] |
| 72 | `Rulate.ErolateGetter` | [erolate.com](https://erolate.com/) | [пример](https://erolate.com/book/3570) | 0 | Таймаут запроса 18 с; содержимое не получено [O3] |
| 73 | `Rulate.MlateGetter` | [mlate.ru](https://mlate.ru/) | Не найден | — | Не запускался: Главная проверена обычным HTTP: error. The SSL connection could not be established, see inner exception.  |
| 74 | `Rulate.RulateGetter` | [tl.rulate.ru](https://tl.rulate.ru/) | [пример](https://tl.rulate.ru/book/129557) | 2 | Текст получен; FB2/EPUB созданы и структурно проверены [O1] |
| 75 | `Samlib.SamlibGetter` | [samlib.ru](http://samlib.ru/) | [пример](http://samlib.ru/o/obushnaja_j_a/inomirnoeputeshestviezemlaynki.shtml) | 1 | Текст получен; FB2/EPUB созданы и структурно проверены [O1] |
| 76 | `StrokiMts.StrokiMtsGetter` | [stroki.mts.ru](https://stroki.mts.ru/) | [пример](https://stroki.mts.ru/book/yevgeniy-onegin-387075) | 0 | Нужен пользовательский token; вход не выполнялся [O1] |
| 77 | `TopLiba.BiglibaGetter` | [bigliba.com](https://bigliba.com/) | Не найден | — | Не запускался: Главная проверена обычным HTTP: 200.  bigliba.com — Buy this expired domain \\| ExpiredDomains.com |
| 78 | `TopLiba.TopLibaGetter` | [topliba.com](https://topliba.com/) | [пример](https://topliba.com/books/1052385) | 0 | Таймаут запроса 18 с; содержимое не получено [O1] |
| 79 | `TwilightRussia.TwilightRussiaGetter` | [twilightrussia.ru](https://twilightrussia.ru/) | [пример](https://twilightrussia.ru/publ/1/vse_ludy/sdelka_s_vragom_moego_detstva_glava_1_vojna_cherez_dorogu/14-1-0-98011) | 0 | Некорректная исходная ссылка https://https//…; попытка соединения с хостом https [O3] |
| 80 | `Wattpad.WattpadGetter` | [wattpad.com](https://wattpad.com/) | [пример](https://www.wattpad.com/story/1001445-the-bro-code) | 2 | Текст получен; FB2/EPUB созданы и структурно проверены [O3]; авторская ссылка в первой части сохранена |
| 81 | `WattpadRu.WattpadGetter` | [watt-pad.ru](https://watt-pad.ru/) | [пример](https://watt-pad.ru/story/355566) | 2 | Текст получен; FB2/EPUB созданы и структурно проверены [O1] |
| 82 | `WuxiaWorld.WuxiaWorldGetter` | [wuxiaworld.ru](https://wuxiaworld.ru/) | [пример](https://wuxiaworld.ru/puteshestvie-k-bessmertiyu-goblinteam/goblinteam-puteshestvie-k-bessmertiyu-glava-521/) | 0 | Таймаут запроса 18 с; содержимое не получено [O1] |
| 83 | `Younettranslate.YounettranslateGetter` | [younettranslate.com](https://younettranslate.com/) | Не найден | — | Не запускался: Главная проверена обычным HTTP: 200.  Redirecting... |

## 12 общих базовых классов

Они входят в число 95 проверенных классов, но не являются отдельными сайтами и не добавляются к 83 строкам живой матрицы:

`BooksYandexGetterBase`, `Fb2TopGetterBase`, `FreedomGetterBase`, `HotNovelPubGetterBase`, `NewLibSocialGetterBase`, `MangaLibGetterBase`, `LitnetGetterBase`, `NovelxoGetterBase`, `RanobeOvhGetterBase`, `RenovelsGetterBase`, `RulateGetterBase`, `TopLibaGetterBase`.

## Исправления по результатам аудита исходного кода

В восьми конкретных загрузчиках устранено широкое удаление авторских заголовков: **Bookinist, Boosty, DarkNovels, Fb2Top, LadyLib, LitLife, TopLiba, Bigliba**. Первые шесть удаляют только начальный структурный заголовок, точно совпавший после нормализации пробелов с названием главы; остальные заголовки остаются. LitLife делает это для каждой страницы перед склейкой. TopLiba/Bigliba удаляют только конкретный узел `h1.capter-title`, из которого взято название. ID/name-якоря сохраняются; заголовки с изображениями либо содержательными ссылками не удаляются общей утилитой.

Общий [импорт EPUB](../src/Elib2Ebook.DomainServices/Getters/GetterBase.cs) больше не удаляет все h1/h2/h3. Он использует [ChapterContentCleaner](../src/Elib2Ebook.DomainServices/Misc/ChapterContentCleaner.cs), сохраняя внутренние заголовки, прозу, изображения и якоря. Это касается всех использующих импорт EPUB загрузчиков, включая Яндекс Книги, MyBook и Строки.

В [общем загрузчике Rulate](../src/ExternalServices/Elib2Ebook.ExternalServices.Rulate/Getters/RulateGetterBase.cs) распознаётся точная конечная самоссылка на ту же главу в явном списке родственных доменов: текущий SystemUrl, tl.rulate.ru, bllate.org, mlate.ru, erolate.com. Удаляется только отдельный последний p с голым URL либо единственной ссылкой с тем же адресом. Другие книги/главы, авторские подписи, сторонние адреса, query/fragment и нестандартные порты сохраняются. Основание межзеркального случая — URL основной главы в официально индексируемом ответе Erolate; живой getter Erolate в этом прогоне завершился таймаутом.

Исправление Litmarket, уже присутствовавшее до этого прохода, отделяет структурный заголовок из blocks от текста главы. Книга пользователя в матрице повторно проверена на первых двух главах.

Проверки изменений: **261 тест ядра и 31 тест сервисов прошли**. Это регрессионные проверки, в том числе искусственные HTTP-ответы и EPUB; они не заменяют проверку живого сайта. Для загрузчиков, у которых доступ завершился ошибкой, работоспособность на сайте после исправления здесь не утверждается.

## Предложения для следующих задач

Это отдельные предложения по результатам диагностики, а не отметки об уже исправленной живой работе.

| Следующая задача | Подтверждённое основание | Что ещё требуется проверить |
|---|---|---|
| Обновить разбор RanobeHub | Реальный HTTP 200 содержит современную NextFlight-карточку, без ожидаемых html[data-id]/h1.header; getter падает в GetToc | Получение актуального оглавления/глав и сохранность содержания на выбранном URL |
| Сделать пустой результат Jaomix явной диагностикой | Getter на реальной книге вернул 0 глав, runner создал файлы без подтверждённого текста | Какая часть текущей страницы содержит оглавление; отличать отсутствие доступных глав от изменения разметки |
| Разобрать ошибки Bookuruk, FictionBook и Acomics; пустой title Freedom | Зафиксированы NullReferenceException либо ArgumentNullException(title); у Acomics отдельно наблюдалась региональная заглушка | Точный отсутствующий узел и его причина; не считать всякий такой отказ поломкой парсера |
| Проверять исходные ссылки TwilightRussia | Из форума пришёл malformed URL вида https://https//twilightrussia.ru/publ/…; запрос пошёл к хосту https | Локальная валидация ссылки/понятная диагностика; общий MakeRelativeUri не объявлен причиной |
| Проверить сохранность непараграфной разметки WuxiaWorld/Romfant и примечаний Litres | WuxiaWorld удаляет прямые узлы кроме p; Romfant выбирает div.book p и переводит их в текст; Litres удаляет title/note/clipped из FB3 section | Реальный пример авторских заголовков/изображений/примечаний; широкие фильтры подтверждены кодом, фактическая потеря в живом примере этого прогона не доказана |
| Дополнить проверку комиксов и авторизованных источников | Три image-only результата получены при NoImage=true; Яндекс/Строки требуют token | Отдельный малый прогон с изображениями и предоставленным владельцем доступом; не считать текущий N/A успешной проверкой комикса |

## Свидетельства и воспроизводимость

Матрица собрана из локального инвентаря `content-getter-inventory.json`, итоговых manifests `content-live-a-f.json`, `content-live-g-n.json`, `content-live-o-z.json` и всех восьми итоговых `results.json`:

| Код | Локальный файл в investigation | Попыток |
|---|---|---:|
| A1 | content-live-results-a-f-stage1/results.json | 8 |
| A2 | content-live-results-a-f-stage2/results.json | 8 |
| A3 | content-live-results-a-f-stage3/results.json | 2 |
| G1 | content-live-results-g-n-stage1/results.json | 11 |
| G2 | content-live-results-g-n-stage2/results.json | 7 |
| G3 | content-live-results-g-n-stage3/results.json | 6 |
| O1 | content-live-results-o-z-stage1/results.json | 13 |
| O3 | content-live-results-o-z-stage3/results.json | 6 |

Проверено совпадение всех 57 пар полного имени getter и текущего BookUrl из manifests с результатами — пропущенных кандидатов нет. Дополнительные попытки: второй Libking, ещё две книги RanobeLib и второй Remanga. В публичный отчёт включены ссылки и диагностические итоги, без текста книг, cookies и токенов. Временные книги, HTML глав и JSON с полными текстами удалены после проверки; метаданные и результаты сохранены.
