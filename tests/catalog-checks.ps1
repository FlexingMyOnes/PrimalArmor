[CmdletBinding()]
param([string] $InstalledConfigDirectory)
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path $PSScriptRoot ('.catalog-fixture-' + [guid]::NewGuid().ToString('N'))
$checks = 0
function Assert([bool] $Condition, [string] $Message) {
    if (!$Condition) { throw $Message }
    $script:checks++
}
function Write-Json([string] $Path, $Value) {
    New-Item -ItemType Directory -Force -Path (Split-Path $Path -Parent) | Out-Null
    $Value | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $Path -Encoding utf8BOM
}
try {
    New-Item -ItemType Directory -Force -Path $fixture | Out-Null
    # Use PowerShell's bundled compiler with only local .NET 9 references; no SDK or downloads.
    Add-Type -Path (Join-Path $PSHOME 'Microsoft.CodeAnalysis.dll')
    Add-Type -Path (Join-Path $PSHOME 'Microsoft.CodeAnalysis.CSharp.dll')
    $runtimeParent = Join-Path $env:APPDATA 'FFXIVSimpleLauncher\Dalamud\Runtime\shared\Microsoft.NETCore.App'
    $runtime = Get-ChildItem -LiteralPath $runtimeParent -Directory | Where-Object Name -Match '^9\.' |
        Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    if (!$runtime) { throw 'The checks need a local .NET 9 runtime from Dalamud.' }
    $references = [System.Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
    foreach ($file in Get-ChildItem -LiteralPath $runtime.FullName -Filter '*.dll') {
        try { $null = [Reflection.AssemblyName]::GetAssemblyName($file.FullName)
            $references.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file.FullName))
        } catch [System.BadImageFormatException] { }
    }
    $trees = [System.Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]]::new()
    $parse = [Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::new([Microsoft.CodeAnalysis.CSharp.LanguageVersion]::CSharp13)
    foreach ($relative in @('Models\CatalogModels.cs','Catalog\ModCatalog.cs')) {
        $trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText(
            [IO.File]::ReadAllText((Join-Path $project $relative)), $parse, $relative))
    }
    $options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary).
        WithNullableContextOptions([Microsoft.CodeAnalysis.NullableContextOptions]::Enable)
    $compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('PrimalArmor.CatalogChecks', $trees, $references, $options)
    $assemblyPath = Join-Path $fixture 'CatalogChecks.dll'
    $stream = [IO.File]::Create($assemblyPath)
    try { $compiled = $compilation.Emit($stream) } finally { $stream.Dispose() }
    if (!$compiled.Success) { throw ($compiled.Diagnostics -join [Environment]::NewLine) }
    [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($assemblyPath)) | Out-Null
    $recipePath = Join-Path $project 'assets\recipes.json'
    $recipes = Get-Content -Raw -LiteralPath $recipePath | ConvertFrom-Json
    Assert ($recipes.Jobs.Count -eq 8) 'Expected eight public recipe jobs.'
    Assert (($recipes.Jobs.Races | Measure-Object).Count -eq 144) 'Expected 144 race/sex selections.'
    Assert (!(Get-Content -Raw -LiteralPath $recipePath).Contains('Directory')) 'Recipes contain a source directory.'
    $mods = Join-Path $fixture 'mods'
    $config = Join-Path $fixture 'pluginConfigs'
    Write-Json (Join-Path $config 'Penumbra.json') @{ ModDirectory = $mods }
    foreach ($job in $recipes.Jobs) {
        $mod = Join-Path $mods ('Source ' + $job.JobTag)
        New-Item -ItemType Directory -Force -Path $mod | Out-Null
        Write-Json (Join-Path $mod 'meta.json') @{ Name = ('The Primal ' + $job.JobTag + ' fixture') }
        Set-Content -LiteralPath (Join-Path $mod 'effect.avfx') 'synthetic fixture'
        Set-Content -LiteralPath (Join-Path $mod 'texture.atex') 'synthetic fixture'
        $suffix = if ($job.JobId % 2) { ' / 本地化' } else { '' }
        $options = @($job.Races | Group-Object Option | ForEach-Object {
            $race = $_.Group[0]
            @{ Name = ($race.Option + $suffix)
               Files = @{ $race.SourceEffectGamePath = 'effect.avfx'; 'vfx/texture/test.atex' = 'texture.atex' }
               FileSwaps = @{ 'vfx/eff/swap.avfx' = 'vfx/eff/original.avfx' }
               Manipulations = @(@{ Type = 'Imc'; Manipulation = @{
                   ObjectType='Equipment'; PrimaryId=188; SecondaryId=0; Variant=1; EquipSlot='Head'; BodySlot='Unknown'; Entry=@{ VfxId=1 }
               } }) }
        })
        Write-Json (Join-Path $mod 'group_001.json') @{ Name=($job.Group + $suffix); Options=$options }
        Write-Json (Join-Path $mod 'default_mod.json') @{ Files=@{ 'vfx/eff/default.avfx'='effect.avfx' } }
    }
    $swapped = Join-Path $mods 'Swapped copy'
    Write-Json (Join-Path $swapped 'meta.json') @{ Name='The Primal AST (Swapped)' }
    Write-Json (Join-Path $swapped 'default_mod.json') @{ Manipulations=@(@{ Type='Imc'; Manipulation=@{
        ObjectType='Equipment'; PrimaryId=999; SecondaryId=0; Variant=2; EquipSlot='Head'; BodySlot='Unknown'; Entry=@{VfxId=3}
    } }) }
    $catalog = [PrimalArmor.Catalog.ModCatalog]::new($recipePath)
    $snapshot = $catalog.LoadAsync($config, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    Assert ($snapshot.Jobs.Count -eq 8) 'Catalog lost a job.'
    $variants = @($snapshot.Jobs.Values | ForEach-Object { $_.Variants.Values })
    Assert ($variants.Count -eq 144) 'Catalog did not load all race/sex mappings.'
    Assert (@($variants | Where-Object { !$_.Available }).Count -eq 0) 'Complete fixtures should all be available.'
    Assert ($snapshot.WhitelistImcKeys.Contains('Equipment:999:0:2:Head:Unknown')) 'Swapped head IMC was omitted.'
    Assert ($snapshot.CarrierPaths.Contains('chara/equipment/e0999/vfx/eff/ve0003.avfx')) 'Swapped carrier was omitted.'
    $variant = $snapshot.FindVariant(20,'MidlanderMale')
    Assert ($variant.Files.ContainsKey($recipes.EmperorEffectPath)) 'Emperor alias was omitted.'
    Assert ([IO.Path]::IsPathRooted($variant.Files[$recipes.EmperorEffectPath])) 'Emperor alias did not resolve to a local file.'
    Assert ($variant.Files['vfx/eff/swap.avfx'] -eq 'vfx/eff/original.avfx') 'Game-path swap was not preserved.'
    Assert ($variant.CacheKey -eq ('MNK:' + $variant.Option)) 'Cache key is not job/option based.'
    $collections = Join-Path $config 'Penumbra\collections'
    $parentId = [guid]::NewGuid()
    $childId = [guid]::NewGuid()
    Write-Json (Join-Path $collections ($parentId.ToString()+'.json')) @{ Id=$parentId.ToString(); Inheritance=@(); Settings=@{
        'Source MNK'=@{Enabled=$true}; 'Source AST'=@{Enabled=$true}; 'Stale directory'=@{Enabled=$true}
    } }
    Write-Json (Join-Path $collections ($childId.ToString()+'.json')) @{ Id=$childId.ToString(); Inheritance=@($parentId.ToString()); Settings=@{
        'Source MNK'=@{Enabled=$false}; 'Source VPR'=@{Enabled=$true}
    } }
    Set-Content -LiteralPath (Join-Path $collections (([guid]::NewGuid()).ToString()+'.json')) '{broken unrelated history'
    $candidates = $catalog.GetCandidatePathSnapshotAsync($childId,[Threading.CancellationToken]::None).GetAwaiter().GetResult()
    Assert ($candidates.Complete) 'Unrelated malformed collection or stale mod made candidate scan fail.'
    Assert ($candidates.Paths.Contains('chara/equipment/e0709/vfx/eff/ve0001.avfx')) 'Child enabled mod not included.'
    Assert ($candidates.Paths.Contains('chara/equipment/e0279/vfx/eff/ve0001.avfx')) 'Inherited enabled mod not included.'
    Assert (!$candidates.Paths.Contains('chara/equipment/e0188/vfx/eff/ve0001.avfx')) 'Disabled child override did not suppress inherited mod.'
    Assert ($candidates.Paths.Contains('vfx/eff/swap.avfx')) 'FileSwaps key not included.'
    Assert ($candidates.Paths.Contains('vfx/eff/default.avfx')) 'Default submod key not included.'
    Assert (@($candidates.Issues | Where-Object Code -eq 'StaleMod').Count -eq 1) 'Stale mod not reported.'
    Write-Json (Join-Path $collections ($parentId.ToString()+'.json')) @{ Inheritance=@($childId.ToString()); Settings=@{} }
    $cycle = $catalog.GetCandidatePathSnapshotAsync($childId,[Threading.CancellationToken]::None).GetAwaiter().GetResult()
    Assert (!$cycle.Complete) 'Inheritance cycle should not silently look complete.'
    Remove-Item -LiteralPath (Join-Path $mods 'Source MNK\effect.avfx')
    $missing = $catalog.LoadAsync($config,[Threading.CancellationToken]::None).GetAwaiter().GetResult()
    Assert (!$missing.FindVariant(20,'MidlanderMale').Available) 'Missing asset should disable the variant.'
    Write-Json (Join-Path $config 'Penumbra.json') @{ ModDirectory=(Join-Path $fixture 'absent') }
    $empty = $catalog.LoadAsync($config,[Threading.CancellationToken]::None).GetAwaiter().GetResult()
    Assert ($empty.Jobs.Count -eq 8) 'Missing mod root should retain all job diagnostics.'
    Assert (@($empty.Issues | Where-Object Code -eq 'JobMissing').Count -eq 8) 'Missing root should report all jobs.'
    if ($InstalledConfigDirectory) {
        $live = $catalog.LoadAsync($InstalledConfigDirectory,[Threading.CancellationToken]::None).GetAwaiter().GetResult()
        $loaded = @($live.Jobs.Values | ForEach-Object { $_.Variants.Values } | Where-Object Available)
        Write-Host "Read-only installed catalog: $($live.Jobs.Count) jobs; $($loaded.Count) available selections; $($live.WhitelistImcKeys.Count) IMC keys; $($live.CarrierPaths.Count) carriers; $($live.Issues.Count) diagnostics."
        Assert ($loaded.Count -eq 144) 'Installed catalog did not reproduce all 144 reference selections.'
    }
    Write-Host "PASS: $checks catalog checks."
} finally {
    $resolvedFixture = [IO.Path]::GetFullPath($fixture)
    $resolvedTests = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ($resolvedFixture.StartsWith($resolvedTests,[StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedFixture)) {
        Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
    }
}
