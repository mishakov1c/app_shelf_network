# Safe offline network plugin regression. No external probes or system changes.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Add-Type -AssemblyName System.IO.Compression.FileSystem
$temp = Join-Path ([IO.Path]::GetTempPath()) ('AppShelf-NetworkTests-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temp)
try {
    & (Join-Path $root 'Build.ps1') -OutputDirectory $temp
    $package = Join-Path $temp 'NetworkPlugin-1.1.0.asplugin'
    [IO.Compression.ZipFile]::ExtractToDirectory($package,(Join-Path $temp 'unpacked'))
    $exe = Join-Path $temp 'unpacked\bin\NetworkPlugin.exe'
    $assembly = [Reflection.Assembly]::LoadFrom($exe)
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
