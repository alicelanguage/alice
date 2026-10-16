# Alice installer for Windows (PowerShell 5.1+ or 7+). No administrator rights, no Visual Studio.
#
#   irm https://github.com/alicelanguage/alice/releases/latest/download/install.ps1 | iex
#   & ([scriptblock]::Create((irm .../install.ps1))) -Version nightly
#
# Installs a verified release into $env:ALICE_HOME\toolchains\VERSION (default ~\.alice), makes it the default,
# writes ~\.alice\bin\alice.cmd, adds that to the user PATH, and installs the .NET 10 SDK per-user
# (Microsoft dotnet-install script into %LOCALAPPDATA%\Microsoft\dotnet) when it is missing.
# Afterwards manage versions with `alice toolchain` and update with `alice self update`.
param(
    # stable, nightly or an exact version such as 0.10.1 (env ALICE_VERSION)
    [string] $Version = $(if ($env:ALICE_VERSION) { $env:ALICE_VERSION } else { 'stable' }),
    # install root (env ALICE_HOME)
    [string] $AliceHome = $(if ($env:ALICE_HOME) { $env:ALICE_HOME } else { Join-Path $HOME '.alice' }),
    # install a downloaded release zip (verifies ARCHIVE.sha256 when present)
    [string] $Archive = '',
    [switch] $NoModifyPath,
    [switch] $NoDotnet
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is many times faster without the progress bar
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$Repo = if ($env:ALICE_RELEASES_REPO) { $env:ALICE_RELEASES_REPO } else { 'alicelanguage/alice' }

function Say([string] $Verb, [string] $Detail, [string] $Color = 'Green') {
    Write-Host ('{0,12} ' -f $Verb) -ForegroundColor $Color -NoNewline
    Write-Host $Detail
}
function Fail([string] $Message, [string] $Hint = '') {
    Write-Host 'error' -ForegroundColor Red -NoNewline
    Write-Host ": $Message"
    if ($Hint) { Write-Host '  hint' -ForegroundColor Cyan -NoNewline; Write-Host ": $Hint" }
    throw "Alice installation failed: $Message"
}
function Get-Sdk10([string] $Dotnet) {
    try { (& $Dotnet --list-sdks 2>$null) | Where-Object { $_ -match '^10\.' } | Select-Object -Last 1 } catch { $null }
}
function Add-UserPath([string] $Dir) {
    $current = [Environment]::GetEnvironmentVariable('Path', 'User')
    $parts = @(); if ($current) { $parts = $current.Split(';') | Where-Object { $_ } }
    if ($parts -notcontains $Dir) {
        [Environment]::SetEnvironmentVariable('Path', (@($Dir) + $parts) -join ';', 'User')
        Say 'Updated' "user PATH += $Dir" 'Cyan'
    }
    if (($env:Path.Split(';')) -notcontains $Dir) { $env:Path = "$Dir;$env:Path" }
}

# ---- platform ----
$arch = $env:PROCESSOR_ARCHITECTURE
if ($env:PROCESSOR_ARCHITEW6432) { $arch = $env:PROCESSOR_ARCHITEW6432 }
$rid = 'win-x64'
if ($arch -eq 'ARM64') { Write-Host '     warning ' -ForegroundColor Yellow -NoNewline; Write-Host 'no native ARM64 build yet; installing win-x64 (runs under emulation)' }
elseif ($arch -ne 'AMD64') { Fail "unsupported architecture $arch" }

Write-Host ''
Write-Host '  /|   ' -ForegroundColor Magenta -NoNewline
Write-Host 'Alice installer ' -NoNewline
Write-Host "($rid, $Version)" -ForegroundColor DarkGray
Write-Host ''

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("alice-install-" + [guid]::NewGuid().ToString('N'))
New-Item $tmp -ItemType Directory | Out-Null
try {
    # ---- .NET 10 SDK ----
    $dotnetDir = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
    $sdk = Get-Sdk10 'dotnet'
    if (-not $sdk -and (Test-Path (Join-Path $dotnetDir 'dotnet.exe'))) {
        $sdk = Get-Sdk10 (Join-Path $dotnetDir 'dotnet.exe')
        if ($sdk) { $env:DOTNET_ROOT = $dotnetDir; $env:Path = "$dotnetDir;$env:Path" }
    }
    if ($sdk) { Say 'Found' ".NET SDK $($sdk.Split(' ')[0])" }
    elseif (-not $NoDotnet) {
        Say 'Installing' ".NET 10 SDK into $dotnetDir (Microsoft dotnet-install, per-user)" 'Cyan'
        $installer = Join-Path $tmp 'dotnet-install.ps1'
        Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer -UseBasicParsing
        & $installer -Channel 10.0 -InstallDir $dotnetDir -NoPath | Out-Null
        if (-not (Test-Path (Join-Path $dotnetDir 'dotnet.exe'))) { Fail '.NET SDK installation failed' 'install it manually: https://dot.net/download' }
        [Environment]::SetEnvironmentVariable('DOTNET_ROOT', $dotnetDir, 'User')
        $env:DOTNET_ROOT = $dotnetDir
        if (-not $NoModifyPath) { Add-UserPath $dotnetDir } else { $env:Path = "$dotnetDir;$env:Path" }
        Say 'Installed' ".NET SDK $((Get-Sdk10 (Join-Path $dotnetDir 'dotnet.exe')).Split(' ')[0])"
    } else {
        Write-Host '     warning ' -ForegroundColor Yellow -NoNewline; Write-Host '.NET 10 SDK not found; Alice needs it to build (https://dot.net/download)'
    }

    # ---- release ----
    if ($Archive) {
        if (-not (Test-Path $Archive)) { Fail "archive not found: $Archive" }
        $name = Split-Path $Archive -Leaf
        Copy-Item $Archive (Join-Path $tmp $name)
        if (Test-Path "$Archive.sha256") { Copy-Item "$Archive.sha256" (Join-Path $tmp "$name.sha256") }
    } else {
        $headers = @{ 'User-Agent' = 'alice-installer'; 'Accept' = 'application/vnd.github+json' }
        if ($env:GITHUB_TOKEN) { $headers['Authorization'] = "Bearer $env:GITHUB_TOKEN" }
        $api = switch -Regex ($Version) {
            '^(stable|latest)$' { "https://api.github.com/repos/$Repo/releases/latest"; break }
            '^nightly$' { "https://api.github.com/repos/$Repo/releases/tags/nightly"; break }
            default { "https://api.github.com/repos/$Repo/releases/tags/v$($Version.TrimStart('v'))" }
        }
        Say 'Resolving' "$Version from github.com/$Repo" 'Cyan'
        try { $release = Invoke-RestMethod $api -Headers $headers -UseBasicParsing }
        catch { Fail "could not find release '$Version'" "see https://github.com/$Repo/releases" }
        $asset = $release.assets | Where-Object { $_.name -like "alice-*-$rid.zip" } | Select-Object -First 1
        $sum = $release.assets | Where-Object { $_.name -like "alice-*-$rid.zip.sha256" } | Select-Object -First 1
        if (-not $asset) { Fail "release '$Version' has no $rid archive" }
        if (-not $sum) { Fail "release '$Version' has no checksum for $rid" }
        $name = $asset.name
        Say 'Downloading' ("{0} ({1:N1} MB)" -f $name, ($asset.size / 1MB))
        Invoke-WebRequest $asset.browser_download_url -OutFile (Join-Path $tmp $name) -UseBasicParsing
        Invoke-WebRequest $sum.browser_download_url -OutFile (Join-Path $tmp "$name.sha256") -UseBasicParsing
    }

    $versionName = $name -replace '^alice-', '' -replace "-$rid\.zip$", ''
    $zip = Join-Path $tmp $name
    if (Test-Path "$zip.sha256") {
        $expected = ((Get-Content "$zip.sha256" -Raw).Trim() -split '\s+')[0].ToLowerInvariant()
        $actual = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($expected -ne $actual) { Fail "checksum mismatch for $name" 'the download was corrupted or tampered with; retry' }
        Say 'Verified' "sha256 $actual"
    } else {
        Write-Host '     warning ' -ForegroundColor Yellow -NoNewline; Write-Host "no .sha256 next to $Archive; skipping verification"
    }

    $toolchains = Join-Path $AliceHome 'toolchains'
    $bin = Join-Path $AliceHome 'bin'
    $dest = Join-Path $toolchains $versionName
    $stage = Join-Path $toolchains ".tmp-$versionName-$PID"
    New-Item $toolchains, $bin -ItemType Directory -Force | Out-Null
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    Expand-Archive $zip -DestinationPath $stage
    if (-not (Test-Path (Join-Path $stage 'alice.exe'))) { Fail "$name does not contain alice.exe" }
    if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
    Move-Item $stage $dest
    Get-ChildItem $dest -Recurse | Unblock-File -ErrorAction SilentlyContinue

    Set-Content (Join-Path $AliceHome 'settings.json') "{`n  `"default`": `"$versionName`"`n}" -Encoding ascii
    Set-Content (Join-Path $bin 'alice.cmd') "@echo off`r`n`"$dest\alice.exe`" %*" -Encoding ascii
    Say 'Installed' "alice $versionName -> $dest"

    if (-not $NoModifyPath) { Add-UserPath $bin } else { $env:Path = "$bin;$env:Path" }

    try { & (Join-Path $dest 'alice.exe') --no-color version | Out-Null } catch { Write-Host "     warning alice did not start; run `"$dest\alice.exe doctor`"" -ForegroundColor Yellow }

    Write-Host ''
    Write-Host "  Alice $versionName is installed." -ForegroundColor Green
    Write-Host ''
    Write-Host '  Open a new terminal, then:'
    Write-Host '    alice                                   ' -NoNewline; Write-Host 'start the REPL' -ForegroundColor DarkGray
    Write-Host '    alice new hello; cd hello; alice run    ' -NoNewline; Write-Host 'create a project' -ForegroundColor DarkGray
    Write-Host '    alice doctor                            ' -NoNewline; Write-Host 'check the setup' -ForegroundColor DarkGray
    Write-Host '    alice toolchain list                    ' -NoNewline; Write-Host 'manage versions (install nightly, default VER)' -ForegroundColor DarkGray
    Write-Host '    alice self update                       ' -NoNewline; Write-Host 'update later' -ForegroundColor DarkGray
    Write-Host '    alice completions pwsh | Out-String | iex   ' -NoNewline; Write-Host 'tab completion' -ForegroundColor DarkGray
    Write-Host ''
} finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
