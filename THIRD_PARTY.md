# Сторонние компоненты

Исходники LocalRemote распространяются под MIT. Лицензия LocalRemote не заменяет лицензии сторонних компонентов.

## FFmpeg

Для H.264 приложение запускает отдельный `ffmpeg.exe`. Используется GPL-сборка [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds), указанная среди Windows-сборок на [сайте FFmpeg](https://ffmpeg.org/download.html#build-windows).

- Лицензия сборки: [GPL v3](third_party/ffmpeg/LICENSE.txt).
- Сведения о сборке, контрольная сумма архива и ссылки на исходники: [SOURCE.txt](third_party/ffmpeg/SOURCE.txt).
- Исходники FFmpeg: [ffmpeg.org](https://ffmpeg.org/download.html).
- Скрипты сборки и настройки зависимостей: [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds).

Бинарный FFmpeg не хранится в Git. `scripts/Get-FFmpeg.ps1` скачивает сборку и проверяет SHA-256 по digest GitHub Release. При упаковке сведения и лицензия копируются в папку `FFmpeg` рядом с приложением. Если распространяете изменённый комплект, сохраняйте эти файлы и соблюдайте условия лицензий включённых компонентов.

## .NET

Автономная сборка включает .NET Runtime от Microsoft. Исходники, лицензия и notices: [dotnet/runtime](https://github.com/dotnet/runtime). При упаковке соответствующие файлы копируются из установленного runtime.
