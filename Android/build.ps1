param(
    [Parameter(Mandatory)][string]$PlatformJar,
    [Parameter(Mandatory)][string]$BuildTools,
    [Parameter(Mandatory)][string]$SigningKey
)

$ErrorActionPreference = 'Stop'
if (!$env:OPENDESK_SIGNING_PASSWORD) { throw 'Set OPENDESK_SIGNING_PASSWORD before building.' }
$platform = (Resolve-Path -LiteralPath $PlatformJar).Path
$tools = (Resolve-Path -LiteralPath $BuildTools).Path
$key = (Resolve-Path -LiteralPath $SigningKey).Path
$project = Split-Path $PSScriptRoot
$buildRoot = (New-Item -ItemType Directory -Force (Join-Path $project 'obj\android')).FullName
$build = (New-Item -ItemType Directory (Join-Path $buildRoot ([Guid]::NewGuid().ToString()))).FullName
$dist = (New-Item -ItemType Directory -Force (Join-Path $project 'dist')).FullName
$output = Join-Path $dist 'OpenDesk-Viewer-0.7.1.apk'

function Invoke-BuildTool([string]$binary, [string[]]$arguments) {
    & $binary @arguments
    if ($LASTEXITCODE -ne 0) { throw "$binary exited with code $LASTEXITCODE." }
}

try {
    $classes = (New-Item -ItemType Directory (Join-Path $build 'classes')).FullName
    $dex = (New-Item -ItemType Directory (Join-Path $build 'dex')).FullName
    $resources = Join-Path $build 'resources.zip'
    $unsigned = Join-Path $build 'unsigned.apk'
    $aligned = Join-Path $build 'aligned.apk'
    $signed = Join-Path $build 'signed.apk'
    $manifest = Join-Path $PSScriptRoot 'AndroidManifest.xml'
    $source = (Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -File -Recurse -Filter '*.java').FullName
    $aapt = Join-Path $tools 'aapt2.exe'

    Invoke-BuildTool $aapt @('compile', '--dir', (Join-Path $PSScriptRoot 'res'), '-o', $resources)
    Invoke-BuildTool $aapt @('link', '-I', $platform, '--manifest', $manifest, '-o', $unsigned, $resources)
    Invoke-BuildTool 'javac.exe' (@('--release', '8', '-classpath', $platform, '-d', $classes) + $source)
    $bytecode = (Get-ChildItem -LiteralPath $classes -File -Recurse -Filter '*.class').FullName
    $d8Arguments = @('--min-api', '26', '--lib', $platform, '--output', $dex) + $bytecode
    Invoke-BuildTool (Join-Path $tools 'd8.bat') $d8Arguments

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::Open($unsigned, [IO.Compression.ZipArchiveMode]::Update)
    try {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $archive, (Join-Path $dex 'classes.dex'), 'classes.dex') | Out-Null
    } finally {
        $archive.Dispose()
    }

    Invoke-BuildTool (Join-Path $tools 'zipalign.exe') @('-f', '4', $unsigned, $aligned)
    $signer = Join-Path $tools 'apksigner.bat'
    Invoke-BuildTool $signer @('sign', '--ks', $key, '--ks-key-alias', 'opendesk-viewer',
        '--ks-pass', 'env:OPENDESK_SIGNING_PASSWORD', '--out', $signed, $aligned)
    Invoke-BuildTool $signer @('verify', '--verbose', $signed)
    Copy-Item -LiteralPath $signed -Destination $output
    Write-Output $output
} finally {
    $resolved = (Resolve-Path -LiteralPath $build).Path
    if ($resolved.StartsWith($buildRoot + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolved -Recurse
    }
}
