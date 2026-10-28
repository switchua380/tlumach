# Тлумач (Tlumach)

Програма для перекладу Unity-ігор від команди SwitchUA: пошук і редагування текстів у ассетах,
заміна шрифтів TextMeshPro, атласи, імпорт перекладу в гру та збирання архіву для гравців.

> Приватний репозиторій. Усі права збережено.

## Структура

- `Launcher/` — основна програма (.NET 8, C#), інтерфейс у `Launcher/www/`
- `TMPFontReplacer/` — створення SDF-атласу з TTF і вбудовування TMP-шрифту
- `exe/` — збирання `Tlumach.exe` (стандартний .NET apphost)
- `lib/` — сторонні бібліотеки (MIT), див. `THIRD_PARTY_NOTICES.txt`
- `tools/` — генератор `tmp_schemas.txt` (лише для розробки)

## Збірка

```
cd Launcher
dotnet build -c Release -o out
```

Запуск для розробки:

```
dotnet out/TranslationLauncher.dll --port 47200 --token TEST --no-browser --stay
```

Інтерфейс: http://127.0.0.1:47200/?t=TEST

Детальніше про архітектуру й пакування — у `CLAUDE.md`.
