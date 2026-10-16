param(
    [Parameter(Mandatory)] [string] $PackageDirectory,
    [Parameter(Mandatory)] [string] $Version
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$package = (Resolve-Path $PackageDirectory).Path
$executable = Join-Path $package $(if ($IsWindows) { 'alice.exe' } else { 'alice' })

foreach ($asset in @('alice.dll', 'alice.deps.json', 'alice.runtimeconfig.json',
    'Alice.Compiler.dll', 'Alice.Runtime.dll', 'Microsoft.CodeAnalysis.dll',
    'Microsoft.CodeAnalysis.CSharp.dll', 'sdk/Alice.props', 'sdk/Alice.targets')) {
    if (-not (Test-Path (Join-Path $package $asset) -PathType Leaf)) {
        throw "Package is missing $asset."
    }
}

function Invoke-Alice {
    param([string[]] $Arguments, [string] $Expected = '')
    $output = & $executable --lang en @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "alice $Arguments failed ($LASTEXITCODE): $output" }
    $text = $output -join "`n"
    if ($Expected -and -not $text.Contains($Expected)) {
        throw "alice $Arguments did not print '$Expected': $text"
    }
    Write-Host $text
}

# Run outside the checkout so imports cannot silently use source-tree SDK files.
$work = Join-Path ([IO.Path]::GetTempPath()) "alice-smoke-$([guid]::NewGuid().ToString('N'))"
New-Item $work -ItemType Directory | Out-Null
Push-Location $work
try {
    Invoke-Alice -Arguments @('--no-color', 'version') -Expected $Version
    'puts "packaged compiler works"' | Set-Content main.al -Encoding utf8NoBOM
    Invoke-Alice -Arguments @('run', 'main.al') -Expected 'packaged compiler works'
    Invoke-Alice -Arguments @('new', 'smoke')
    Push-Location smoke
    try {
        Invoke-Alice -Arguments @('build', '--release')
        Invoke-Alice -Arguments @('run') -Expected 'sum of squares: 385'
        Invoke-Alice -Arguments @('test') -Expected '2 passed; 0 failed'
    } finally { Pop-Location }
} finally {
    Pop-Location
    Remove-Item $work -Recurse -Force
}
