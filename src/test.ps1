# Automated tests for IntunePackageGrabber.exe: simulates IME extracting packages into a fake IMECache.
# Usage: powershell -ExecutionPolicy Bypass -File test.ps1 [-Work <work folder>] [-Exe <exe>]
param(
    [string]$Work = (Join-Path $env:TEMP 'IPG-tests'),
    [string]$Exe = (Join-Path $PSScriptRoot '..\IntunePackageGrabber.exe')
)
$ErrorActionPreference = 'Stop'
# Fake AppWorkload.log with the policy of a known app (for the test with name/setup read from the policy)
$KnownAppId = '11111111-2222-3333-4444-555555555555'
$KnownAppName = 'Contoso Test App'
$imeLogs = Join-Path $Work 'ime-logs'
New-Item -ItemType Directory $imeLogs -Force | Out-Null
$detection = ConvertTo-Json -Compress @(
    @{ DetectionType = 1; DetectionText = (ConvertTo-Json -Compress @{ ProductCode = '{AAAAAAAA-0000-0000-0000-000000000001}'; ProductVersion = '1.2'; ProductVersionOperator = 5 }) },
    @{ DetectionType = 3; DetectionText = (ConvertTo-Json -Compress @{ EnforceSignatureCheck = 0; RunAs32Bit = 0; ScriptBody = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('exit 0')) }) }
)
$policy = '[' + (ConvertTo-Json -Compress @{
    Id = $KnownAppId; Name = $KnownAppName; Version = 1; SetUpFilePath = 'install.ps1'
    InstallCommandLine = 'powershell.exe -ExecutionPolicy Bypass -File install.ps1'
    DetectionRule = $detection; ReturnCodes = '[{"ReturnCode":0,"Type":1},{"ReturnCode":3010,"Type":2}]'
}) + ']'
Set-Content -LiteralPath (Join-Path $imeLogs 'AppWorkload.log') -Encoding UTF8 -Value "<![LOG[Get policies = $policy]LOG]!><time=`"00:00:00.000`" date=`"1-1-2026`" component=`"AppWorkload`">"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$results = New-Object System.Collections.Generic.List[object]
$settings = Join-Path $env:APPDATA 'IntunePackageGrabber\settings.txt'
$settingsBackup = if (Test-Path $settings) { Get-Content $settings -Raw } else { $null }

function New-Scenario([string]$Name) {
    $root = Join-Path $Work $Name
    if (Test-Path $root) { cmd /c rd /s /q "\\?\$root" }
    New-Item -ItemType Directory -Path "$root\cache", "$root\out" -Force | Out-Null
    [pscustomobject]@{ Name = $Name; Root = $root; Cache = "$root\cache"; Out = "$root\out"; Log = "$root\grabber.log" }
}

