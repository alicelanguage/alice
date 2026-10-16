param(
    [string] $Tag = '',
    [ValidateSet('stable', 'nightly')] [string] $Channel = 'stable',
    # UTC build stamp for nightlies (yyyyMMdd or yyyyMMddHHmm); defaults to today.
    [string] $Stamp = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

[xml] $props = Get-Content (Join-Path $PSScriptRoot '../../Directory.Build.props') -Raw
$version = [string] $props.Project.PropertyGroup.Version
if ($version -notmatch '^0\.1[0-9]\.(0|[1-9][0-9]*)$') {
    throw "Version must be 0.10.x through 0.19.x; found '$version'."
}
if ($Channel -eq 'nightly') {
    # Nightlies precede the next patch release: 0.10.1 -> 0.10.2-nightly.20261016
    $parts = $version.Split('.')
    if (-not $Stamp) { $Stamp = [DateTime]::UtcNow.ToString('yyyyMMdd') }
    if ($Stamp -notmatch '^[0-9]{8}([0-9]{4})?$') { throw "Nightly stamp must be yyyyMMdd[HHmm]; found '$Stamp'." }
    return "$($parts[0]).$($parts[1]).$([int]$parts[2] + 1)-nightly.$Stamp"
}
if ($Tag -and $Tag -cne "v$version") {
    throw "Tag '$Tag' must match Directory.Build.props: v$version."
}
$version
