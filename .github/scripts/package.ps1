param(
    [Parameter(Mandatory)]
    [ValidateSet('linux-x64', 'win-x64', 'osx-x64', 'osx-arm64')]
    [string] $Runtime,
    [Parameter(Mandatory)] [string] $Version,
    [string] $OutputDirectory = 'artifacts'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$sourceVersion = & (Join-Path $PSScriptRoot 'release-version.ps1')
$nightlyBase = & (Join-Path $PSScriptRoot 'release-version.ps1') -Channel nightly -Stamp '00000000'
$nightlyBase = $nightlyBase.Substring(0, $nightlyBase.IndexOf('-'))
if ($sourceVersion -cne $Version -and $Version -notmatch "^$([regex]::Escape($nightlyBase))-nightly\.[0-9]{8}([0-9]{4})?$") {
    throw "Expected source version $sourceVersion (or $nightlyBase-nightly.STAMP), got $Version."
}

# ReadyToRun assemblies and apphosts must be smoke-tested on the target OS/architecture.
$hostArch = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
$hostOS = if ($IsWindows) { 'win' } elseif ($IsMacOS) { 'osx' } elseif ($IsLinux) { 'linux' } else { 'unsupported' }
$hostRuntime = "$hostOS-$hostArch"
if ($Runtime -ne $hostRuntime) {
    throw "Cannot smoke-test ReadyToRun package $Runtime with host $hostRuntime. Run packaging on a matching OS and architecture."
}

Push-Location $root
try {
    $output = [IO.Path]::GetFullPath($OutputDirectory)
    $name = "alice-$Version-$Runtime"
    $publish = Join-Path $output "publish/$name"
    $dist = Join-Path $output 'dist'
    $unpack = Join-Path $output "smoke/$name"
    # Refuse stale output; every archive must come from one publish operation.
    if (Test-Path $publish) { throw "Output already exists: $publish. Choose a new output directory." }
    New-Item $publish, $dist, $unpack -ItemType Directory -Force | Out-Null

    # Keep assemblies on disk: Roslyn uses assembly paths and MSBuild runs alice.dll.
    & dotnet publish src/Alice.Cli/Alice.Cli.csproj --configuration Release --runtime $Runtime `
        --self-contained false --output $publish --artifacts-path (Join-Path $output 'build') `
        -p:UseAppHost=true -p:PublishSingleFile=false -p:PublishTrimmed=false `
        -p:PublishAot=false -p:PublishReadyToRun=true -p:ContinuousIntegrationBuild=true `
        -p:Version=$Version
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $LASTEXITCODE" }

    Copy-Item (Join-Path $root 'docs/releases.md') (Join-Path $publish 'RELEASES.md')
    Copy-Item (Join-Path $root 'LICENSE') (Join-Path $publish 'LICENSE')
    Copy-Item (Join-Path $root 'NOTICE') (Join-Path $publish 'NOTICE')
    if ($Runtime -eq 'win-x64') {
        $archive = Join-Path $dist "$name.zip"
        Compress-Archive -Path "$publish/*" -DestinationPath $archive
        Expand-Archive $archive -DestinationPath $unpack
    } else {
        $archive = Join-Path $dist "$name.tar.gz"
        & tar -czf $archive -C $publish .
        if ($LASTEXITCODE -ne 0) { throw 'Archive creation failed.' }
        & tar -xzf $archive -C $unpack
        if ($LASTEXITCODE -ne 0) { throw 'Archive extraction failed.' }
    }

    & (Join-Path $PSScriptRoot 'smoke-package.ps1') -PackageDirectory $unpack -Version $Version
    $hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($archive))" | Set-Content "$archive.sha256" -Encoding ascii
    Write-Host "Verified package: $archive"
} finally { Pop-Location }
