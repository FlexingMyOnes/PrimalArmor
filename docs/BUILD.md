# Building Primal Armor

The plugin targets Windows, .NET 9, and Dalamud API 13. It has no NuGet package dependencies. Dalamud, Penumbra, Glamourer, game files, and visual assets are not redistributed with the plugin.

## Standard build

Install the .NET 9 SDK and use local API 13 Dalamud developer libraries:

```powershell
dotnet build .\PrimalArmor.csproj -c Release -p:DalamudLibPath="D:\Dalamud\Injector"
```

Or run the helper, which can detect a standard local launcher directory:

```powershell
.\build.ps1 -DalamudLibPath 'D:\Dalamud\Injector' -Compiler Sdk -Package
```

`DALAMUD_HOME` can supply the library directory. Build output is `bin/Release/net9.0-windows`; packages are written to `artifacts` unless overridden. The DLL and `PrimalArmor.json` must remain beside one another. Keep the `assets` directory alongside them.

## Local build without an SDK

On a machine where PowerShell 7 bundles the Roslyn compiler assemblies, the helper can compile with existing .NET 9 runtime libraries from the launcher:

```powershell
.\build.ps1 -Compiler Roslyn -DalamudLibPath 'D:\Dalamud\Injector' -RuntimePath 'D:\Dalamud\Runtime\shared\Microsoft.NETCore.App\9.0.11' -Package
```

This fallback performs no downloads. It uses only the supplied .NET 9 runtime assemblies as framework references, even if PowerShell itself runs on a newer .NET version. Its output has a .NET 9 target-framework attribute. The SDK route remains the usual development workflow because it uses official targeting reference assemblies.

The package includes the plugin DLL, manifest, recipe metadata, license, and documentation. The script does not copy dependency DLLs from the local launcher. No asset files are included beyond the plugin's own recipe metadata.

## Validation scope

A successful build establishes source compatibility with the local API 13 assemblies. It does not establish that a particular TW 7.25 launcher build accepts the plugin, that external plugin IPC is available in game, or that user-supplied assets fit a character. Verify those behaviors in game using the manual checks in the repository documentation.
