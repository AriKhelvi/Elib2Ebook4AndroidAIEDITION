# Разбор сбоев релизов и CI — 12 ноября 2026

Старые красные запуски имеют разные причины. В двух релизах APK уже был собран, но GitHub запретил менять опубликованный immutable-релиз. Отдельно срабатывал устаревший порог количества загрузчиков и не выделялся macOS runner.

| Запуск | Что подтверждено | Действие |
|---|---|---|
| [v0.0.1, run 40405667414](https://github.com/AriKhelvi/Elib2Ebook4AndroidAIEDITION/actions/runs/40405667414/job/121862642855) | APK проверен и сохранён в Actions. `gh release upload --clobber` завершился HTTP 422 `Validation Failed`, следом: `Cannot delete asset from an immutable release`. Замена пыталась удалить уже существующий файл. | Не заменять файлы опубликованного релиза. Для исправленной сборки использовать новый номер; APK прикреплять к draft до публикации. |
| [v0.0.2, run 40481965051](https://github.com/AriKhelvi/Elib2Ebook4AndroidAIEDITION/actions/runs/40481965051/job/122114219917) | Три попытки загрузки APK получили HTTP 422 `Cannot upload assets to an immutable release`. Повтор запроса не устраняет запрет immutable. | Не пытаться дозагрузить APK после публикации и не переставлять тег. Готовить новый релиз с APK заранее. |
| [Android APK, run 40657017613](https://github.com/AriKhelvi/Elib2Ebook4AndroidAIEDITION/actions/runs/40657017613/job/122710421289) | После проверки подписи найдено по 45 сборок сайтов для arm64 и arm32. Job остановила проверка «меньше 50», хотя состав загрузчиков намеренно меняется. | Порог 50 уже удалён. Сохранены проверка подписи, выравнивания APK и равенства количества сборок между архитектурами. |
| [.NET, run 43205159590, коммит 5bd3f37](https://github.com/AriKhelvi/Elib2Ebook4AndroidAIEDITION/actions/runs/43205159590) | Аннотации сообщают, что hosted runner не получил задание, и указывают нехватку macOS arm64 capacity. В доступном списке jobs сборка macOS успешна, `publish (macos-latest)` отменён без шагов. | Это сбой выделения runner, а не ошибка компиляции. При повторении достаточно повторить нужный job после восстановления доступности. |

Логи первых трёх jobs повторно прочитаны через GitHub. Для macOS проверены сохранённые аннотации и текущий список jobs. Старые релизы, теги и assets в ходе аудита не изменялись; перезапуски не отправлялись.

## Состояние проверенного коммита

У `fcdd380bc0481b80c67048f5ee70359fb9615887` нет упавших или ожидающих проверок:

- [.NET](https://github.com/AriKhelvi/Elib2Ebook4AndroidAIEDITION/actions/runs/43209578352): build и упаковка CLI на Ubuntu, Windows и macOS, а также тесты — success. Шаги публикации релиза и Docker — skipped; job `publish` здесь означает создание артефактов CLI.
- Android APK для [ветки](https://github.com/AriKhelvi/Elib2Ebook4AndroidAIEDITION/actions/runs/43209578331) и [PR](https://github.com/AriKhelvi/Elib2Ebook4AndroidAIEDITION/actions/runs/43209586869) — success.
- [CodeQL](https://github.com/AriKhelvi/Elib2Ebook4AndroidAIEDITION/actions/runs/43209586762) — success; 11 замечаний уровня notice и 0 warning. Разбор нового сообщения бота — в [CODEQL-REVIEW.md](CODEQL-REVIEW.md).

Эти результаты относятся именно к `fcdd380`. Они не заменяют запуск CI для последующих изменений загрузчиков и процесса релиза. Подготовка нового workflow ведётся отдельно; успешная публикация им в этом аудите не заявляется.
