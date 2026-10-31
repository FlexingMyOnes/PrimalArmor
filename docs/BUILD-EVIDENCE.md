# Build evidence

Date: 2026-10-31

| Check | Result |
| --- | --- |
| Assembly | `PrimalArmor`, version `0.1.0.1` |
| Target | Windows, `.NETCoreApp,Version=v9.0` |
| Dalamud library | API 13, assembly `13.0.0.16` |
| Compiler | Local Roslyn `5.0.0.0` hosted by PowerShell 7.6.5 |
| Framework references | Existing launcher .NET 9.0.11 runtime assemblies |
| Build result | Success; zero compile errors and zero warnings |
| Bundled recipes | `PrimalArmor.assets.recipes.json` embedded by both SDK and Roslyn builds |
| Memory-loaded release DLL | Empty `Assembly.Location`; embedded recipes match source; read-only local scan found 8 jobs and 144 available selections |
| Regression checks | 41 catalog checks, 353 engine assertions, 29 installed-provider metadata checks passed |
| Framework assembly references in DLL | `System.Private.CoreLib`, `System.Text.Json`, `System.IO.Compression`, `System.Linq`, `System.Text.RegularExpressions`: all `9.0.0.0` |
| Direct game/plugin dependencies in DLL | `Dalamud`, `Dalamud.Bindings.ImGui`, `Lumina`, `Lumina.Excel` |
| .NET 10 references in DLL | None |
| Penumbra/Glamourer/Newtonsoft direct assembly references | None; IPC and provider metadata integration use the loaded providers |
| Runtime package dependency DLLs | None copied from Dalamud or other plugins |

The environment has no .NET SDK, so the helper's local Roslyn fallback produced the release DLL. The SDK project is included for standard .NET 9 development but was not executed in this environment. No downloads were performed.

The build and metadata checks do not constitute an in-game test. The TW 7.25 launcher acceptance, actual installed-provider IPC behavior, character redraws, race/gender variants, state restoration, and externally supplied visual assets still require the documented in-game checks.
