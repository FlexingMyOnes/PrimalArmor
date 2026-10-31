[CmdletBinding()]
param(
    [string] $PluginAssembly,
    [string] $DalamudLibPath = (Join-Path $env:APPDATA 'FFXIVSimpleLauncher\Dalamud\Injector'),
    [string] $PenumbraDirectory
)

$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
if (!$PluginAssembly) { $PluginAssembly = Join-Path $project 'bin\Release\net9.0-windows\PrimalArmor.dll' }
if (!(Test-Path -LiteralPath $PluginAssembly)) { throw 'Build the plugin first, or pass -PluginAssembly with the compiled DLL.' }
if (!$PenumbraDirectory) {
    $installed = Join-Path $env:APPDATA 'FFXIVSimpleLauncher\Dalamud\Config\installedPlugins\Penumbra'
    $PenumbraDirectory = Get-ChildItem -LiteralPath $installed -Directory |
        Where-Object { $_.Name -match '^\d+(\.\d+){1,3}$' } |
        Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (!$PenumbraDirectory) { throw 'Pass -PenumbraDirectory with an installed Penumbra provider directory.' }

# Run in a fresh PowerShell process. This loads the installed decoder into this
# test process only. No game access, injection, mods/config writes or downloads.
[void][Reflection.Assembly]::LoadFrom((Join-Path $DalamudLibPath 'Dalamud.dll'))
[void][Reflection.Assembly]::LoadFrom((Join-Path $PenumbraDirectory 'Penumbra.GameData.dll'))
$provider = [Reflection.Assembly]::LoadFrom((Join-Path $PenumbraDirectory 'Penumbra.dll'))
[void][Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $PluginAssembly).Path)
$decode = $provider.GetType('Penumbra.Api.Api.MetaApi', $true).GetMethod('ConvertManips', [Reflection.BindingFlags]'NonPublic,Static')
if (!$decode) { throw 'Installed Penumbra metadata decoder is unsupported.' }
$checks = 0
function Assert([bool] $Condition, [string] $Message) {
    if (!$Condition) { throw $Message }
    $script:checks++
}
function Encode-Meta([string] $Json) {
    $memory = [IO.MemoryStream]::new()
    $gzip = [IO.Compression.GZipStream]::new($memory, [IO.Compression.CompressionLevel]::Optimal, $true)
    try {
        $gzip.WriteByte(0)
        $bytes = [Text.Encoding]::UTF8.GetBytes($Json)
        $gzip.Write($bytes, 0, $bytes.Length)
        $gzip.Dispose()
        return [Convert]::ToBase64String($memory.ToArray())
    } finally { $gzip.Dispose(); $memory.Dispose() }
}
function Read-Meta([string] $Encoded) {
    $arguments = [object[]]@($Encoded, $null, [byte]0)
    if (!$decode.Invoke($null, $arguments)) { throw 'The real Penumbra decoder rejected metadata.' }
    return $arguments[1]
}

