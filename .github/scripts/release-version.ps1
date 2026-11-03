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
# 1.x and later: MAJOR.MINOR.PATCH, optionally a release candidate -rc.N (no more 0.x releases).
if ($version -cnotmatch '^[1-9][0-9]*\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-rc\.[1-9][0-9]*)?$') {
    throw "Version must be MAJOR.MINOR.PATCH (1.0.0 or later) with an optional -rc.N; found '$version'."
}
if ($Channel -eq 'nightly') {
    # Nightlies precede the next release: 1.0.1 -> 1.0.2-nightly.20261016; a candidate precedes its
    # release, so 1.0.0-rc.1 -> 1.0.0-nightly.20261016.
    $core = $version.Split('-')[0]
    $parts = $core.Split('.')
    if (-not $Stamp) { $Stamp = [DateTime]::UtcNow.ToString('yyyyMMdd') }
    if ($Stamp -notmatch '^[0-9]{8}([0-9]{4})?$') { throw "Nightly stamp must be yyyyMMdd[HHmm]; found '$Stamp'." }
    if ($core -cne $version) { return "$core-nightly.$Stamp" }
    return "$($parts[0]).$($parts[1]).$([int]$parts[2] + 1)-nightly.$Stamp"
}
if ($Tag -and $Tag -cne "v$version") {
    throw "Tag '$Tag' must match Directory.Build.props: v$version."
}
$version
