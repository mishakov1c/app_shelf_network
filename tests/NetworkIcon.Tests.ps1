# Windows PowerShell 5.1 -STA. Validate the built EXE, Windows Shell and both
# real WPF windows. Diagnostics probes are confined to IPv4 loopback port 9.
param(
    [string]$OutputDirectory = (Join-Path $env:LOCALAPPDATA ('Temp\opencode\NetworkIcon-' + [guid]::NewGuid().ToString('N'))),
    [switch]$CaptureDesktop,
    [switch]$VpnEnabled,
    [switch]$Utf8ParentInput
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core' -or [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Use Windows PowerShell 5.1 -STA.' }
$root = Split-Path $PSScriptRoot -Parent
[void][IO.Directory]::CreateDirectory($OutputDirectory)
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName PresentationFramework
$manifest = Get-Content (Join-Path $root 'plugin.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$package = Join-Path $root ('dist\NetworkPlugin-' + $manifest.version + '.asplugin')
$unpacked = Join-Path $OutputDirectory 'unpacked'
[IO.Compression.ZipFile]::ExtractToDirectory($package, $unpacked)
$exe = Join-Path $unpacked 'bin\NetworkPlugin.exe'
$ico = Join-Path $root 'Assets\NetworkPlugin.ico'
$bytes = [IO.File]::ReadAllBytes($ico)
$sizes = @(16,20,24,32,48,64,128,256)
if ([BitConverter]::ToUInt16($bytes,0) -ne 0 -or [BitConverter]::ToUInt16($bytes,2) -ne 1 -or [BitConverter]::ToUInt16($bytes,4) -ne 8) { throw 'Invalid ICO directory.' }
for ($i=0; $i -lt $sizes.Count; $i++) {
    $offset = 6 + $i * 16
    $size = if ($bytes[$offset] -eq 0) { 256 } else { [int]$bytes[$offset] }
    $height = if ($bytes[$offset+1] -eq 0) { 256 } else { [int]$bytes[$offset+1] }
    if ($size -ne $sizes[$i] -or $height -ne $size -or [BitConverter]::ToUInt16($bytes,$offset+6) -ne 32) { throw 'ICO size or color depth mismatch.' }
}
$assembly = [Reflection.Assembly]::LoadFrom($exe)
$resource = $assembly.GetManifestResourceStream('AppShelf.Network.Icon.ico')
if ($null -eq $resource) { throw 'Managed icon resource missing.' }
try {
    $memory = New-Object IO.MemoryStream
    $resource.CopyTo($memory)
    if ([Convert]::ToBase64String($memory.ToArray()) -cne [Convert]::ToBase64String($bytes)) { throw 'Embedded ICO differs from Assets.' }
} finally { $resource.Dispose() }
$stream = [IO.File]::OpenRead($ico)
try {
    $decoder = New-Object Windows.Media.Imaging.IconBitmapDecoder($stream, [Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat, [Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
    if ($decoder.Frames.Count -ne 8) { throw 'WPF cannot decode all ICO frames.' }
    for ($i=0; $i -lt $sizes.Count; $i++) {
        $frame = @($decoder.Frames | Where-Object { $_.PixelWidth -eq $sizes[$i] -and $_.PixelHeight -eq $sizes[$i] })
        if ($frame.Count -ne 1) { throw 'Missing or duplicate decoded ICO frame size.' }
        $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
        $encoder.Frames.Add($frame[0])
        $file = [IO.File]::Create((Join-Path $OutputDirectory ('frame-' + $sizes[$i] + '.png')))
        try { $encoder.Save($file) } finally { $file.Dispose() }
    }
} finally { $stream.Dispose() }
Write-Host 'PASS: eight 32-bit ICO frames, WPF decoding and byte-identical managed EXE resource.'

Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
public static class NetworkIconNative {
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    public struct SHFILEINFO {
        public IntPtr Icon; public int Index; public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=80)] public string TypeName;
    }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr SHGetFileInfo(string path,uint attributes,out SHFILEINFO info,uint size,uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd,uint msg,IntPtr wparam,IntPtr lparam);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd,out RECT rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd,IntPtr hdc,uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd,uint command);
    [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr hwnd,int id);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd,int index);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key,byte scan,uint flags,UIntPtr extra);
    private static void Screenshot(string path) {
        using(Bitmap bitmap=new Bitmap(GetSystemMetrics(0),GetSystemMetrics(1)))
        using(Graphics graphics=Graphics.FromImage(bitmap)) {
            graphics.CopyFromScreen(0,0,0,0,bitmap.Size);
            bitmap.Save(path,ImageFormat.Png);
        }
    }
    public static void Desktop(IntPtr hwnd,string output) {
        SetForegroundWindow(hwnd);
        System.Threading.Thread.Sleep(600);
        Screenshot(Path.Combine(output,"desktop-taskbar.png"));
        keybd_event(0x12,0,0,UIntPtr.Zero);
        try {
            keybd_event(0x09,0,0,UIntPtr.Zero);
            keybd_event(0x09,0,2,UIntPtr.Zero);
            System.Threading.Thread.Sleep(1000);
            Screenshot(Path.Combine(output,"desktop-alt-tab.png"));
        }
        finally { keybd_event(0x12,0,2,UIntPtr.Zero); }
    }
    public static void Shell(string exe,string output) {
        foreach(bool small in new[]{true,false}) {
            SHFILEINFO info;
            if(SHGetFileInfo(exe,0,out info,(uint)Marshal.SizeOf(typeof(SHFILEINFO)),0x100u | (small?1u:0u))==IntPtr.Zero || info.Icon==IntPtr.Zero)
                throw new Exception("Explorer Shell icon extraction failed.");
            try { using(Bitmap bitmap=Icon.FromHandle(info.Icon).ToBitmap()) bitmap.Save(Path.Combine(output,small?"explorer-small.png":"explorer-large.png"),ImageFormat.Png); }
            finally { DestroyIcon(info.Icon); }
        }
    }
    public static void SafeDialogButton(IntPtr dialog,int id) {
        if(id!=7 && id!=1 && id!=2) throw new Exception("Test may only decline or dismiss.");
        IntPtr button=GetDlgItem(dialog,id);
        if(button!=IntPtr.Zero) SendMessage(button,0x00F5,IntPtr.Zero,IntPtr.Zero);
        else SendMessage(dialog,0x0111,(IntPtr)id,IntPtr.Zero);
    }
    public static void Window(IntPtr hwnd,string output,string name) {
        if(GetWindow(hwnd,4)!=IntPtr.Zero || (GetWindowLong(hwnd,-20)&0x80)!=0)
            throw new Exception("Window is owned or excluded from Alt+Tab/taskbar.");
        for(int size=0;size<2;size++) {
            IntPtr icon=SendMessage(hwnd,0x007F,(IntPtr)size,IntPtr.Zero);
            if(icon==IntPtr.Zero) throw new Exception("WPF native window icon is missing: "+size);
            using(Bitmap bitmap=Icon.FromHandle(icon).ToBitmap())
                bitmap.Save(Path.Combine(output,name+(size==0?"-small.png":"-large.png")),ImageFormat.Png);
        }
        RECT rect;
        if(!GetWindowRect(hwnd,out rect)) throw new Exception("Cannot get window bounds.");
        using(Bitmap bitmap=new Bitmap(rect.Right-rect.Left,rect.Bottom-rect.Top))
        using(Graphics graphics=Graphics.FromImage(bitmap)) {
            IntPtr hdc=graphics.GetHdc();
            try { if(!PrintWindow(hwnd,hdc,2)) throw new Exception("Cannot capture window."); }
            finally { graphics.ReleaseHdc(hdc); }
            bitmap.Save(Path.Combine(output,name+".png"),ImageFormat.Png);
        }
    }
}
'@
[NetworkIconNative]::Shell($exe,$OutputDirectory)
Write-Host 'PASS: Windows Explorer Shell extracts small and large icons from the packaged EXE.'

