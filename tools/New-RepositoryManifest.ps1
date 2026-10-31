[CmdletBinding()]
param(
    [Parameter(Mandatory)][uri]$DownloadUrl,
    [Parameter(Mandatory)][uri]$RepositoryUrl,
    [Parameter(Mandatory)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
foreach ($taskUri in @($DownloadUrl,$RepositoryUrl)) {
    if (!$taskUri.IsAbsoluteUri -or $taskUri.Scheme -ne 'https') { throw 'Use an absolute HTTPS URL.' }
}
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskManifest = Get-Content -LiteralPath (Join-Path $taskRoot 'PrimalArmor.json') -Raw | ConvertFrom-Json
$taskEntry = [ordered]@{}
foreach ($taskProperty in $taskManifest.PSObject.Properties) { $taskEntry[$taskProperty.Name] = $taskProperty.Value }
$taskEntry['RepoUrl'] = $RepositoryUrl.AbsoluteUri
$taskEntry['DownloadLinkInstall'] = $DownloadUrl.AbsoluteUri
$taskEntry['DownloadLinkUpdate'] = $DownloadUrl.AbsoluteUri
$taskEntry['IsTestingExclusive'] = $false
$taskEntry['LastUpdate'] = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$taskJson = ConvertTo-Json -InputObject @($taskEntry) -Depth 10
[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath),$taskJson + [Environment]::NewLine,[Text.UTF8Encoding]::new($false))
Write-Output ('Generated local repository manifest: ' + [IO.Path]::GetFullPath($OutputPath))
