# Safe offline network plugin regression. No external probes or system changes.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Add-Type -AssemblyName System.IO.Compression.FileSystem
$temp = Join-Path ([IO.Path]::GetTempPath()) ('AppShelf-NetworkTests-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temp)
try {
    & (Join-Path $root 'Build.ps1') -OutputDirectory $temp
    & (Join-Path $root 'Build.ps1') -OutputDirectory $temp
    if (@(Get-ChildItem -LiteralPath $temp -File).Count -ne 1) { throw 'Repeated build left temporary or backup files.' }
    Write-Host 'PASS: two consecutive builds into the same directory'
    $manifest = Get-Content -LiteralPath (Join-Path $root 'plugin.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $version = [string]$manifest.version
    $package = Join-Path $temp ('NetworkPlugin-' + $version + '.asplugin')
    $archive = [IO.Compression.ZipFile]::OpenRead($package)
    try {
        $entries = @($archive.Entries | ForEach-Object { $_.FullName } | Sort-Object)
        if (($entries -join '|') -cne 'bin/LICENSE|bin/NetworkPlugin.exe|bin/NOTICE.md|plugin.json') { throw 'Package must contain one EXE, one manifest and license notices only.' }
    } finally { $archive.Dispose() }
    [IO.Compression.ZipFile]::ExtractToDirectory($package,(Join-Path $temp 'unpacked'))
    $packagedManifest = Get-Content -LiteralPath (Join-Path $temp 'unpacked\plugin.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($packagedManifest.version -ne $version -or $packagedManifest.update.provider -cne 'github' -or $packagedManifest.update.repository -cne 'mishakov1c/app_shelf_network') { throw 'Packaged version or GitHub update source mismatch.' }
    Write-Host 'PASS: packaged manifest version and GitHub update source'
    $exe = Join-Path $temp 'unpacked\bin\NetworkPlugin.exe'
    $assembly = [Reflection.Assembly]::LoadFrom($exe)
    if ($assembly.GetName().Version.ToString() -cne ($version + '.0') -or [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion -cne ($version + '.0')) { throw 'Executable version mismatch.' }
    $icon = [IO.File]::ReadAllBytes((Join-Path $root 'Assets\NetworkPlugin.ico'))
    $sizes = @(16,20,24,32,48,64,128,256)
    if ($icon.Length -lt 134 -or [BitConverter]::ToUInt16($icon,0) -ne 0 -or [BitConverter]::ToUInt16($icon,2) -ne 1 -or [BitConverter]::ToUInt16($icon,4) -ne 8) { throw 'Invalid ICO directory.' }
    for ($i=0; $i -lt $sizes.Count; $i++) {
        $o = 6 + 16 * $i
        $width = if ($icon[$o] -eq 0) { 256 } else { [int]$icon[$o] }
        $height = if ($icon[$o+1] -eq 0) { 256 } else { [int]$icon[$o+1] }
        $length = [BitConverter]::ToUInt32($icon,$o+8)
        $offset = [BitConverter]::ToUInt32($icon,$o+12)
        if ($width -ne $sizes[$i] -or $height -ne $width -or [BitConverter]::ToUInt16($icon,$o+6) -ne 32 -or $length -eq 0 -or $offset -lt 134 -or ([long]$offset + $length) -gt $icon.Length) { throw 'Invalid ICO frame.' }
    }
    $resource = $assembly.GetManifestResourceStream('AppShelf.Network.Icon.ico')
    if ($null -eq $resource) { throw 'Embedded icon missing.' }
    $memory = New-Object IO.MemoryStream
    try {
        $resource.CopyTo($memory)
        if ([Convert]::ToBase64String($memory.ToArray()) -cne [Convert]::ToBase64String($icon)) { throw 'Embedded ICO differs from source.' }
    } finally { $resource.Dispose(); $memory.Dispose() }
    Write-Host 'PASS: eight ICO frames and byte-identical embedded resource.'
    $targetType = $assembly.GetType('AppShelf.Network.NetworkChecks',$true)
    $parse = $targetType.GetMethod('TryParse',[Reflection.BindingFlags]'Static,NonPublic')
    $targetCases = @(
        @{Mode=''; Target=''; Index=1; Good=$true; Canonical='1.1.1.1'}
        @{Mode='default'; Target=''; Index=2; Good=$true; Canonical='ya.ru'}
        @{Mode='icmp'; Target='192.168.1.1'; Index=1; Good=$true; Canonical='192.168.1.1'}
        @{Mode='icmp'; Target='server.company.local'; Index=2; Good=$true; Canonical='server.company.local'}
        @{Mode='https'; Target='https://example.com/path'; Index=1; Good=$true; Canonical='example.com'}
        @{Mode='tcp'; Target='server.company.local:8443'; Index=2; Good=$true; Canonical='server.company.local'}
        @{Mode='tcp'; Target='[::1]:443'; Index=1; Good=$true; Canonical='::1'}
        @{Mode='https'; Target='http://example.com'; Index=1; Good=$false}
        @{Mode='https'; Target='https://user:secret@example.com'; Index=1; Good=$false}
        @{Mode='https'; Target='https://example.com/#fragment'; Index=1; Good=$false}
        @{Mode='tcp'; Target='example.com'; Index=1; Good=$false}
        @{Mode='tcp'; Target='example.com:65536'; Index=1; Good=$false}
        @{Mode='icmp'; Target='https://example.com'; Index=2; Good=$false}
        @{Mode='icmp'; Target='host name'; Index=2; Good=$false}
        @{Mode='icmp'; Target='example.com/path'; Index=1; Good=$false}
        @{Mode='icmp'; Target='1.1.1.1'; Index=3; Good=$false}
        @{Mode='unknown'; Target='example.com'; Index=1; Good=$false}
    )
    foreach ($case in $targetCases) {
        $arguments = [object[]]@([string]$case.Mode, [string]$case.Target,
            [int]$case.Index, $null, $null)
        $valid = [bool]$parse.Invoke($null, $arguments)
        if ($valid -ne $case.Good) {
            throw ("FAIL: network target '{0}' ({1}) accepted={2}, expected={3}" -f
                $case.Target, $case.Mode, $valid, $case.Good)
        }
        if ($valid) {
            $parsed = $arguments[3]
            $parsedHost = [string]$parsed.GetType().GetField('Host').GetValue($parsed)
            if ($parsedHost -ne $case.Canonical -or $arguments[4] -ne $null) {
                throw "FAIL: normalized network host for $($case.Target)"
            }
        } elseif ([String]::IsNullOrWhiteSpace([string]$arguments[4])) {
            throw 'FAIL: invalid network target has no actionable validation message.'
        }
    }
    Write-Host ("PASS: {0} typed network target validations; no real network checks." -f $targetCases.Count)
    
    
    $type = $assembly.GetType('AppShelf.Network.NetworkSettings',$true)
    $flags = [Reflection.BindingFlags]'Static,Instance,NonPublic'
    $settings = [Activator]::CreateInstance($type,$true)
    $type.GetField('ShowNetwork').SetValue($settings,$false)
    $type.GetField('NetworkCheck1Mode').SetValue($settings,'tcp')
    $type.GetField('NetworkCheck1Target').SetValue($settings,'localhost:443')
    [void]$type.GetMethod('Save',$flags).Invoke($settings.PSObject.BaseObject,[object[]]@([string]$temp))
    $loaded = $type.GetMethod('Load',$flags).Invoke($null,[object[]]@([string]$temp))
    if ($loaded.ShowNetwork -or $loaded.NetworkCheck1Mode -ne 'tcp' -or $loaded.NetworkCheck1Target -ne 'localhost:443') { throw 'Network settings roundtrip failed.' }
    $type.GetField('NetworkCheck1Target').SetValue($settings,'invalid')
    $before = [IO.File]::ReadAllText((Join-Path $temp 'network-settings.json'))
    $failed = $false
    try { [void]$type.GetMethod('Save',$flags).Invoke($settings.PSObject.BaseObject,[object[]]@([string]$temp)) } catch { $failed = $true }
    if (-not $failed -or $before -ne [IO.File]::ReadAllText((Join-Path $temp 'network-settings.json'))) { throw 'Invalid settings replaced saved data.' }
    Write-Host 'PASS: network settings validation and atomic roundtrip'
    $plugin = $assembly.GetType('AppShelf.Network.NetworkPlugin',$true)
    $plugin.GetField('settings',$flags).SetValue($null,$loaded.PSObject.BaseObject)
    $plugin.GetMethod('ResetResults',$flags).Invoke($null,@())
    $widgets = $plugin.GetMethod('Snapshot',$flags).Invoke($null,@())
    if (@($widgets).Count -ne 3 -or @($widgets | Where-Object {$_.Kind -ne 'indicator'}).Count -ne 0) { throw 'Indicator snapshot mismatch.' }
    foreach ($widget in $widgets) {
        if ($widget.Elements[0].Label -ne 'Выключено' -or $widget.Elements[1].CommandId -ne 'open') { throw 'Disabled monitoring or indicator command mismatch.' }
    }
    Write-Host 'PASS: three independent indicator descriptors; no network traffic'
} finally {
    # Loaded assembly is locked by Windows; cleanup best-effort, confined to this unique directory.
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