$data = Join-Path $OutputDirectory 'data'
[void][IO.Directory]::CreateDirectory($data)
[IO.File]::WriteAllText((Join-Path $data 'network-settings.json'), '{"showNetwork":false,"networkCheck1Mode":"tcp","networkCheck1Target":"127.0.0.1:9","networkCheck2Mode":"tcp","networkCheck2Target":"127.0.0.1:9"}')
if ($VpnEnabled) {
    [IO.File]::WriteAllText((Join-Path $data 'network-settings.json'), '{"showNetwork":false,"networkCheck1Mode":"tcp","networkCheck1Target":"127.0.0.1:9","networkCheck2Mode":"tcp","networkCheck2Target":"127.0.0.1:9","vpnEnabled":true,"showVpnIndicator":true,"vpnAdapterIds":["11111111-1111-1111-1111-111111111111"]}')
}
$p = New-Object Diagnostics.Process
$p.StartInfo.FileName = $exe
$p.StartInfo.UseShellExecute = $false
$p.StartInfo.CreateNoWindow = $true
$p.StartInfo.RedirectStandardInput = $true
$p.StartInfo.RedirectStandardOutput = $true
$p.StartInfo.RedirectStandardError = $true
$stderrTask = $null
$currentRequest = 'not sent'
$originalInputEncoding = [Console]::InputEncoding
$started = $false
function Ipc-Failure([string]$Reason) {
    $exited = $p.WaitForExit(1000)
    $state = if ($exited) { 'exit code ' + $p.ExitCode } else { 'still running' }
    $stderr = if ($null -ne $stderrTask -and $exited -and $stderrTask.Wait(1000)) { $stderrTask.Result } else { '<not yet complete>' }
    throw ($Reason + ' Request: ' + $script:currentRequest + '; process: ' + $state + '; stderr: ' + $stderr)
}
function Read-Exact([IO.Stream]$Stream,[int]$Count) {
    $buffer = New-Object byte[] $Count
    $offset = 0
    while ($offset -lt $Count) {
        $task = $Stream.ReadAsync($buffer,$offset,$Count-$offset)
        if (-not $task.Wait(10000)) { Ipc-Failure 'IPC timed out.' }
        if ($task.Result -le 0) { Ipc-Failure 'IPC stream closed.' }
        $offset += $task.Result
    }
    return ,$buffer
}
$requestId = 0
function Exchange([hashtable]$Request) {
    $script:requestId++
    $Request.apiVersion = 1
    $Request.id = $script:requestId
    $script:currentRequest = [string]$Request.method + ' #' + $Request.id
    $payload = [Text.Encoding]::UTF8.GetBytes(($Request | ConvertTo-Json -Compress))
    $send = $p.StandardInput.BaseStream
    $header = [BitConverter]::GetBytes([int]$payload.Length)
    try { $send.Write($header,0,4); $send.Write($payload,0,$payload.Length); $send.Flush() }
    catch { Ipc-Failure ('IPC write failed: ' + $_.Exception.Message) }
    $receive = $p.StandardOutput.BaseStream
    $length = [BitConverter]::ToInt32((Read-Exact $receive 4),0)
    if ($length -lt 1 -or $length -gt 65536) { throw 'Invalid IPC frame.' }
    $reply = [Text.Encoding]::UTF8.GetString((Read-Exact $receive $length)) | ConvertFrom-Json
    if (-not $reply.ok -or $reply.id -ne $Request.id) { Ipc-Failure ('Plugin request failed: ' + $reply.error) }
}
try {
    # Reproduce the GitHub Windows runner's BOM-emitting parent encoding on any workstation.
    if ($Utf8ParentInput) { [Console]::InputEncoding = New-Object Text.UTF8Encoding($true) }
    $parentInputEncoding = [Console]::InputEncoding
    # Framework Process.Start creates an AutoFlush StreamWriter using Console.InputEncoding.
    # A BOM-emitting UTF-8 parent inserts EF-BB-BF BEFORE any BaseStream frame writes.
    # Start with BOM-less UTF-8, then immediately restore the parent. Do not relax framing.
    try {
        [Console]::InputEncoding = New-Object Text.UTF8Encoding($false)
        $started = $p.Start()
    } finally { [Console]::InputEncoding = $parentInputEncoding }
    if (-not $started) { throw 'Plugin process did not start.' }
    $stderrTask = $p.StandardError.ReadToEndAsync()
    if ($p.StandardInput.Encoding.GetPreamble().Length -ne 0) { throw 'Binary IPC writer can emit an encoding preamble.' }
    if ([Console]::InputEncoding.CodePage -ne $parentInputEncoding.CodePage -or
        [BitConverter]::ToString([Console]::InputEncoding.GetPreamble()) -cne [BitConverter]::ToString($parentInputEncoding.GetPreamble())) {
        throw 'Parent console input encoding was not restored.'
    }
    Write-Host ('PASS: BOM-free binary IPC writer; parent input code page ' + $parentInputEncoding.CodePage +
        ', preamble bytes ' + $parentInputEncoding.GetPreamble().Length + ', restored after launch.')
    Exchange @{method='hello';pluginId='appshelf.network';pluginVersion=[string]$manifest.version;locale='ru-RU';dataDirectory=$data}
    Exchange @{method='invoke';commandId='open'}
    Exchange @{method='settings'}
    Add-Type -AssemblyName UIAutomationClient
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $windows = @()
    do {
        Start-Sleep -Milliseconds 150
        $desktop = [Windows.Automation.AutomationElement]::RootElement
        $condition = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty,$p.Id)
        $windows = @($desktop.FindAll([Windows.Automation.TreeScope]::Children,$condition))
    } while ($windows.Count -lt 2 -and $clock.ElapsedMilliseconds -lt 15000)
    if ($windows.Count -ne 2) { throw 'Both actual plugin windows were not found.' }
    # Exercise singleton commands in the actual process, not a reconstructed window.
    Exchange @{method='invoke';commandId='open'}
    Exchange @{method='settings'}
    if ($desktop.FindAll([Windows.Automation.TreeScope]::Children,$condition).Count -ne 2) { throw 'Duplicate plugin windows.' }
    foreach ($window in $windows) {
        $handle = [IntPtr]$window.Current.NativeWindowHandle
        $name = if ($window.Current.Name.Contains([string][char]0x0414)) { 'diagnostics' } else { 'settings' }
        [NetworkIconNative]::Window($handle,$OutputDirectory,$name)
        $transform = [Windows.Automation.TransformPattern]$window.GetCurrentPattern([Windows.Automation.TransformPattern]::Pattern)
        foreach ($size in @(@(520,400),@(790,640),@(1100,780))) {
            $transform.Resize($size[0],$size[1])
            Start-Sleep -Milliseconds 200
            $buttons = $window.FindAll([Windows.Automation.TreeScope]::Descendants,
                (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Button)))
            foreach ($button in $buttons) {
                # Settings content intentionally scrolls/clips at its viewport.
                # Only the fixed footer must remain wholly inside the window.
                if ($name -eq 'settings' -and $button.Current.Name -notin @('Сохранить','Отмена')) { continue }
                $rect = $button.Current.BoundingRectangle
                $bounds = $window.Current.BoundingRectangle
                if (-not $button.Current.IsOffscreen -and $rect.Width -gt 0 -and
                    ($rect.Left -lt $bounds.Left -or $rect.Right -gt $bounds.Right + 2 -or $rect.Bottom -gt $bounds.Bottom + 2)) {
                    throw ('Visible button clipped: ' + $button.Current.Name)
                }
            }
            [NetworkIconNative]::Window($handle,$OutputDirectory,($name+'-'+$size[0]+'x'+$size[1]))
        }
        if ($name -eq 'settings') {
            $scrollers = $window.FindAll([Windows.Automation.TreeScope]::Descendants,
                (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::IsScrollPatternAvailableProperty,$true)))
            $scrolled = $false
            foreach ($element in $scrollers) {
                $pattern = [Windows.Automation.ScrollPattern]$element.GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern)
                if (-not $pattern.Current.VerticallyScrollable) { continue }
                $pattern.SetScrollPercent(-1,100)
                Start-Sleep -Milliseconds 150
                if ($pattern.Current.VerticalScrollPercent -lt 90) { throw 'Settings scroll did not reach VPN lists.' }
                [NetworkIconNative]::Window($handle,$OutputDirectory,'settings-scrolled')
                $pattern.SetScrollPercent(-1,0)
                $scrolled = $true
                break
            }
            if (-not $scrolled) { throw 'Settings vertical scrolling is unavailable.' }
        }
        if ($name -eq 'diagnostics') { $diagnostics = $window } else { $settingsWindow = $window }
    }
    Write-Host 'PASS: both real EXE windows provide small/large native icons and are eligible for taskbar and Alt+Tab.'
    $editCondition = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Edit)
    $report = $diagnostics.FindFirst([Windows.Automation.TreeScope]::Descendants,$editCondition)
    $clock.Restart()
    do {
        $value = ([Windows.Automation.ValuePattern]$report.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).Current.Value
        if ($value.Contains('Системный прокси WinINET')) { break }
        Start-Sleep -Milliseconds 100
    } while ($clock.ElapsedMilliseconds -lt 15000)
    if ([regex]::Matches($value,'Системный прокси WinINET').Count -ne 1 -or [regex]::Matches($value,'DNS-серверы активных').Count -ne 1) {
        throw 'Common diagnostics are missing or duplicated.'
    }
    if ($VpnEnabled -ne $value.Contains('VPN / TUN')) { throw 'VPN report enable/disable mismatch.' }
    Write-Host 'PASS: real EXE report has one proxy/DNS section and the expected optional VPN appendix; three sizes and singleton windows checked.'
    if ($VpnEnabled) {
        # Close settings before recovery; decline only the initial confirmation.
        ([Windows.Automation.WindowPattern]$settingsWindow.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern)).Close()
        $clock.Restart()
        do {
            $remaining = $desktop.FindAll([Windows.Automation.TreeScope]::Children,$condition)
            if ($remaining.Count -eq 1) { break }
            Start-Sleep -Milliseconds 100
        } while ($clock.ElapsedMilliseconds -lt 5000)
        if ($remaining.Count -ne 1) { throw 'Settings window did not close before recovery decline test.' }
        $recover = $diagnostics.FindFirst([Windows.Automation.TreeScope]::Descendants,
            (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty,'Восстановить сеть пошагово…')))
        if ($null -eq $recover -or -not $recover.Current.IsEnabled) { throw 'Recovery button missing or disabled.' }
        Add-Type -ReferencedAssemblies UIAutomationClient,UIAutomationTypes -TypeDefinition @'
