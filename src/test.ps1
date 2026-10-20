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
if ($null -ne $settingsBackup) { Set-Content -Path $settings -Value $settingsBackup -NoNewline } elseif (Test-Path $settings) { Remove-Item $settings }
$results | Format-Table -AutoSize -Wrap | Out-String -Width 220
$failed = @($results | Where-Object Result -eq 'FAIL').Count
"Total: $($results.Count)  PASS: $($results.Count - $failed)  FAIL: $failed"
