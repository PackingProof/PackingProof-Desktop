# Repository Guidelines

## Project Structure & Module Organization

- `ExpressPackingMonitoring.sln` is the main solution.
- `ExpressPackingMonitoring/` contains the WPF application, including XAML views, view models, services, SQLite access, recording logic, and `Web/index.html`.
- `ExpressPackingMonitoring.Launcher/` contains the small launcher executable used by the clean package layout.
- `build/Publish-CleanPackage.ps1` creates the distributable directory and zip package.
- `Scripts/快递助手订单推送.user.js` is the browser userscript for order push integration.
- `Image/` stores README and project screenshots. `Test/HTML/` contains captured sample pages for script/debug reference, not an automated test suite.

## Build, Test, and Development Commands

```powershell
dotnet restore ExpressPackingMonitoring.sln
dotnet build ExpressPackingMonitoring.sln -c Debug
dotnet run --project ExpressPackingMonitoring
powershell -ExecutionPolicy Bypass -File build\Publish-CleanPackage.ps1
```

- `restore` downloads NuGet dependencies.
- `build` verifies the WPF app and launcher compile.
- `run` starts the main app locally.
- `Publish-CleanPackage.ps1` produces the clean release layout with the root launcher and `app\` payload.

## Runtime and Distribution Notes

- The publish script generates a directory package and a matching `.zip`.
- The clean package root should mainly contain `ExpressPackingMonitoring.exe` and `app\`; the real app payload, dependencies, Web files, LibVLC files, and `tools\ffmpeg.exe` live under `app\`.
- Release packages must not include `config.json`, `videos.db`, cache files, logs, recordings, or other local runtime data.
- Runtime data is stored under `%LOCALAPPDATA%\ExpressPackingMonitoring\`, so normal upgrades keep user configuration and database records.
- `ffmpeg.exe` may be resolved from `app\tools\ffmpeg.exe`, the application runtime directory, or the system `PATH`.
- `Scripts/快递助手订单推送.user.js` is the browser userscript used for order push integration.
- Edge TTS is the default online voice path. Kokoro local TTS models and runtime dependencies are optional and should not be bundled unless explicitly intended.

## Coding Style & Naming Conventions

Use C# with nullable references and implicit usings enabled. Follow the existing WPF/MVVM style: `PascalCase` for public types, properties, and commands; `camelCase` for locals; `_camelCase` for private fields. Keep XAML names descriptive and aligned with their backing view or view model. Preserve UTF-8 text and avoid broad line-ending or encoding churn, especially in Chinese strings, XAML, HTML, and userscript files.

## Testing Guidelines

There is no dedicated unit test project yet. At minimum, run `dotnet build ExpressPackingMonitoring.sln -c Debug` before committing. For recording, Web playback, TTS, packaging, or FFmpeg changes, also run the affected workflow manually and note what was verified. Use `Test/HTML/` pages when validating userscript parsing behavior.

## Commit & Pull Request Guidelines

Recent history uses conventional prefixes with Chinese subjects, for example `fix: 优化 Web 搜索和转码确认` and `docs: 优化 README 表述`. Keep commits scoped and include a short body explaining what changed and why. Do not include secrets, local paths, account IDs, signing files, or machine-specific details.

Pull requests should include a concise summary, validation steps, linked issue if applicable, and screenshots or recordings for UI, playback, or packaging changes.

## Security & Configuration Tips

Do not commit generated configs, databases, logs, caches, recordings, `.env` files, certificates, or signing material. Runtime data belongs under `%LOCALAPPDATA%\ExpressPackingMonitoring\`; release packages should not include local user state.
