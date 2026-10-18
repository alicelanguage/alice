# Offline regression for the SDK child process. Run via iex in a Restricted parent.
param([string] $RepositoryRoot = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'

$source = Join-Path $RepositoryRoot 'scripts/install.ps1'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($source, [ref] $tokens, [ref] $errors)
if ($errors.Count) { throw "Installer has parser errors: $errors" }
$helper = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-DotnetInstaller' }, $true)
if (-not $helper) { throw 'Missing policy-safe SDK launcher: Invoke-DotnetInstaller' }
. ([scriptblock]::Create($helper.Extent.Text))

$policy = Get-ExecutionPolicy
if ($env:OS -eq 'Windows_NT' -and $policy -ne 'Restricted') { throw "Regression must run with a Restricted parent, got $policy" }
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('alice policy test ' + [guid]::NewGuid().ToString('N'))
New-Item $tmp -ItemType Directory | Out-Null
try {
    $sdk = Join-Path $tmp 'dotnet-install.ps1'
    $output = Join-Path $tmp 'SDK with spaces'
    New-Item $output -ItemType Directory | Out-Null
    @'
param([string] $Channel, [string] $InstallDir, [switch] $NoPath)
@{ Channel = $Channel; InstallDir = $InstallDir; NoPath = [bool] $NoPath; Policy = [string] (Get-ExecutionPolicy); Edition = $PSVersionTable.PSEdition } |
    ConvertTo-Json | Set-Content (Join-Path $InstallDir 'invocation.json')
'@ | Set-Content $sdk
    Invoke-DotnetInstaller $sdk $output
    $result = Get-Content (Join-Path $output 'invocation.json') -Raw | ConvertFrom-Json
    if ($result.Channel -ne '10.0' -or $result.InstallDir -ne $output -or -not $result.NoPath) { throw 'SDK arguments were not preserved' }
    if ($env:OS -eq 'Windows_NT' -and $result.Policy -ne 'Bypass') { throw "Child policy was $($result.Policy), expected Bypass" }
    if ($result.Edition -ne $PSVersionTable.PSEdition) { throw 'SDK launcher selected a different PowerShell edition' }
    if ((Get-ExecutionPolicy) -ne $policy) { throw 'Parent execution policy changed' }

    'exit 23' | Set-Content $sdk
    $failed = $false
    try { Invoke-DotnetInstaller $sdk $output } catch {
        if ($_.Exception.Message -notmatch '23') { throw }
        $failed = $true
    }
    if (-not $failed) { throw 'SDK failure was silently ignored' }
    Write-Host "PASS: child SDK script runs; arguments, parent policy and failure propagation verified ($($PSVersionTable.PSEdition), parent $policy)"
} finally { Remove-Item $tmp -Recurse -Force }
