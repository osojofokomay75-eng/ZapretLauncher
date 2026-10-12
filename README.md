# Zapret Launcher

Простое приложение для Windows, которое включает [zapret](https://github.com/bol-van/zapret) в один клик.
Один маленький установщик `ZapretLauncherSetup.exe` — больше ничего не нужно.

*A tiny one-file Windows app that downloads and runs zapret (DPI bypass) with one click.*

## Как это работает

1. Запустите `ZapretLauncherSetup.exe` и пройдите установку (потребуются права администратора — их требует драйвер WinDivert).
2. При первом запуске приложение само скачает свежую сборку [Flowseal/zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube) с GitHub в `%LOCALAPPDATA%\ZapretLauncher`.
3. Нажмите большую кнопку питания. Если что-то не открывается — нажмите «Автоподбор»: приложение по очереди пробует стратегии и оставляет первую, при которой открываются YouTube и Discord.

Дополнительно:

- **Игровой фильтр** — расширяет обход на игровые порты.
- **Запускать вместе с Windows** — создаёт задачу планировщика с повышенными правами: приложение стартует в трее, сразу включает обход и не показывает окно UAC.
- **Обновить** — скачивает новую версию компонентов; ваши файлы `*user*.txt` в `lists` сохраняются.
- Если включено «Сворачивать в трей» (по умолчанию да), при закрытии окна приложение остаётся в трее. Выход — через значок в трее.

Рядом с exe приложение ничего не создаёт: все данные лежат в `%LOCALAPPDATA%\ZapretLauncher`.
Удаляется программа через «Установка и удаление программ» в параметрах Windows.

## Установщик

`ZapretLauncherSetup.exe` — это тот же exe под другим именем. Любая копия, которая не является установленной, при запуске открывает мастер установки.
Мастер копирует лаунчер в выбранную папку (по умолчанию `C:\Program Files\Zapret Launcher`), создаёт ярлыки, регистрирует программу в «Установка и удаление программ» и сразу скачивает компоненты zapret.

Удаление — через параметры Windows (или `ZapretLauncher.exe --uninstall`). Мастер удаления спрашивает, сохранить ли настройки и ваши списки `*user*.txt`.
Запустить без установки можно только с ключом `--portable` (нужно для разработки): `ZapretLauncher.exe --portable`.

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

Рядом появится `out/ZapretLauncherSetup.exe` — установщик (копия того же файла).
Раздавать нужно только `out/ZapretLauncherSetup.exe` (приложение собрано под .NET Framework 4.8, он встроен в Windows 10/11).

### Релиз через GitHub Actions

Любой push в `main` собирает exe (вкладка Actions → Artifacts). Чтобы выпустить релиз:

```
git tag v1.0.0
git push origin v1.0.0
```

Workflow сам соберёт exe и приложит его к релизу.

## Благодарности

[bol-van/zapret](https://github.com/bol-van/zapret) и [Flowseal/zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube) — все заслуги за сам обход принадлежат им.