function Start-Grabber($S, [int]$ExitAfter, [switch]$NoListen, [string]$Dest) {
    if (-not $Dest) { $Dest = $S.Out }
    $a = "--cache `"$($S.Cache)`" --dest `"$Dest`" --log `"$($S.Log)`" --imelogs `"$imeLogs`" --exit-after $ExitAfter"
    if (-not $NoListen) { $a += ' --listen' }
    Start-Process $Exe -ArgumentList $a -PassThru
}

function Get-Log($S) { if (Test-Path $S.Log) { Get-Content $S.Log -Raw } else { '' } }

function Wait-Log($S, [string]$Pattern, [int]$TimeoutSec = 60) {
    $end = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $end) { if ((Get-Log $S) -match $Pattern) { return $true }; Start-Sleep -Milliseconds 100 }
    return $false
}

function Get-Captures($S, [string]$Dest) {
    if (-not $Dest) { $Dest = $S.Out }
    @(Get-ChildItem -LiteralPath $Dest -Directory -ErrorAction SilentlyContinue)
}

function Compare-Content([string]$Expected, [string]$Actual) {
    $e = @(Get-ChildItem -LiteralPath $Expected -Recurse -File | ForEach-Object { '{0}|{1}' -f $_.FullName.Substring($Expected.Length), (Get-FileHash -LiteralPath $_.FullName -Algorithm MD5).Hash })
    $a = @(Get-ChildItem -LiteralPath $Actual -Recurse -File | ForEach-Object { '{0}|{1}' -f $_.FullName.Substring($Actual.Length), (Get-FileHash -LiteralPath $_.FullName -Algorithm MD5).Hash })
    -not (Compare-Object $e $a)
}

function Test-Intunewin([string]$Path, [string]$SetupFile) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entry = $zip.GetEntry('IntuneWinPackage/Metadata/Detection.xml')
        if (-not $entry -or -not $zip.GetEntry('IntuneWinPackage/Contents/IntunePackage.intunewin')) { return $false }
        $reader = New-Object IO.StreamReader $entry.Open()
        $xml = [xml]$reader.ReadToEnd(); $reader.Close()
        return $xml.ApplicationInfo.SetupFile -eq $SetupFile
    }
    finally { $zip.Dispose() }
}

function Assert($Scenario, [string]$Check, [bool]$Ok, [string]$Detail = '') {
    $results.Add([pscustomobject]@{ Scenario = $Scenario; Check = $Check; Result = $(if ($Ok) { 'PASS' } else { 'FAIL' }); Detail = $Detail })
}

function New-Package([string]$Path, [hashtable]$Files) {
    foreach ($k in $Files.Keys) {
        $f = Join-Path $Path $k
        New-Item -ItemType Directory -Path (Split-Path $f) -Force | Out-Null
        [IO.File]::WriteAllBytes($f, [byte[]]$Files[$k])
    }
}

$rng = New-Object Random
function Get-RandomBytes([int]$Size) { $b = New-Object byte[] $Size; $rng.NextBytes($b); , $b }

function Remove-Tree([string]$Path) { if (Test-Path -LiteralPath $Path) { cmd /c rd /s /q "\\?\$Path" 2>$null } }

# A test script error must not stop the suite: it is recorded as FAIL
trap { Assert 'TEST-SCRIPT' "line $($_.InvocationInfo.ScriptLineNumber)" $false $_.Exception.Message; continue }

# ---------------------------------------------------------------------------------------------
# T1: progressive extraction, with a large file kept open for writing for ~8 seconds
$s = New-Scenario 'T1-progressive-extraction'
$p = Start-Grabber $s -ExitAfter 40
Start-Sleep 2
$pkg = Join-Path $s.Cache "$KnownAppId`_1"
New-Item -ItemType Directory $pkg -Force | Out-Null
New-Package $pkg @{ 'install.ps1' = [Text.Encoding]::ASCII.GetBytes('exit 0'); 'AppDeployToolkit\a.ps1' = (Get-RandomBytes 1000) }
New-Item -ItemType Directory "$pkg\Files" -Force | Out-Null
$fs = [IO.File]::Open("$pkg\Files\big.bin", 'Create', 'Write', 'Read')
for ($i = 0; $i -lt 8; $i++) { $chunk = Get-RandomBytes 1MB; $fs.Write($chunk, 0, $chunk.Length); Start-Sleep 1 }
$fs.Close()
New-Package $pkg @{ 'SupportFiles\last.txt' = [Text.Encoding]::ASCII.GetBytes('last file') }
$p.WaitForExit(90000) | Out-Null
$cap = Get-Captures $s
Assert $s.Name 'Exactly one capture' ($cap.Count -eq 1) "$($cap.Count) captures"
if ($cap.Count -ge 1) {
    Assert $s.Name 'Identical content (MD5 hash)' (Compare-Content $pkg (Join-Path $cap[0].FullName 'Content'))
    Assert $s.Name 'big.bin complete (8 MB)' ((Get-Item -LiteralPath (Join-Path $cap[0].FullName 'Content\Files\big.bin')).Length -eq 8MB)
    $iw = Get-ChildItem -LiteralPath $cap[0].FullName -Filter *.intunewin
    Assert $s.Name 'Valid .intunewin with the policy SetupFile (install.ps1)' ($iw -and (Test-Intunewin $iw.FullName 'install.ps1'))
    Assert $s.Name 'App name from the IME policy' ($cap[0].Name -like "$KnownAppName`_v1_*") $cap[0].Name
    Assert $s.Name 'app-info.txt and policy.json' ((Test-Path -LiteralPath "$($cap[0].FullName)\app-info.txt") -and (Test-Path -LiteralPath "$($cap[0].FullName)\policy.json"))
    $info = Get-Content -LiteralPath "$($cap[0].FullName)\app-info.txt" -Raw
    Assert $s.Name 'Decoded detection rules and return codes' (($info -match 'MSI \{AAAAAAAA-0000-0000-0000-000000000001\} version >= 1\.2') -and ($info -match 'Return codes\s+: 0 success, 3010 soft reboot'))
    Assert $s.Name 'Detection script saved' ((Get-Content -LiteralPath "$($cap[0].FullName)\detection.ps1" -Raw -ErrorAction SilentlyContinue) -eq 'exit 0')
}

# ---------------------------------------------------------------------------------------------
# T2: parallel packages (same app in two versions + unknown app) and folders to ignore
$s = New-Scenario 'T2-parallel'
New-Item -ItemType Directory "$($s.Cache)\HealthScripts", "$($s.Cache)\NotAGuid" -Force | Out-Null
New-Package "$($s.Cache)\HealthScripts" @{ 'x.ps1' = (Get-RandomBytes 10) }
$unknown = [guid]::NewGuid().ToString()
$p = Start-Grabber $s -ExitAfter 30
Start-Sleep 2
New-Package (Join-Path $s.Cache "$KnownAppId`_1") @{ 'install.ps1' = (Get-RandomBytes 100); 'Files\a.bin' = (Get-RandomBytes 3MB) }
New-Package (Join-Path $s.Cache "$KnownAppId`_2") @{ 'install.ps1' = (Get-RandomBytes 100); 'Files\b.bin' = (Get-RandomBytes 2MB) }
New-Package (Join-Path $s.Cache "$unknown`_1") @{ 'setup.exe' = (Get-RandomBytes 5000); 'data\c.bin' = (Get-RandomBytes 1MB) }
$p.WaitForExit(90000) | Out-Null
$cap = Get-Captures $s
Assert $s.Name '3 captures (HealthScripts and non-GUID ignored)' ($cap.Count -eq 3) (($cap | ForEach-Object Name) -join ', ')
Assert $s.Name 'Separate folders for v1 and v2 of the same app' ((@($cap | Where-Object Name -like '*_v1_*').Count -ge 2) -and (@($cap | Where-Object Name -like '*_v2_*').Count -eq 1))
$u = $cap | Where-Object Name -like "$unknown*"
Assert $s.Name 'Unknown app: name = App ID' ([bool]$u)
if ($u) {
    $iw = Get-ChildItem -LiteralPath $u.FullName -Filter *.intunewin
    Assert $s.Name 'Unknown app: setup guessed (setup.exe) and valid .intunewin' ($iw -and (Test-Intunewin $iw.FullName 'setup.exe'))
}
foreach ($c in $cap) {
    $srcName = if ($c.Name -like "$unknown*") { "$unknown`_1" } elseif ($c.Name -like '*_v2_*') { "$KnownAppId`_2" } else { "$KnownAppId`_1" }
    Assert $s.Name "Identical content: $($c.Name)" (Compare-Content (Join-Path $s.Cache $srcName) (Join-Path $c.FullName 'Content'))
}

# ---------------------------------------------------------------------------------------------
# T3: folder removed by IME before the extraction is stable
$s = New-Scenario 'T3-removed-before'
$p = Start-Grabber $s -ExitAfter 15
Start-Sleep 2
$pkg = Join-Path $s.Cache "$([guid]::NewGuid())_1"
New-Package $pkg @{ 'setup.exe' = (Get-RandomBytes 100) }
Start-Sleep 1
Remove-Tree $pkg
$p.WaitForExit(60000) | Out-Null
Assert $s.Name 'No capture' ((Get-Captures $s).Count -eq 0)
Assert $s.Name 'Log: folder removed' ((Get-Log $s) -match 'removed by IME before the copy')

# ---------------------------------------------------------------------------------------------
# T4: folder deleted during the copy -> handled error, no crash
$s = New-Scenario 'T4-removed-during-copy'
$p = Start-Grabber $s -ExitAfter 25
Start-Sleep 2
$pkg = Join-Path $s.Cache "$([guid]::NewGuid())_1"
$chunk = Get-RandomBytes 1MB
New-Item -ItemType Directory "$pkg\Files" -Force | Out-Null
[IO.File]::WriteAllBytes("$pkg\setup.exe", (Get-RandomBytes 100))
for ($i = 0; $i -lt 400; $i++) { [IO.File]::WriteAllBytes("$pkg\Files\f$i.bin", $chunk) }
$started = Wait-Log $s 'Capturing ' 30
Remove-Tree $pkg
$p.WaitForExit(60000) | Out-Null
$log = Get-Log $s
Assert $s.Name 'Copy started' $started
Assert $s.Name 'Error logged, process exited normally' (($log -match 'ERROR') -and ($log -match 'Automatic exit')) (($log -split "`r?`n" | Select-String 'ERROR' | Select-Object -First 1) -join '')

# ---------------------------------------------------------------------------------------------
# T5: retry - IME removes the captured folder and recreates it
$s = New-Scenario 'T5-retry'
$p = Start-Grabber $s -ExitAfter 30
Start-Sleep 2
$pkg = Join-Path $s.Cache "$([guid]::NewGuid())_1"
New-Package $pkg @{ 'setup.exe' = (Get-RandomBytes 100) }
Wait-Log $s 'created and verified' 30 | Out-Null
Remove-Tree $pkg
Start-Sleep 2
New-Package $pkg @{ 'setup.exe' = (Get-RandomBytes 200) }
$p.WaitForExit(90000) | Out-Null
Assert $s.Name 'Captured again after reappearing (2 captures)' ((Get-Captures $s).Count -eq 2) "$((Get-Captures $s).Count) captures"

# ---------------------------------------------------------------------------------------------
# T6: paths over 260 characters (long destination + deep subfolders)
$s = New-Scenario 'T6-long-paths'
$longDest = Join-Path $s.Out ('Folder with a very long name to simulate OneDrive - Company Name\' * 2 + 'Archive of captured Intune packages')
$p = Start-Grabber $s -ExitAfter 30 -Dest $longDest
Start-Sleep 2
$pkg = Join-Path $s.Cache "$([guid]::NewGuid())_1"
$deep = 'Files\' + ('deep_subfolder_level_\' * 4) + 'package_file_with_a_long_name.bin'
New-Package $pkg @{ 'setup.exe' = (Get-RandomBytes 100); $deep = (Get-RandomBytes 1000) }
$p.WaitForExit(90000) | Out-Null
$cap = Get-Captures $s -Dest $longDest
$maxLen = if ($cap) { (Get-ChildItem -LiteralPath "\\?\$($cap[0].FullName)" -Recurse -File | ForEach-Object { $_.FullName.Length - 4 } | Measure-Object -Maximum).Maximum } else { 0 }
Assert $s.Name "Capture with path > 260 characters" ($cap.Count -eq 1 -and $maxLen -gt 260 -and (Get-Log $s) -match 'created and verified') "max $maxLen characters"

# ---------------------------------------------------------------------------------------------
# T7: IMECache missing at startup, created later
$s = New-Scenario 'T7-cache-created-later'
Remove-Item $s.Cache -Force
$p = Start-Grabber $s -ExitAfter 20
Start-Sleep 3
New-Package (Join-Path $s.Cache "$([guid]::NewGuid())_1") @{ 'setup.exe' = (Get-RandomBytes 100) }
$p.WaitForExit(60000) | Out-Null
Assert $s.Name 'Capture after IMECache is created' ((Get-Captures $s).Count -eq 1)

# ---------------------------------------------------------------------------------------------
# T8: no setup file found -> content saved, .intunewin not created, clear message
$s = New-Scenario 'T8-unknown-setup'
$p = Start-Grabber $s -ExitAfter 15
Start-Sleep 2
New-Package (Join-Path $s.Cache "$([guid]::NewGuid())_1") @{ 'readme.txt' = (Get-RandomBytes 100) }
$p.WaitForExit(60000) | Out-Null
$cap = Get-Captures $s
Assert $s.Name 'Content saved' ($cap.Count -eq 1 -and (Test-Path -LiteralPath "$($cap[0].FullName)\Content\readme.txt"))
Assert $s.Name 'Message: setup file not determined' ((Get-Log $s) -match 'setup file not determined')

# ---------------------------------------------------------------------------------------------
# T9: Listen / Stop buttons (UI Automation)
$s = New-Scenario 'T9-buttons'
$p = Start-Grabber $s -ExitAfter 45 -NoListen
$win = $null
for ($i = 0; $i -lt 50 -and -not $win; $i++) {
    Start-Sleep -Milliseconds 200
    $p.Refresh()
    if ($p.MainWindowHandle -ne [IntPtr]::Zero) { $win = [Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle) }
}
Assert $s.Name 'Window found via UI Automation' ([bool]$win)
Add-Type -Namespace IPG -Name Native -MemberDefinition '[DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);'
# Real button click (BM_CLICK on the native handle found by name via UI Automation)
function Invoke-Button([string]$Text) {
    $btn = $win.FindAll('Descendants', [Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name -like "*$Text" } | Select-Object -First 1
    [void][IPG.Native]::SendMessage([IntPtr]$btn.Current.NativeWindowHandle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)
}
New-Package (Join-Path $s.Cache "$([guid]::NewGuid())_1") @{ 'setup.exe' = (Get-RandomBytes 100) }
Start-Sleep 6
Assert $s.Name 'Before Listen: no capture' ((Get-Captures $s).Count -eq 0)
Invoke-Button 'Listen'
Assert $s.Name 'After Listen: existing folder captured' (Wait-Log $s 'created and verified' 30)
Invoke-Button 'Stop'
Start-Sleep 1
New-Package (Join-Path $s.Cache "$([guid]::NewGuid())_1") @{ 'setup.exe' = (Get-RandomBytes 100) }
Start-Sleep 6
Assert $s.Name 'After Stop: new folder not captured' ((Get-Captures $s).Count -eq 1)
Invoke-Button 'Listen'
Start-Sleep 8
Assert $s.Name 'Listen again: second folder captured too' ((Get-Captures $s).Count -eq 2)
$p.WaitForExit(90000) | Out-Null

# ---------------------------------------------------------------------------------------------
# Helpers for the tab context and diagnostics tests: elements by name (wildcards), modal dialogs answered with WM_COMMAND
Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
namespace IPG
{
    public static class Win
    {
        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] static extern IntPtr GetDlgItem(IntPtr h, int id);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        // Visible dialog (MessageBox) of the process
        public static IntPtr FindDialog(int pid)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((h, l) =>
            {
                uint p;
                GetWindowThreadProcessId(h, out p);
                if (p != pid || !IsWindowVisible(h)) return true;
                var sb = new StringBuilder(64);
                GetClassName(h, sb, 64);
                if (sb.ToString() != "#32770") return true;
                found = h;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        // Text of a MessageBox (static control 0xFFFF)
        public static string DialogText(IntPtr h)
        {
            var sb = new StringBuilder(4096);
            GetWindowText(GetDlgItem(h, 0xFFFF), sb, 4096);
            return sb.ToString();
        }

        // ListView rows of another process: UI Automation does not expose the rows of the WinForms ListView,
        // so the LVM messages are sent with an LVITEM (x64 layout, 88 bytes) and a text buffer allocated in that process
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);

        // WM_SETTEXT: the WinForms TextBox is a generic pane for UI Automation, without ValuePattern
        public static void SetText(IntPtr h, string text) { SendMessage(h, 0x000C, IntPtr.Zero, text); }

        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] static extern IntPtr VirtualAllocEx(IntPtr p, IntPtr addr, IntPtr size, uint type, uint protect);
        [DllImport("kernel32.dll")] static extern bool VirtualFreeEx(IntPtr p, IntPtr addr, IntPtr size, uint type);
        [DllImport("kernel32.dll")] static extern bool WriteProcessMemory(IntPtr p, IntPtr addr, byte[] buf, IntPtr size, out IntPtr done);
        [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(IntPtr p, IntPtr addr, byte[] buf, IntPtr size, out IntPtr done);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        const int ItemSize = 88, TextChars = 512;

        static T WithRemoteItem<T>(IntPtr list, Func<IntPtr, IntPtr, T> action)
        {
            uint pid;
            GetWindowThreadProcessId(list, out pid);
            IntPtr process = OpenProcess(0x0008 | 0x0010 | 0x0020, false, pid);
            IntPtr remote = VirtualAllocEx(process, IntPtr.Zero, (IntPtr)(ItemSize + TextChars * 2), 0x3000, 0x04);
            try { return action(process, remote); }
            finally { VirtualFreeEx(process, remote, IntPtr.Zero, 0x8000); CloseHandle(process); }
        }

        static byte[] Item(int mask, int index, int state, int stateMask, long text)
        {
            var b = new byte[ItemSize];
            BitConverter.GetBytes(mask).CopyTo(b, 0);
            BitConverter.GetBytes(index).CopyTo(b, 4);
            BitConverter.GetBytes(state).CopyTo(b, 12);
            BitConverter.GetBytes(stateMask).CopyTo(b, 16);
            BitConverter.GetBytes(text).CopyTo(b, 24);
            BitConverter.GetBytes(TextChars).CopyTo(b, 32);
            return b;
        }

        public static string[] ListItems(IntPtr list)
        {
            int count = (int)SendMessage(list, 0x1004, IntPtr.Zero, IntPtr.Zero); // LVM_GETITEMCOUNT
            return WithRemoteItem(list, (process, remote) =>
            {
                var result = new string[count];
                IntPtr done;
                for (int i = 0; i < count; i++)
                {
                    var item = Item(0, i, 0, 0, (long)remote + ItemSize);
                    WriteProcessMemory(process, remote, item, (IntPtr)ItemSize, out done);
                    SendMessage(list, 0x1073, (IntPtr)i, remote); // LVM_GETITEMTEXTW
                    var text = new byte[TextChars * 2];
                    ReadProcessMemory(process, remote + ItemSize, text, (IntPtr)text.Length, out done);
                    result[i] = Encoding.Unicode.GetString(text).Split('\0')[0];
                }
                return result;
            });
        }

        // Selects the row (LVIS_SELECTED | LVIS_FOCUSED); without 'add' the other rows are deselected first
        public static bool SelectItem(IntPtr list, string text, bool add)
        {
            int index = Array.IndexOf(ListItems(list), text);
            if (index < 0) return false;
            return WithRemoteItem(list, (process, remote) =>
            {
                IntPtr done;
                if (!add)
                {
                    WriteProcessMemory(process, remote, Item(0x8, -1, 0, 0x2, 0), (IntPtr)ItemSize, out done);
                    SendMessage(list, 0x102B, (IntPtr)(-1), remote); // LVM_SETITEMSTATE, all rows
                }
                WriteProcessMemory(process, remote, Item(0x8, index, 0x3, 0x3, 0), (IntPtr)ItemSize, out done);
                SendMessage(list, 0x102B, (IntPtr)index, remote);
                return true;
            });
        }
    }
}
'@
$IDOK = 1; $IDNO = 7

function Get-Window($Process) {
    for ($i = 0; $i -lt 50; $i++) {
        Start-Sleep -Milliseconds 200
        $Process.Refresh()
        if ($Process.MainWindowHandle -ne [IntPtr]::Zero) { return [Windows.Automation.AutomationElement]::FromHandle($Process.MainWindowHandle) }
    }
    $null
}

# Visible element whose name matches the wildcard pattern (hidden WinForms controls are not returned)
function Find-Element($Window, [string]$Pattern) {
    foreach ($e in $Window.FindAll('Descendants', [Windows.Automation.Condition]::TrueCondition)) {
        if ($e.Current.Name -like $Pattern -and -not $e.Current.IsOffscreen) { return $e }
    }
    $null
}

function Wait-Element($Window, [string]$Pattern, [int]$TimeoutSec = 20) {
    $end = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $end) { $e = Find-Element $Window $Pattern; if ($e) { return $e }; Start-Sleep -Milliseconds 250 }
    $null
}

# -Async for the buttons that open a modal dialog (SendMessage would wait for it to close)
function Click-Element($Window, [string]$Pattern, [switch]$Async) {
    $e = Find-Element $Window $Pattern
    if (-not $e) { throw "Element not found: $Pattern" }
    $h = [IntPtr]$e.Current.NativeWindowHandle
    if ($Async) { [void][IPG.Win]::PostMessage($h, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) }
    else { [void][IPG.Native]::SendMessage($h, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) }
    Start-Sleep -Milliseconds 400
}

function Wait-Dialog($Process, [int]$TimeoutSec = 15) {
    $end = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $end) {
        $d = [IPG.Win]::FindDialog($Process.Id)
        if ($d -ne [IntPtr]::Zero) { Start-Sleep -Milliseconds 300; return $d }
        Start-Sleep -Milliseconds 200
    }
    [IntPtr]::Zero
}

# Answers the dialog and returns its text. An OK-only MessageBox ignores WM_COMMAND IDOK: it is closed with WM_CLOSE
function Close-Dialog([IntPtr]$Dialog, [int]$Button) {
    if ($Dialog -eq [IntPtr]::Zero) { return '' }
    $text = [IPG.Win]::DialogText($Dialog)
    if ($Button -eq $IDOK) { [void][IPG.Win]::PostMessage($Dialog, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) }
    else { [void][IPG.Win]::PostMessage($Dialog, 0x0111, [IntPtr]$Button, [IntPtr]::Zero) }
    Start-Sleep -Milliseconds 500
    $text
}

# The ListView of the tab on screen (the lists of the hidden tabs are not visible)
function Get-VisibleList($Window) {
    $list = $Window.FindAll('Descendants', [Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.ClassName -like '*SysListView32*' -and -not $_.Current.IsOffscreen } | Select-Object -First 1
    if (-not $list) { throw 'No visible list' }
    [IntPtr]$list.Current.NativeWindowHandle
}

function Get-Rows($Window) { @([IPG.Win]::ListItems((Get-VisibleList $Window))) }

function Select-Row($Window, [string]$Name, [switch]$Add) {
    if (-not [IPG.Win]::SelectItem((Get-VisibleList $Window), $Name, [bool]$Add)) { throw "Row not found: $Name" }
    Start-Sleep -Milliseconds 300
}

function Set-RegJson([string]$Path, [string]$Name, $Value) {
    New-Item -Path $Path -Force | Out-Null
    New-ItemProperty -Path $Path -Name $Name -Value $Value -PropertyType String -Force | Out-Null
}

# ---------------------------------------------------------------------------------------------
# T10: only the controls useful in the selected tab are shown
$s = New-Scenario 'T10-tab-context'
$p = Start-Grabber $s -ExitAfter 60 -NoListen
$win = Get-Window $p
Assert $s.Name 'Capture tab: Listen, Stop and .intunewin option visible' ([bool](Find-Element $win '*Listen') -and [bool](Find-Element $win '*Stop') -and [bool](Find-Element $win 'Also create the .intunewin*'))
Assert $s.Name 'Capture tab: Refresh/Export not visible' (-not (Find-Element $win 'Refresh') -and -not (Find-Element $win 'Export selected'))
foreach ($tab in 'Win32 apps', 'Remediations') {
    Click-Element $win $tab
    Assert $s.Name "${tab}: Listen, Stop and .intunewin option hidden" (-not (Find-Element $win '*Listen') -and -not (Find-Element $win '*Stop') -and -not (Find-Element $win 'Also create the .intunewin*'))
    Assert $s.Name "${tab}: Refresh and Export selected visible" ([bool](Find-Element $win 'Refresh') -and [bool](Find-Element $win 'Export selected'))
    Assert $s.Name "${tab}: global controls visible (Browse, Open folder, Collect logs, Tutorial)" ([bool](Find-Element $win 'Browse...') -and [bool](Find-Element $win 'Open folder') -and [bool](Find-Element $win 'Collect logs') -and [bool](Find-Element $win 'Tutorial'))
}
Click-Element $win 'Capture'
Assert $s.Name 'Back to Capture: Listen visible again' ([bool](Find-Element $win '*Listen'))
$p.Kill()

# ---------------------------------------------------------------------------------------------
# T11: Listen keeps capturing while another tab is shown, and the Capture title tells it
$s = New-Scenario 'T11-listen-other-tab'
$p = Start-Grabber $s -ExitAfter 60 -NoListen
$win = Get-Window $p
Click-Element $win '*Listen'
Click-Element $win 'Remediations'
Assert $s.Name 'Title shows that Listen is active' ([bool](Find-Element $win 'Capture*listening'))
New-Package (Join-Path $s.Cache "$([guid]::NewGuid())_1") @{ 'setup.exe' = (Get-RandomBytes 100) }
Assert $s.Name 'Captured while the Remediations tab is shown' (Wait-Log $s 'created and verified' 30)
Assert $s.Name 'Title counts the new package' ([bool](Wait-Element $win 'Capture*listening*(1 new)' 5))
Click-Element $win 'Capture*'
Assert $s.Name 'Counter reset when the Capture tab is opened' ([bool](Find-Element $win 'Capture*listening') -and -not (Find-Element $win 'Capture*new*'))
Click-Element $win '*Stop'
Assert $s.Name 'After Stop the title is Capture' ([bool](Find-Element $win 'Capture'))
$p.Kill()

# ---------------------------------------------------------------------------------------------
# T12-T15: Win32 apps, remediations, exports and log collection on fake IME data (registry under HKCU, logs, IMECache)
$s = New-Scenario 'T12-diagnostics'
$regRoot = 'HKCU:\Software\IPG-tests'
if (Test-Path $regRoot) { Remove-Item $regRoot -Recurse -Force }
$ime = "$regRoot\IME"
$device = '00000000-0000-0000-0000-000000000000'; $user = '74000000-0000-0000-0000-000000000001'
$failId = '22222222-3333-4444-5555-666666666666'; $availId = '33333333-4444-5555-6666-777777777777'; $orphanId = '99999999-8888-7777-6666-555555555555'
$installed = '{"Applicability":0,"ComplianceState":1,"DesiredState":1,"ErrorCode":null,"TargetingMethod":0,"InstallContext":2,"TargetType":2}'
$failed = '{"Applicability":null,"ComplianceState":4,"DesiredState":0,"ErrorCode":-2016215017,"TargetingMethod":0,"InstallContext":1,"TargetType":1}'
Set-RegJson "$ime\Win32Apps\$device\$KnownAppId`_1" 'Intent' '3'
Set-RegJson "$ime\Win32Apps\$device\$KnownAppId`_1\ComplianceStateMessage" 'ComplianceStateMessage' $installed
Set-RegJson "$ime\Win32Apps\$device\$KnownAppId`_1\EnforcementStateMessage" 'EnforcementStateMessage' '{"EnforcementState":1000,"ErrorCode":0,"TargetingMethod":0}'
Set-RegJson "$ime\Win32Apps\$user\$failId`_1" 'Intent' '3'
Set-RegJson "$ime\Win32Apps\$user\$failId`_1\ComplianceStateMessage" 'ComplianceStateMessage' $failed
Set-RegJson "$ime\Win32Apps\$user\$failId`_1\EnforcementStateMessage" 'EnforcementStateMessage' '{"EnforcementState":5000,"ErrorCode":-2016215017,"TargetingMethod":0}'
# Orphan app (no policy in the logs) with an old revision: the newest one counts
Set-RegJson "$ime\Win32Apps\$user\$orphanId`_1\ComplianceStateMessage" 'ComplianceStateMessage' $installed
Set-RegJson "$ime\Win32Apps\$user\$orphanId`_2\EnforcementStateMessage" 'EnforcementStateMessage' '{"EnforcementState":5000,"ErrorCode":-2147024895,"TargetingMethod":0}'

$diagLogs = Join-Path $s.Root 'ime-logs'
New-Item -ItemType Directory $diagLogs -Force | Out-Null
$policies = @(
    @{ Id = $KnownAppId; Name = $KnownAppName; Version = 1; Intent = 3; SetUpFilePath = 'install.ps1'; InstallCommandLine = 'powershell.exe -ExecutionPolicy Bypass -File install.ps1'; DetectionRule = $detection; ReturnCodes = '[{"ReturnCode":0,"Type":1}]' },
    @{ Id = $failId; Name = 'Fabrikam Failing App'; Version = 1; Intent = 3; SetUpFilePath = 'setup.exe'; InstallCommandLine = 'setup.exe /S'; DetectionRule = $detection; ReturnCodes = '[]' },
    @{ Id = $availId; Name = 'Woodgrove Available App'; Version = 2; Intent = 1; SetUpFilePath = 'setup.exe'; InstallCommandLine = 'setup.exe /S'; DetectionRule = $detection; ReturnCodes = '[]' }
) | ForEach-Object { ConvertTo-Json -Compress $_ }
Set-Content -LiteralPath (Join-Path $diagLogs 'AppWorkload.log') -Encoding UTF8 -Value "<![LOG[Get policies = [$($policies -join ',')]]LOG]!><time=`"00:00:00.000`" date=`"1-1-2026`" component=`"AppWorkload`">"

$remOk = '44444444-0000-0000-0000-000000000001'; $remFail = '44444444-0000-0000-0000-000000000002'; $remNew = '44444444-0000-0000-0000-000000000003'
foreach ($r in "$remOk`_3", "$remFail`_1", "$remNew`_1") { New-Package (Join-Path $s.Cache "HealthScripts\$r") @{ 'detect.ps1' = [Text.Encoding]::ASCII.GetBytes("exit 1 # $r"); 'remediate.ps1' = [Text.Encoding]::ASCII.GetBytes("Write-Output fixed # $r") } }
Set-RegJson "$ime\SideCarPolicies\Scripts\Reports\$user\$remOk`_3\Result" 'Result' (ConvertTo-Json -Compress @{ PolicyId = $remOk; Result = 3; ErrorCode = 0; PreRemediationDetectScriptOutput = 'issue found'; Info = @{ FirstDetectExitCode = 1; RemediationExitCode = 0; LastDetectExitCode = 0 } })
Set-RegJson "$ime\SideCarPolicies\Scripts\Execution\$user\$remOk`_3" 'LastExecution' '10/22/2026 08:00:00'
Set-RegJson "$ime\SideCarPolicies\Scripts\Reports\$device\$remFail`_1\Result" 'Result' (ConvertTo-Json -Compress @{ PolicyId = $remFail; Result = 4; ErrorCode = -2016410860; Info = @{ FirstDetectExitCode = 1; RemediationExitCode = 1; LastDetectExitCode = 1 } })

$a = "--cache `"$($s.Cache)`" --dest `"$($s.Out)`" --log `"$($s.Log)`" --imelogs `"$diagLogs`" --imereg `"HKCU\Software\IPG-tests\IME`" --exit-after 300"
$p = Start-Process $Exe -ArgumentList $a -PassThru
$win = Get-Window $p

# T12: Win32 apps tab
Click-Element $win 'Win32 apps'
Assert 'T12-win32-apps' 'Count and errors in the caption (4 apps, 2 with errors)' ([bool](Wait-Element $win 'Win32 apps known by IME on this device: 4*2 with errors*'))
$rows = Get-Rows $win
Assert 'T12-win32-apps' 'Rows: errors first, assigned app without state listed, apps without a name last' (($rows -join '|') -eq "Fabrikam Failing App|$KnownAppName|Woodgrove Available App|(name not in IME logs)") ($rows -join ' | ')
Click-Element $win 'Export selected' -Async
$text = Close-Dialog (Wait-Dialog $p) $IDOK
Assert 'T12-win32-apps' 'Export without selection: asks to select an app' ($text -like '*Select one or more apps*') $text
Assert 'T12-win32-apps' 'Export without selection: nothing written' (-not (Get-ChildItem -LiteralPath $s.Out -Directory))
Select-Row $win $KnownAppName
Click-Element $win 'Export selected' -Async
$text = Close-Dialog (Wait-Dialog $p) $IDNO
Assert 'T12-win32-apps' 'Export: asks whether to open the folder' ($text -like '*Exported app to:*Open the folder now?*') $text
$exp = @(Get-ChildItem -LiteralPath $s.Out -Directory -Filter "$KnownAppName`_info_*")
$info = if ($exp) { Get-Content -LiteralPath "$($exp[0].FullName)\app-info.txt" -Raw } else { '' }
Assert 'T12-win32-apps' 'Export: app-info.txt with state and commands, policy.json' (($info -match 'Detected\s+: Installed') -and ($info -match 'Install command\s+: powershell\.exe') -and (Test-Path -LiteralPath "$($exp[0].FullName)\policy.json"))
Select-Row $win 'Fabrikam Failing App'
Select-Row $win '(name not in IME logs)' -Add
Click-Element $win 'Export selected' -Async
$text = Close-Dialog (Wait-Dialog $p) $IDNO
Assert 'T12-win32-apps' 'Export of 2 apps: one question for both' ($text -like '*2 apps exported to:*') $text
$orphan = @(Get-ChildItem -LiteralPath $s.Out -Directory -Filter "$orphanId`_info_*")
$info = if ($orphan) { Get-Content -LiteralPath "$($orphan[0].FullName)\app-info.txt" -Raw } else { '' }
Assert 'T12-win32-apps' 'App without policy: newest revision and error code' (($info -match 'revision 2') -and ($info -match '0x80070001'))

# T13: invalid archive folder
$edit = [IntPtr](Find-Element $win $s.Out).Current.NativeWindowHandle
[IPG.Win]::SetText($edit, 'C:\invalid<>folder')
Click-Element $win 'Export selected' -Async
$text = Close-Dialog (Wait-Dialog $p) $IDOK
Assert 'T13-invalid-folder' 'Clear error, no crash' (($text -like '*Unable to create the folder*') -and -not $p.HasExited) $text
Assert 'T13-invalid-folder' 'Nothing exported to the invalid folder' (-not (Test-Path -LiteralPath 'C:\invalid'))
[IPG.Win]::SetText($edit, $s.Out)

# T14: Remediations tab
Click-Element $win 'Remediations'
Assert 'T14-remediations' 'Count and problems in the caption (3, 1 with problems)' ([bool](Wait-Element $win 'Remediations on this device: 3*1 with problems*'))
Select-Row $win $remOk
Click-Element $win 'Export selected' -Async
$text = Close-Dialog (Wait-Dialog $p) $IDNO
Assert 'T14-remediations' 'Export: asks whether to open the folder' ($text -like '*Exported remediation to:*Open the folder now?*') $text
$rem = @(Get-ChildItem -LiteralPath $s.Out -Directory -Filter "Remediation_$remOk`_v3_*")
$info = if ($rem) { Get-Content -LiteralPath "$($rem[0].FullName)\info.txt" -Raw } else { '' }
Assert 'T14-remediations' 'Export: scripts, result and decoded exit codes' (($info -match 'Detection\s+: Issue found \(exit 1\)') -and ($info -match 'Remediation\s+: Done \(exit 0\)') -and ($info -match 'Post-detection\s+: Fixed \(exit 0\)') -and (Test-Path -LiteralPath "$($rem[0].FullName)\detect.ps1") -and (Test-Path -LiteralPath "$($rem[0].FullName)\remediate.ps1") -and (Test-Path -LiteralPath "$($rem[0].FullName)\result.json"))

# T15: Collect logs
Click-Element $win 'Collect logs' -Async
$text = Close-Dialog (Wait-Dialog $p 90) $IDNO
Assert 'T15-collect-logs' 'Asks whether to open the folder' ($text -like '*Diagnostics collected*Open the folder now?*') $text
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipFile = Get-ChildItem -LiteralPath $s.Out -Filter 'IntuneDiagnostics_*.zip' | Select-Object -First 1
if ($zipFile) {
    $zip = [IO.Compression.ZipFile]::OpenRead($zipFile.FullName)
    $entries = $zip.Entries | ForEach-Object FullName
    $read = { param($n) $r = New-Object IO.StreamReader ($zip.GetEntry($n).Open()); $t = $r.ReadToEnd(); $r.Close(); $t }
    $apps = (& $read 'win32-apps.csv') -split "`r?`n" | Where-Object { $_ } | Select-Object -Skip 1 | ForEach-Object { ($_ -split '";"')[0].Trim('"') }
    $rems = & $read 'remediations.csv'
    $reg = & $read 'registry/IntuneManagementExtension.reg'
    $zip.Dispose()
    Assert 'T15-collect-logs' 'Zip with IME logs, dsregcmd, registry and CSV' (($entries -contains 'IME/AppWorkload.log') -and ($entries -contains 'dsregcmd-status.txt') -and ($reg -match 'IPG-tests') -and ($rems -match $remFail))
    Assert 'T15-collect-logs' 'App order: errors first, apps without a name last' ((($apps -join '|') -eq "Fabrikam Failing App|$KnownAppName|Woodgrove Available App|(name not in IME logs)")) ($apps -join ' | ')
}
else { Assert 'T15-collect-logs' 'Zip created' $false }
$p.Kill()
Remove-Item $regRoot -Recurse -Force

# ---------------------------------------------------------------------------------------------
if ($null -ne $settingsBackup) { Set-Content -Path $settings -Value $settingsBackup -NoNewline } elseif (Test-Path $settings) { Remove-Item $settings }
$results | Format-Table -AutoSize -Wrap | Out-String -Width 220
$failed = @($results | Where-Object Result -eq 'FAIL').Count
"Total: $($results.Count)  PASS: $($results.Count - $failed)  FAIL: $failed"
