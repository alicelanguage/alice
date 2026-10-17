param(
    [Parameter(Mandatory)] [string] $PackageDirectory,
    [Parameter(Mandatory)] [string] $Version,
    # Run via a matching-architecture .NET host instead of the apphost.
    [switch] $ViaDotnet
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
    $output = if ($ViaDotnet) { & dotnet (Join-Path $package 'alice.dll') --lang en @Arguments 2>&1 } else { & $executable --lang en @Arguments 2>&1 }
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
    # The installed CLI must speak LSP on stdout without banners or update notices.
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = if ($ViaDotnet) { 'dotnet' } else { $executable }
    if ($ViaDotnet) { $start.ArgumentList.Add((Join-Path $package 'alice.dll')) }
    $start.ArgumentList.Add('lsp')
    $start.ArgumentList.Add('--stdio')
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['ALICE_NO_UPDATE_CHECK'] = '1'
    $server = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $server.StandardOutput.ReadToEndAsync()
        $stderr = $server.StandardError.ReadToEndAsync()
        foreach ($message in @(
            '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"capabilities":{}}}',
            '{"jsonrpc":"2.0","id":2,"method":"shutdown"}',
            '{"jsonrpc":"2.0","method":"exit"}'
        )) {
            $frame = "Content-Length: $([Text.Encoding]::UTF8.GetByteCount($message))`r`n`r`n$message"
            $server.StandardInput.Write($frame)
        }
        $server.StandardInput.Close()
        if (-not $server.WaitForExit(15000)) { $server.Kill($true); throw 'Packaged LSP did not exit.' }
        $protocol = $stdout.GetAwaiter().GetResult()
        if ($server.ExitCode -ne 0 -or -not $protocol.StartsWith('Content-Length: ') -or -not $protocol.Contains('"completionProvider"')) {
            throw "Packaged LSP failed: $protocol $($stderr.GetAwaiter().GetResult())"
        }
    } finally { $server.Dispose() }

    'puts "packaged compiler works"' | Set-Content main.al -Encoding utf8NoBOM
    Invoke-Alice -Arguments @('run', 'main.al') -Expected 'packaged compiler works'
    Invoke-Alice -Arguments @('new', 'smoke')
    Push-Location smoke
    try {
        Invoke-Alice -Arguments @('build', '--release')
        Invoke-Alice -Arguments @('run') -Expected 'sum of squares: 385'
        Invoke-Alice -Arguments @('test') -Expected '2 examples, 0 failures'
    } finally { Pop-Location }
} finally {
    Pop-Location
    Remove-Item $work -Recurse -Force
}