# Nondefault fields expose accidental replacement of a complete ImcEntry.
# Other supported types must survive through the provider-specific converter.
$sourceJson = @'
[
 {"Type":"Imc","Manipulation":{"Entry":{"MaterialId":1,"DecalId":3,"VfxId":1,"MaterialAnimationId":2,"AttributeMask":1023,"SoundId":4},"ObjectType":"Equipment","PrimaryId":279,"SecondaryId":0,"Variant":0,"EquipSlot":"Head","BodySlot":"Unknown"}},
 {"Type":"Imc","Manipulation":{"Entry":{"MaterialId":2,"DecalId":5,"VfxId":1,"MaterialAnimationId":3,"AttributeMask":133,"SoundId":6},"ObjectType":"Equipment","PrimaryId":279,"SecondaryId":0,"Variant":1,"EquipSlot":"Head","BodySlot":"Unknown"}},
 {"Type":"Imc","Manipulation":{"Entry":{"MaterialId":3,"DecalId":7,"VfxId":7,"MaterialAnimationId":4,"AttributeMask":257,"SoundId":8},"ObjectType":"Equipment","PrimaryId":999,"SecondaryId":0,"Variant":0,"EquipSlot":"Head","BodySlot":"Unknown"}},
 {"Type":"Eqp","Manipulation":{"Entry":234153095762804736,"SetId":"188","Slot":"Head"}},
 {"Type":"Eqdp","Manipulation":{"Entry":0,"Gender":"Female","Race":"Hrothgar","SetId":"5004","Slot":"Head"}},
 {"Type":"Est","Manipulation":{"Entry":848,"Gender":"Male","Race":"Midlander","SetId":"709","Slot":"Head"}},
 {"Type":"Gmp","Manipulation":{"Entry":{"Enabled":true,"Animated":false,"RotationA":180,"RotationB":180,"RotationC":0,"UnknownA":10,"UnknownB":10},"SetId":"709"}}
]
'@
$encoded = Encode-Meta $sourceJson
$before = Read-Meta $encoded
$neutralizer = [PrimalArmor.Interop.MetaNeutralizer]::new()
$keys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
[void]$keys.Add('Equipment:279:0:1:Head:Unknown')
$output = $neutralizer.Clear($encoded, $keys)
$after = Read-Meta $output
Assert ($before.Count -eq 7 -and $after.Count -eq 7) 'Mixed fixture metadata was lost.'
$changed = 0
foreach ($kind in @('Imc','Eqp','Eqdp','Est','Gmp','Rsp','Atch','Shp','Atr','GlobalEqp')) {
    $oldEntries = $before.$kind
    $newEntries = $after.$kind
    Assert ($oldEntries.Count -eq $newEntries.Count) "Metadata count changed: $kind"
    foreach ($pair in $oldEntries.GetEnumerator()) {
        $expected = $pair.Value
        if ($kind -eq 'Imc' -and $pair.Key.PrimaryId.Id -eq 279 -and $pair.Key.Variant.Id -eq 1) {
            $expected.GetType().GetProperty('VfxId').SetValue($expected, [byte]0)
            $changed++
        }
        Assert ($newEntries.ContainsKey($pair.Key) -and $expected.Equals($newEntries[$pair.Key])) "Unexpected change: $kind $($pair.Key)"
    }
}
Assert ($changed -eq 1) 'The explicit whitelist must change exactly one VFX entry.'
Assert ($neutralizer.Clear($output, $keys) -ceq $output) 'Neutralization must be idempotent.'
Assert ($neutralizer.Clear('', $keys) -ceq '') 'Empty metadata must stay empty.'
$empty = [Collections.Generic.HashSet[string]]::new()
Assert ($neutralizer.Clear($encoded, $empty) -ceq $encoded) 'An empty whitelist must preserve the original bytes.'
$malformedRejected = $false
try { $null = $neutralizer.Clear('not base64 metadata', $keys) } catch { $malformedRejected = $true }
Assert $malformedRejected 'Malformed metadata must fail closed.'

$hat = Read-Meta ([PrimalArmor.Interop.MetaNeutralizer]::EmperorHatMetadata)
Assert ($hat.Imc.Count -eq 2) 'Emperor metadata must contain both head variants.'
foreach ($pair in $hat.Imc.GetEnumerator()) {
    Assert ($pair.Key.PrimaryId.Id -eq 279 -and $pair.Key.Variant.Id -in @(0,1)) 'Wrong Emperor carrier.'
    Assert ($pair.Value.MaterialId -eq 1 -and $pair.Value.DecalId -eq 0 -and $pair.Value.VfxId -eq 1 -and
        $pair.Value.MaterialAnimationId -eq 0 -and $pair.Value.AttributeMask -eq 1023 -and $pair.Value.SoundId -eq 0) 'Wrong Emperor entry fields.'
}
$memory = [IO.MemoryStream]::new([Convert]::FromBase64String($output))
$gzip = [IO.Compression.GZipStream]::new($memory, [IO.Compression.CompressionMode]::Decompress)
try { Assert ($gzip.ReadByte() -eq 0) 'Output must use the provider-supported V0 payload.' }
finally { $gzip.Dispose(); $memory.Dispose() }

[ordered]@{
    Success = $true
    Checks = $checks
    FixtureCount = $before.Count
    PreservedNonemptyTypes = @('Imc','Eqp','Eqdp','Est','Gmp')
    WhitelistedChanges = $changed
    Idempotent = $true
    EmperorHatEntries = $hat.Imc.Count
    Provider = $provider.GetName().Version.ToString()
    Scope = 'Offline installed-provider decoder; no game or user-file changes'
} | ConvertTo-Json
