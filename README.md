<picture>
  <source media="(prefers-color-scheme: dark)" srcset=".github/assets/banner-dark.png">
  <img alt="Deduplicator — офлайн-поиск дубликатов файлов по содержимому" src=".github/assets/banner-light.png" width="100%">
</picture>

**Deduplicator** — офлайн-утилита для Windows 10/11: находит дубликаты файлов по содержимому, а не по имени, и собирает разрозненные носители в один чистый архив без копий.

![stack](https://img.shields.io/badge/stack-.NET%208%20%2B%20Avalonia%2011-4D703B?style=flat-square&labelColor=443327)
![platform](https://img.shields.io/badge/platform-Windows%2010%2F11-648B51?style=flat-square&labelColor=443327)
![version](https://img.shields.io/badge/version-1.0.1-4D703B?style=flat-square&labelColor=443327)

## Что умеет

- Ищет точные копии по содержимому: группировка по размеру, частичный и полный XxHash128, побайтовая сверка перед признанием копии.
- Находит похожие фото (pHash, pixel-hash, опционально с учётом поворотов и отражений) и похожую музыку (аудио-отпечатки Chromaprint через `fpcalc.exe`).
- Быстро обходит NTFS-тома через Win32/MFT/USN, повторный скан читает только изменения USN-журнала; HDD читаются последовательно, SSD/NVMe — параллельно.
- Хранит каталог носителей: сверка содержимого между дисками, обзор отключённого носителя по сохранённому слепку, переносимый индекс `.dedupindex` на самом носителе.
- Собирает «Чистый архив»: уникальное копируется, дубликаты пропускаются.
- Очищает мусор (пустые папки, файлы 0 байт, временные файлы, битые ярлыки, большие файлы) и показывает SMART-состояние дисков.
- Экспортирует отчёт в CSV, выбирает дубли по правилу, заменяет дубли жёсткими ссылками (NTFS, один том), защищает папки-эталоны.

Принципы безопасности:

- Ничего не удаляется автоматически: только по подтверждению и только в Корзину или карантин с восстановлением через «Историю».
- В группе всегда остаётся минимум одна копия; похожие файлы обрабатываются только вручную.
- Файловые записи атомарны, перенос между томами идёт как копирование → проверка → удаление источника, намерение операции записывается в журнал до её выполнения.
- Битые, занятые и недоступные файлы и длинные пути не роняют скан: файл пропускается с записью в лог.
- Нет телеметрии и отправки данных сканирования. Сеть нужна только для проверки обновлений на GitHub (её можно выключить в настройках).

## Быстрый старт

Нужны .NET SDK 10.0.102 (закреплён в `global.json`) и среда выполнения .NET 8; приложение работает на Windows 10/11.

```bash
dotnet restore Deduplicator.slnx --locked-mode
dotnet build Deduplicator.slnx -c Release --no-restore
dotnet test Deduplicator.slnx -c Release --no-build

# запуск приложения
dotnet run --project src/Deduplicator.App/Deduplicator.App.csproj -c Release

# справка консольного движка
dotnet run --project src/FastDedup.Cli/FastDedup.Cli.csproj -- --help
```

Restore выполняйте с `--locked-mode` до Release-сборки: lock-файлы NuGet сформированы для Debug-оценки, и неявный restore в Release их перепишет.

## Стек

| Часть | Технологии |
|---|---|
| UI | Avalonia 11.2, CommunityToolkit.Mvvm 8.4 |
| Ядро | .NET 8 (C#), FastDedup.Engine (Win32/MFT/USN), XxHash128 (`System.IO.Hashing`) |
| Хранилище | SQLite в режиме WAL (Microsoft.Data.Sqlite 8.0, SQLitePCLRaw 3) |
| Медиа | SixLabors.ImageSharp 3.1 + CoenM.ImageHash, Chromaprint `fpcalc.exe` (опционально) |
| Тесты | xUnit, headless UI-тесты Avalonia |

## Деплой

Сборка релиза для Windows:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/publish-win-x64.ps1
powershell -ExecutionPolicy Bypass -File scripts/build-installer.ps1
```

`publish-win-x64.ps1` делает locked restore, сборку, тесты, аудит уязвимостей NuGet и самодостаточную публикацию в `dist/`; `build-installer.ps1` собирает установщик NSIS (`installer/Deduplicator.nsi`).

CI `.github/workflows/windows-release.yml` повторяет эти шаги на Windows, при наличии секретов репозитория подписывает бинарники (имена секретов — в [SECURITY.md](SECURITY.md)), проверяет тихую установку и удаление и публикует артефакты с контрольными суммами.

## Архитектура

Зависимости: `Deduplicator.App → Deduplicator.Core → FastDedup.Engine`; консольный инструмент использует движок напрямую.

```
Deduplicator.slnx
├── src/Deduplicator.App       — Avalonia UI (MVVM): 12 страниц, главное окно, экран загрузки
├── src/Deduplicator.Core      — ядро: модели, SQLite-репозитории, хэширование, медиа, группировка, сервисы
├── src/FastDedup.Engine       — быстрый обход Win32/MFT/USN и потоковая воронка хэширования
├── src/Deduplicator.MftHelper — узкий NTFS-helper с повышенными правами; UI и база работают без них
├── src/FastDedup.Cli          — консольный запуск FastDedup.Engine
├── tests/                     — модульные, интеграционные и headless UI-тесты (xUnit)
└── tools/                     — DedupBench, генератор иконки, место для fpcalc.exe
```

Подробно — в [docs/architecture.md](docs/architecture.md).

## Данные приложения

Данные хранятся в `%LOCALAPPDATA%\Deduplicator\`:

- `deduplicator.db` — база слепков и групп (WAL);
- `Карантин\` — перемещённые файлы, восстанавливаются через «Историю»;
- `cache\thumbnails\` — кэш миниатюр;
- `settings.json` — настройки.

Переносимый режим: пустой файл `Deduplicator.portable` рядом с `Deduplicator.exe` переводит хранение в соседнюю папку `Data\`. Установщик этот режим не включает. Слепки хранят только метаданные и хэши — около 0,5 ГБ на миллион файлов (см. [docs/architecture.md](docs/architecture.md)).

## Аудио-сверка

Похожая музыка требует `fpcalc.exe` из Chromaprint (проект AcoustID). Положите его рядом с приложением, в `tools/fpcalc.exe` или укажите путь в настройках — см. [tools/README.txt](tools/README.txt). Без fpcalc остальные функции работают.

## Ограничения

Похожие видео (нужен ffmpeg), превью и pHash для RAW/HEIC и мультиязычный интерфейс пока не реализованы. Полный список — в [docs/known-issues.md](docs/known-issues.md).

## Документы

- [PRIVACY.md](PRIVACY.md) — какие данные обрабатываются
- [SECURITY.md](SECURITY.md) — как сообщить об уязвимости
- [CHANGELOG.md](CHANGELOG.md) — история изменений
- [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) — сторонние компоненты и лицензии

## Лицензия

Лицензия пока не выбрана — см. [LICENSE](LICENSE), все права защищены.

## English summary

Deduplicator is an offline Windows 10/11 desktop app that finds duplicate files by content rather than by name. It is built with .NET 8 and Avalonia 11 and uses a fast Win32/MFT/USN scanning engine, XxHash128 with byte-by-byte verification, and a SQLite (WAL) catalog of drives. It also finds similar photos (perceptual hashing) and similar music (Chromaprint `fpcalc.exe`, optional), and can merge scattered drives into one clean, duplicate-free archive. Nothing is deleted automatically: files go to the Recycle Bin or a restorable quarantine, and every group keeps at least one copy. Build with `dotnet restore --locked-mode`, then `dotnet build -c Release --no-restore`; the UI is in Russian.

---

<sub>Сделано в <a href="https://vanking.uz">vanking.uz</a> · Telegram <a href="https://t.me/vanking_bot">@vanking_bot</a></sub>
