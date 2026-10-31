[CmdletBinding()]
param(
    [string] $DalamudLibPath = $env:DALAMUD_HOME,
    [string] $RuntimePath,
    [ValidateSet('Auto', 'Sdk', 'Roslyn')]
    [string] $Compiler = 'Auto',
    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'bin\Release\net9.0-windows'),
    [switch] $Package,
    [string] $PackageDirectory = (Join-Path $PSScriptRoot 'artifacts')
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$assemblyName = 'PrimalArmor'
$version = '0.1.0.1'

function Get-LatestVersionDirectory([string] $ParentPath) {
    if (!(Test-Path -LiteralPath $ParentPath -PathType Container)) { return $null }
    Get-ChildItem -LiteralPath $ParentPath -Directory |
        Where-Object { $_.Name -match '^9\.\d+\.\d+$' } |
        Sort-Object { [version]$_.Name } -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}

if (!$DalamudLibPath) {
    $candidates = @(
        (Join-Path $env:APPDATA 'FFXIVSimpleLauncher\Dalamud\Injector'),
        (Join-Path $env:APPDATA 'XIVLauncher\addon\Hooks\dev')
    )
    $DalamudLibPath = $candidates | Where-Object { Test-Path -LiteralPath (Join-Path $_ 'Dalamud.dll') } | Select-Object -First 1
}
if (!$DalamudLibPath -or !(Test-Path -LiteralPath (Join-Path $DalamudLibPath 'Dalamud.dll'))) {
    throw 'Pass -DalamudLibPath with a local Dalamud API 13 Injector directory, or set DALAMUD_HOME.'
}
$DalamudLibPath = (Resolve-Path -LiteralPath $DalamudLibPath).Path
$dalamudIdentity = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $DalamudLibPath 'Dalamud.dll'))
if ($dalamudIdentity.Version.Major -ne 13) { throw 'Primal Armor requires Dalamud API 13 libraries.' }

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
$sdkInstalled = $false
if ($dotnet) {
    $sdkInstalled = @(& $dotnet.Source --list-sdks 2>$null).Count -gt 0
}
if ($Compiler -eq 'Sdk' -and !$sdkInstalled) { throw 'No .NET SDK is installed. Install the .NET 9 SDK, or use -Compiler Roslyn with local .NET 9 runtime libraries.' }
$useSdk = $Compiler -ne 'Roslyn' -and $sdkInstalled
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path

