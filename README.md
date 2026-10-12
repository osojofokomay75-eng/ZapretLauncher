# Zapret Launcher

Простое приложение для Windows, которое включает [zapret](https://github.com/bol-van/zapret) в один клик.
Один маленький `ZapretLauncher.exe` — больше ничего рядом не нужно.

*A tiny one-file Windows app that downloads and runs zapret (DPI bypass) with one click.*

## Как это работает

1. Запустите `ZapretLauncher.exe` (потребуются права администратора — их требует драйвер WinDivert).
2. При первом запуске приложение само скачает свежую сборку [Flowseal/zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube) с GitHub в `%LOCALAPPDATA%\ZapretLauncher`.
3. Нажмите большую кнопку питания. Если что-то не открывается — нажмите «Автоподбор»: приложение по очереди пробует стратегии и оставляет первую, при которой открываются YouTube и Discord.

Дополнительно:

- **Игровой фильтр** — расширяет обход на игровые порты.
- **Запускать вместе с Windows** — создаёт задачу планировщика с повышенными правами: приложение стартует в трее, сразу включает обход и не показывает окно UAC.
- **Обновить** — скачивает новую версию компонентов; ваши файлы `*user*.txt` в `lists` сохраняются.
- При закрытии окна во время работы приложение сворачивается в трей. Выход — через значок в трее.

Рядом с exe приложение ничего не создаёт: все данные лежат в `%LOCALAPPDATA%\ZapretLauncher`.
Чтобы удалить всё, выключите автозапуск, закройте приложение и удалите exe и эту папку.

## Важно

- Антивирусы иногда ругаются на `winws.exe` и `WinDivert` (это легитимные инструменты перехвата трафика, но они похожи на «хакерские»). Если файлы удаляются, добавьте папку `%LOCALAPPDATA%\ZapretLauncher` в исключения.
- Для разбора стратегий приложение читает `.bat`-файлы из сборки Flowseal. Если автор сильно поменяет формат этих файлов, парсер в `Core.cs` (`Engine.BuildArgs`) придётся поправить.
- Автоподбор — эвристика: он проверяет, что отвечают YouTube и Discord. На разных провайдерах результат может отличаться.
- Это независимый проект, он не связан с авторами zapret и zapret-discord-youtube. Используйте на свой риск и с учётом законов вашей страны.

## Сборка

Нужен [.NET SDK](https://dotnet.microsoft.com/download) 6+ на Windows:

```
dotnet build ZapretLauncher.csproj -c Release -o out
```

Готовый `out/ZapretLauncher.exe` — единственный файл, который нужно раздавать (приложение собрано под .NET Framework 4.8, он встроен в Windows 10/11).

### Релиз через GitHub Actions

Любой push в `main` собирает exe (вкладка Actions → Artifacts). Чтобы выпустить релиз:

```
git tag v1.0.0
git push origin v1.0.0
```

Workflow сам соберёт exe и приложит его к релизу.

## Благодарности

[bol-van/zapret](https://github.com/bol-van/zapret) и [Flowseal/zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube) — все заслуги за сам обход принадлежат им.
