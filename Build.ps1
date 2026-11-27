param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'))
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$manifest = Get-Content -LiteralPath (Join-Path $root 'plugin.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$version = [string]$manifest.version
if ($version -notmatch '^(0|[1-9]\d{0,4})\.(0|[1-9]\d{0,4})\.(0|[1-9]\d{0,4})$') { throw 'Invalid plugin manifest version.' }
$source = [IO.File]::ReadAllText((Join-Path $root 'NetworkPlugin.cs'))
if (-not $source.Contains('private const string Version = "' + $version + '";')) { throw 'Plugin manifest and executable version differ.' }
$framework = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path (Join-Path $framework 'csc.exe'))) {
    $framework = Join-Path $env:SystemRoot 'Microsoft.NET\Framework\v4.0.30319'
}
$csc = Join-Path $framework 'csc.exe'
if (-not (Test-Path $csc)) { throw 'Windows .NET Framework compiler not found.' }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$stage = Join-Path ([IO.Path]::GetTempPath()) ('AppShelf-Network-' + [guid]::NewGuid().ToString('N'))
try {
    [void][IO.Directory]::CreateDirectory((Join-Path $stage 'bin'))
    $exe = Join-Path $stage 'bin\NetworkPlugin.exe'
    & $csc /nologo /codepage:65001 /target:winexe /platform:anycpu /optimize+ "/out:$exe" /reference:System.dll /reference:System.Core.dll /reference:System.Xml.dll /reference:System.Runtime.Serialization.dll "/reference:$framework\WPF\WindowsBase.dll" "/reference:$framework\WPF\PresentationCore.dll" "/reference:$framework\WPF\PresentationFramework.dll" /reference:System.Xaml.dll (Join-Path $root 'PluginProtocol.cs') (Join-Path $PSScriptRoot 'NetworkPlugin.cs') (Join-Path $PSScriptRoot 'NetworkChecks.cs') (Join-Path $PSScriptRoot 'PluginTheme.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Network plugin compilation failed.' }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'plugin.json') -Destination $stage
    $output = [IO.Path]::GetFullPath($OutputDirectory)
    [void][IO.Directory]::CreateDirectory($output)
    $package = Join-Path $output ('NetworkPlugin-' + $version + '.asplugin')
    $temporary = Join-Path $output ([guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $archive = [IO.Compression.ZipFile]::Open($temporary, [IO.Compression.ZipArchiveMode]::Create)
        try {
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $stage 'plugin.json'), 'plugin.json')
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $exe, 'bin/NetworkPlugin.exe')
        } finally { $archive.Dispose() }
        if ([IO.File]::Exists($package)) { [IO.File]::Replace($temporary, $package, $null) }
        else { [IO.File]::Move($temporary, $package) }
    } finally {
        if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
    }
    Write-Output $package
} finally {
    Remove-Item -LiteralPath $stage -Recurse -Force
}
