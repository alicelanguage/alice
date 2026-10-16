param([string] $Tag = '')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

[xml] $props = Get-Content (Join-Path $PSScriptRoot '../../Directory.Build.props') -Raw
$version = [string] $props.Project.PropertyGroup.Version
if ($version -notmatch '^0\.1[0-9]\.(0|[1-9][0-9]*)$') {
    throw "Version must be 0.10.x through 0.19.x; found '$version'."
}
if ($Tag -and $Tag -cne "v$version") {
    throw "Tag '$Tag' must match Directory.Build.props: v$version."
}
$version