using System;
using System.Threading;
using System.Windows.Automation;
public static class InvokeRecoveryForDecline {
 public static void Invoke(AutomationElement element) {
  ThreadPool.QueueUserWorkItem(delegate {
   try { ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); } catch(ElementNotAvailableException) {}
  });
 }
}
'@
        [InvokeRecoveryForDecline]::Invoke($recover)
        $clock.Restart(); $no = $null
        do {
            $dialogCondition = New-Object Windows.Automation.AndCondition($condition,
                (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty,'NetworkPlugin — VPN / восстановление')))
            $confirmationWindow = $desktop.FindFirst([Windows.Automation.TreeScope]::Descendants,$dialogCondition)
            if ($null -ne $confirmationWindow) {
                $no = $confirmationWindow.FindFirst([Windows.Automation.TreeScope]::Descendants,
                    (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty,'7')))
            }
            if ($null -eq $no) { Start-Sleep -Milliseconds 100 }
        } while ($null -eq $no -and $clock.ElapsedMilliseconds -lt 10000)
        if ($null -eq $no) {
            foreach ($candidate in $desktop.FindAll([Windows.Automation.TreeScope]::Children,$condition)) {
                Write-Host ('Observed dialog/window: ' + $candidate.Current.Name)
                foreach ($element in $candidate.FindAll([Windows.Automation.TreeScope]::Children,[Windows.Automation.Condition]::TrueCondition)) {
                    Write-Host ('  '+$element.Current.ControlType.ProgrammaticName+' '+$element.Current.AutomationId+' '+$element.Current.Name)
                }
            }
            throw 'Initial recovery confirmation not found; no consent was granted.'
        }
        [NetworkIconNative]::SafeDialogButton([IntPtr]$confirmationWindow.Current.NativeWindowHandle,7)
        # The result dialog is informational; dismiss OK, never any Yes button.
        $clock.Restart(); $ok = $null
        do {
            $dialogCondition = New-Object Windows.Automation.AndCondition($condition,
                (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty,'NetworkPlugin — результат')))
            $resultWindow = $desktop.FindFirst([Windows.Automation.TreeScope]::Descendants,$dialogCondition)
            if ($null -ne $resultWindow) {
                $ok = $resultWindow.FindFirst([Windows.Automation.TreeScope]::Descendants,
                    (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty,'2')))
                if ($null -eq $ok) { $ok = $resultWindow.FindFirst([Windows.Automation.TreeScope]::Descendants,
                    (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty,'1'))) }
            }
            if ($null -eq $ok) { Start-Sleep -Milliseconds 100 }
        } while ($null -eq $ok -and $clock.ElapsedMilliseconds -lt 10000)
        if ($null -eq $ok) { throw 'Declined recovery result not found.' }
        [NetworkIconNative]::SafeDialogButton([IntPtr]$resultWindow.Current.NativeWindowHandle,[int]$ok.Current.AutomationId)
        Write-Host 'PASS: actual EXE recovery confirmation declined through UI Automation; no system change authorized.'
    }
    if ($CaptureDesktop) { [NetworkIconNative]::Desktop($handle,$OutputDirectory) }
    Exchange @{method='shutdown'}
    if (-not $p.WaitForExit(5000) -or $p.ExitCode -ne 0) { throw 'Plugin did not shut down cleanly.' }
} finally {
    if ($started -and -not $p.HasExited) { $p.Kill(); [void]$p.WaitForExit(5000) }
    $p.Dispose()
    [Console]::InputEncoding = $originalInputEncoding
}
Write-Host ('Evidence: ' + $OutputDirectory)