if ($useSdk) {
    & $dotnet.Source build (Join-Path $projectRoot 'PrimalArmor.csproj') --configuration Release --output $OutputDirectory "-p:DalamudLibPath=$DalamudLibPath" --nologo
    if ($LASTEXITCODE -ne 0) { throw 'The SDK build failed.' }
    $compilerDescription = '.NET SDK'
} else {
    # A local emergency compiler path for machines with PowerShell 7 and a Dalamud
    # runtime but no SDK. Only .NET 9 assemblies are passed as framework references.
    if (!$RuntimePath) {
        $runtimeRoot = Join-Path (Split-Path $DalamudLibPath -Parent) 'Runtime\shared\Microsoft.NETCore.App'
        $RuntimePath = Get-LatestVersionDirectory $runtimeRoot
    }
    if (!$RuntimePath) { throw 'Pass -RuntimePath to a Microsoft.NETCore.App\9.x directory. No network downloads are performed.' }
    $RuntimePath = (Resolve-Path -LiteralPath $RuntimePath).Path
    if ((Split-Path $RuntimePath -Leaf) -notmatch '^9\.\d+\.\d+$') { throw 'The fallback compiler requires .NET 9 runtime references.' }
    $desktopRoot = Join-Path (Split-Path (Split-Path $RuntimePath -Parent) -Parent) 'Microsoft.WindowsDesktop.App'
    $desktopRuntime = Get-LatestVersionDirectory $desktopRoot
    foreach ($name in @('Microsoft.CodeAnalysis.dll', 'Microsoft.CodeAnalysis.CSharp.dll')) {
        $compilerAssembly = Join-Path $PSHOME $name
        if (!(Test-Path -LiteralPath $compilerAssembly)) { throw "PowerShell does not bundle $name. Install the .NET 9 SDK and rerun." }
        Add-Type -Path $compilerAssembly
    }
    $referenceFiles = @{}
    foreach ($folder in @($RuntimePath, $desktopRuntime)) {
        if (!$folder) { continue }
        foreach ($file in Get-ChildItem -LiteralPath $folder -Filter '*.dll' -File) {
            if ($referenceFiles.ContainsKey($file.Name)) { continue }
            try {
                $null = [Reflection.AssemblyName]::GetAssemblyName($file.FullName)
                $referenceFiles[$file.Name] = $file.FullName
            } catch [System.BadImageFormatException] { }
        }
    }
    # Keep dependency selection identical to the SDK project instead of using
    # unrelated assemblies that happen to be present in the launcher directory.
    $projectDocument = [xml][IO.File]::ReadAllText((Join-Path $projectRoot 'PrimalArmor.csproj'))
    foreach ($dependency in @($projectDocument.Project.ItemGroup.Reference)) {
        if (!$dependency) { continue }
        $path = ([string]$dependency.HintPath).Replace('$(DalamudLibPath)', $DalamudLibPath)
        if (!(Test-Path -LiteralPath $path)) { throw "Required dependency was not found: $($dependency.Include)" }
        $referenceFiles[(Split-Path $path -Leaf)] = $path
    }
    $references = [System.Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
    foreach ($path in $referenceFiles.Values | Sort-Object) {
        $references.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($path))
    }
    $parseOptions = [Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::Default.WithLanguageVersion([Microsoft.CodeAnalysis.CSharp.LanguageVersion]::CSharp13)
    $trees = [System.Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]]::new()
    $sourceFiles = Get-ChildItem -LiteralPath $projectRoot -Filter '*.cs' -File -Recurse |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj|artifacts|tests)[\\/]' } |
        Sort-Object FullName
    if (!$sourceFiles) { throw 'No plugin C# source files were found.' }
    foreach ($source in $sourceFiles) {
        $relativePath = [IO.Path]::GetRelativePath($projectRoot, $source.FullName).Replace('\', '/')
        $sourceText = [Microsoft.CodeAnalysis.Text.SourceText]::From([IO.File]::ReadAllText($source.FullName), [Text.Encoding]::UTF8)
        $trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($sourceText, $parseOptions, $relativePath))
    }
    $generatedSource = @"
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Net.Http;
global using System.Threading;
global using System.Threading.Tasks;
[assembly: System.Reflection.AssemblyTitle("Primal Armor")]
[assembly: System.Reflection.AssemblyDescription("Match nearby players' Primal armor VFX to their jobs in your local view.")]
[assembly: System.Reflection.AssemblyCompany("Primal Armor contributors")]
[assembly: System.Reflection.AssemblyProduct("Primal Armor")]
[assembly: System.Reflection.AssemblyVersion("$version")]
[assembly: System.Reflection.AssemblyFileVersion("$version")]
[assembly: System.Reflection.AssemblyInformationalVersion("$version")]
[assembly: System.Runtime.Versioning.TargetFramework(".NETCoreApp,Version=v9.0", FrameworkDisplayName = ".NET 9.0")]
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]
"@
    $trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($generatedSource, $parseOptions, 'PrimalArmor.Generated.cs'))
    $options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary).
        WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release).
        WithAllowUnsafe($true).
        WithNullableContextOptions([Microsoft.CodeAnalysis.NullableContextOptions]::Enable).
        WithDeterministic($true)
    $compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create($assemblyName, $trees, $references, $options)
    # Dalamud can load the DLL from memory, so Assembly.Location cannot locate
    # sidecar files. Match the SDK EmbeddedResource item in the fallback build.
    $embeddedRecipePath = Join-Path $projectRoot 'assets\recipes.json'
    $resourceProvider = [Func[IO.Stream]]({ [IO.File]::OpenRead($embeddedRecipePath) }.GetNewClosure())
    $resources = [Collections.Generic.List[Microsoft.CodeAnalysis.ResourceDescription]]::new()
    $resources.Add([Microsoft.CodeAnalysis.ResourceDescription]::new('PrimalArmor.assets.recipes.json', $resourceProvider, $false))
    $outputPath = Join-Path $OutputDirectory "$assemblyName.dll"
    $stream = [IO.File]::Create($outputPath)
    try { $result = $compilation.Emit($stream, $null, $null, $null, $resources) } finally { $stream.Dispose() }
    foreach ($diagnostic in $result.Diagnostics) {
        if ($diagnostic.Severity -in @([Microsoft.CodeAnalysis.DiagnosticSeverity]::Error, [Microsoft.CodeAnalysis.DiagnosticSeverity]::Warning)) {
            Write-Host $diagnostic.ToString()
        }
    }
    if (!$result.Success) {
        Remove-Item -LiteralPath $outputPath -ErrorAction SilentlyContinue
        throw 'The local Roslyn build failed.'
    }
    $compilerDescription = "Roslyn $([Microsoft.CodeAnalysis.CSharp.CSharpCompilation].Assembly.GetName().Version); .NET 9 runtime references"
}

Copy-Item -LiteralPath (Join-Path $projectRoot 'PrimalArmor.json') -Destination $OutputDirectory -Force
$recipeRoot = Join-Path $projectRoot 'recipes'
if (Test-Path -LiteralPath $recipeRoot) { Copy-Item -LiteralPath $recipeRoot -Destination $OutputDirectory -Recurse -Force }
$assetRoot = Join-Path $projectRoot 'assets'
if (Test-Path -LiteralPath $assetRoot) { Copy-Item -LiteralPath $assetRoot -Destination $OutputDirectory -Recurse -Force }
$compiledAssembly = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $OutputDirectory 'PrimalArmor.dll'))
if ($compiledAssembly.Version.ToString() -ne $version) { throw 'Compiled assembly version does not match the manifest.' }
Write-Host "Built $($compiledAssembly.Name) $($compiledAssembly.Version) using $compilerDescription."

if ($Package) {
    New-Item -ItemType Directory -Path $PackageDirectory -Force | Out-Null
    $packageFiles = [System.Collections.Generic.List[string]]::new()
    $packageFiles.Add((Join-Path $OutputDirectory 'PrimalArmor.dll'))
    $packageFiles.Add((Join-Path $OutputDirectory 'PrimalArmor.json'))
    foreach ($name in @('README.md', 'LICENSE', 'NOTICE.md')) {
        $path = Join-Path $projectRoot $name
        if (Test-Path -LiteralPath $path) { $packageFiles.Add($path) }
    }
    if (Test-Path -LiteralPath $recipeRoot) { $packageFiles.Add($recipeRoot) }
    if (Test-Path -LiteralPath $assetRoot) { $packageFiles.Add($assetRoot) }
    $docsRoot = Join-Path $projectRoot 'docs'
    if (Test-Path -LiteralPath $docsRoot) { $packageFiles.Add($docsRoot) }
    $packagePath = Join-Path $PackageDirectory "PrimalArmor-$version.zip"
    Compress-Archive -LiteralPath $packageFiles.ToArray() -DestinationPath $packagePath -Force
    Write-Host "Package: $packagePath"
}
