#Requires -Version 5.1
<#
    RO2RW Manager - ממשק גרפי לווינדוס להפעלת הסקריפט RO2RW (מאת LeeGarChat) דרך ADB.
    הסקריפט עצמו רץ על הטלפון (TWRP / Magisk). התוכנה הזו:
      * מורידה את חבילת ה-ZIP ואת כלי ADB
      * עורכת את config.txt בצורה נוחה ובונה ZIP מותאם
      * מעבירה את ה-ZIP לטלפון ומריצה אותו, עם יומן חי במחשב
      * מאפשרת לשלוח תשובות לשאלות של הסקריפט (במצב Magisk/טרמינל)
      * מושכת את קובצי היומן (NEO.LOGS) למחשב
#>

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.Windows.Forms.Application]::EnableVisualStyles()

try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }

# ---------------------------------------------------------------- נתיבים וקבועים
$AppDir       = Split-Path -Parent $MyInvocation.MyCommand.Path
$ToolsDir     = Join-Path $AppDir 'platform-tools'
$WorkDir      = Join-Path $AppDir 'work'
$LogsDir      = Join-Path $AppDir 'logs'
$DefaultZip   = Join-Path $WorkDir 'RO2RW-TEST-3.7.3.0s.zip'
$CustomZip    = Join-Path $WorkDir 'RO2RW-custom.zip'
$Ro2rwUrl     = 'https://sourceforge.net/projects/multi-function-patch/files/RO2RW/RO2RW-TEST-3.7.3.0s.zip/download'
$AdbUrl       = 'https://dl.google.com/android/repository/platform-tools-latest-windows.zip'
$SettingsFile = Join-Path $AppDir 'settings.json'
$RemoteZip    = '/sdcard/RO2RW.zip'   # היעד בטלפון

foreach ($d in @($WorkDir, $LogsDir)) { if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d | Out-Null } }

# ---------------------------------------------------------------- עזרים כלליים
function Get-AdbPath {
    $local = Join-Path $ToolsDir 'adb.exe'
    if (Test-Path $local) { return $local }
    $cmd = Get-Command adb.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

function Invoke-AdbQuick {
    param([string]$Arguments, [int]$TimeoutMs = 8000)
    $adb = Get-AdbPath
    if (-not $adb) { return $null }
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $adb
    $psi.Arguments = $Arguments
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.StandardOutputEncoding = [Text.Encoding]::UTF8
    $p = [Diagnostics.Process]::Start($psi)
    $outTask = $p.StandardOutput.ReadToEndAsync()
    $errTask = $p.StandardError.ReadToEndAsync()
    if (-not $p.WaitForExit($TimeoutMs)) { try { $p.Kill() } catch { }; return $null }
    return (($outTask.Result + $errTask.Result).Trim())
}

function Save-Settings {
    $o = [ordered]@{ Mode = $cmbMode.SelectedIndex }
    try { $o | ConvertTo-Json | Set-Content -Path $SettingsFile -Encoding UTF8 } catch { }
}

function Load-Settings {
    if (-not (Test-Path $SettingsFile)) { return }
    try {
        $o = Get-Content $SettingsFile -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($null -ne $o.Mode -and $o.Mode -ge 0 -and $o.Mode -lt $cmbMode.Items.Count) { $cmbMode.SelectedIndex = [int]$o.Mode }
    } catch { }
}

# ---------------------------------------------------------------- טיפול ב-ZIP וב-config.txt
function Read-ZipConfig {
    param([string]$ZipPath)
    $zip = [IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $entry = $zip.GetEntry('config.txt')
        if (-not $entry) { throw 'הקובץ config.txt לא נמצא בתוך ה-ZIP. האם זו חבילת RO2RW?' }
        $sr = New-Object IO.StreamReader($entry.Open(), [Text.Encoding]::UTF8)
        try { return $sr.ReadToEnd() } finally { $sr.Dispose() }
    } finally { $zip.Dispose() }
}

function Get-ConfigValue {
    param([string]$Text, [string]$Key)
    $m = [regex]::Match($Text, '(?m)^[ \t]*' + [regex]::Escape($Key) + '="([^"]*)"')
    if ($m.Success) { return $m.Groups[1].Value }
    return ''
}

function Set-ConfigValue {
    param([string]$Text, [string]$Key, [string]$Value)
    $rx = '(?m)^([ \t]*)' + [regex]::Escape($Key) + '="[^"]*"'
    if ([regex]::IsMatch($Text, $rx)) {
        $v = $Value.Replace('$', '$$')
        return [regex]::Replace($Text, $rx, ('${1}' + $Key + '="' + $v + '"'), 1)
    }
    return $Text.TrimEnd() + "`n    $Key=`"$Value`"`n"
}

function Write-CustomZip {
    param([string]$SourceZip, [string]$TargetZip, [string]$ConfigText)
    Copy-Item -Path $SourceZip -Destination $TargetZip -Force
    $ConfigText = $ConfigText -replace "`r`n", "`n"
    $zip = [IO.Compression.ZipFile]::Open($TargetZip, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $old = $zip.GetEntry('config.txt')
        if ($old) { $old.Delete() }
        $new = $zip.CreateEntry('config.txt', [IO.Compression.CompressionLevel]::Optimal)
        $sw = New-Object IO.StreamWriter($new.Open(), (New-Object Text.UTF8Encoding($false)))
        try { $sw.NewLine = "`n"; $sw.Write($ConfigText) } finally { $sw.Dispose() }
    } finally { $zip.Dispose() }
}

# ---------------------------------------------------------------- הרצת תהליכים עם פלט חי (ללא קפיאת הממשק)
$script:Steps     = New-Object System.Collections.ArrayList
$script:Proc      = $null
$script:OutTask   = $null
$script:ErrTask   = $null
$script:OutBuf    = New-Object char[] 4096
$script:ErrBuf    = New-Object char[] 4096
$script:OutLine   = ''
$script:ErrLine   = ''
$script:PartialAt = [DateTime]::MinValue
$script:Filter    = $false
$script:OnDone    = $null
$script:LogFile   = $null

function Write-Log {
    param([string]$Text, [System.Drawing.Color]$Color = [System.Drawing.Color]::Gainsboro)
    $txtLog.SelectionStart = $txtLog.TextLength
    $txtLog.SelectionLength = 0
    $txtLog.SelectionColor = $Color
    $txtLog.AppendText($Text)
    $txtLog.SelectionColor = $txtLog.ForeColor
    $txtLog.ScrollToCaret()
    if ($script:LogFile) { try { [IO.File]::AppendAllText($script:LogFile, $Text, [Text.Encoding]::UTF8) } catch { } }
}

function Write-Info  { param([string]$t) Write-Log ("`n>> " + $t + "`n") ([System.Drawing.Color]::DeepSkyBlue) }
function Write-Ok    { param([string]$t) Write-Log ("`n>> " + $t + "`n") ([System.Drawing.Color]::LightGreen) }
function Write-Err   { param([string]$t) Write-Log ("`n!! " + $t + "`n") ([System.Drawing.Color]::Tomato) }

function Format-OutLine {
    param([string]$line)
    $line = $line.TrimEnd("`r")
    if ($script:Filter) {
        if ($line -eq 'ui_print' -or $line -eq 'ui_print ') { return $null }
        if ($line.StartsWith('ui_print ')) { return $line.Substring(9) }
    }
    if ($line -match '\r') { $line = ($line -split "`r")[-1] }   # שורות התקדמות של adb push
    return $line
}

function Emit-Lines {
    param([string]$chunk, [bool]$IsErr)
    if ($IsErr) { $buf = $script:ErrLine + $chunk } else { $buf = $script:OutLine + $chunk }
    $parts = $buf -split "`n"
    for ($i = 0; $i -lt $parts.Length - 1; $i++) {
        $l = Format-OutLine $parts[$i]
        if ($null -ne $l) {
            if ($IsErr) { Write-Log ($l + "`n") ([System.Drawing.Color]::Khaki) }
            else        { Write-Log ($l + "`n") }
        }
    }
    $rest = $parts[$parts.Length - 1]
    if ($IsErr) { $script:ErrLine = $rest } else { $script:OutLine = $rest }
    if ($rest.Length -gt 0) { $script:PartialAt = [DateTime]::Now }
}

function Start-NextStep {
    if ($script:Steps.Count -eq 0) {
        Set-Busy $false
        Write-Ok 'הפעולה הסתיימה.'
        if ($script:OnDone) { $cb = $script:OnDone; $script:OnDone = $null; & $cb }
        return
    }
    $step = $script:Steps[0]
    $script:Steps.RemoveAt(0)

    if ($step.ContainsKey('Script')) {
        try { & $step.Script; Start-NextStep } catch { Write-Err $_.Exception.Message; Stop-All }
        return
    }

    $adb = Get-AdbPath
    if (-not $adb) { Write-Err 'ADB לא נמצא. לחץ "הורד ADB" בלשונית חיבור.'; Stop-All; return }

    Write-Info $step.Desc
    $script:Filter = [bool]$step.Filter
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $adb
    $psi.Arguments = $step.Args
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.RedirectStandardInput = $true
    $psi.CreateNoWindow = $true
    $psi.StandardOutputEncoding = [Text.Encoding]::UTF8
    $psi.StandardErrorEncoding = [Text.Encoding]::UTF8
    try {
        $script:Proc = [Diagnostics.Process]::Start($psi)
        $script:Proc.StandardInput.NewLine = "`n"
    } catch {
        Write-Err ('לא ניתן להפעיל את ADB: ' + $_.Exception.Message); Stop-All; return
    }
    $script:Proc | Add-Member -NotePropertyName AllowFail -NotePropertyValue ([bool]$step.AllowFail) -Force
    $script:OutLine = ''; $script:ErrLine = ''
    $script:OutTask = $script:Proc.StandardOutput.ReadAsync($script:OutBuf, 0, $script:OutBuf.Length)
    $script:ErrTask = $script:Proc.StandardError.ReadAsync($script:ErrBuf, 0, $script:ErrBuf.Length)
    $timer.Start()
}

function Poll-Process {
    if (-not $script:Proc) { $timer.Stop(); return }
    $guard = 0
    while ($script:OutTask -and $script:OutTask.IsCompleted -and $guard -lt 50) {
        $n = $script:OutTask.Result
        if ($n -le 0) { $script:OutTask = $null; break }
        Emit-Lines ([string]::new($script:OutBuf, 0, $n)) $false
        $script:OutTask = $script:Proc.StandardOutput.ReadAsync($script:OutBuf, 0, $script:OutBuf.Length)
        $guard++
    }
    $guard = 0
    while ($script:ErrTask -and $script:ErrTask.IsCompleted -and $guard -lt 50) {
        $n = $script:ErrTask.Result
        if ($n -le 0) { $script:ErrTask = $null; break }
        Emit-Lines ([string]::new($script:ErrBuf, 0, $n)) $true
        $script:ErrTask = $script:Proc.StandardError.ReadAsync($script:ErrBuf, 0, $script:ErrBuf.Length)
        $guard++
    }
    # שאלה בלי ירידת שורה (למשל " - Select num: ") - מציגים אותה אחרי השהיה קצרה
    if ($script:OutLine.Length -gt 0 -and ([DateTime]::Now - $script:PartialAt).TotalMilliseconds -gt 400) {
        $l = Format-OutLine $script:OutLine
        $script:OutLine = ''
        if ($l) { Write-Log $l ([System.Drawing.Color]::Gold); $txtInput.Focus() | Out-Null }
    }

    if ($null -eq $script:OutTask -and $null -eq $script:ErrTask -and $script:Proc.HasExited) {
        $timer.Stop()
        foreach ($rest in @($script:OutLine, $script:ErrLine)) { if ($rest) { Write-Log ((Format-OutLine $rest) + "`n") } }
        $script:OutLine = ''; $script:ErrLine = ''
        $code = $script:Proc.ExitCode
        $allow = $script:Proc.AllowFail
        $script:Proc.Dispose(); $script:Proc = $null
        if ($code -ne 0 -and -not $allow) {
            Write-Err ("השלב נכשל (קוד יציאה $code). הפעולה נעצרה.")
            Stop-All
        } else {
            Start-NextStep
        }
    }
}

function Stop-All {
    $script:Steps.Clear()
    $script:OnDone = $null
    if ($script:Proc) {
        try { if (-not $script:Proc.HasExited) { $script:Proc.Kill() } } catch { }
        try { $script:Proc.Dispose() } catch { }
        $script:Proc = $null
    }
    $timer.Stop()
    Set-Busy $false
}

function Run-Steps {
    param([array]$Steps, [scriptblock]$OnDone = $null)
    if ($script:Proc) { [System.Windows.Forms.MessageBox]::Show('פעולה אחרת כבר רצה.', 'RO2RW') | Out-Null; return }
    $script:Steps.Clear()
    foreach ($s in $Steps) { [void]$script:Steps.Add($s) }
    $script:OnDone = $OnDone
    $script:LogFile = Join-Path $LogsDir ('session-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.txt')
    Set-Busy $true
    Start-NextStep
}

function Step { param([string]$Desc, [string]$AdbArgs, [switch]$Filter, [switch]$AllowFail) @{ Desc = $Desc; Args = $AdbArgs; Filter = [bool]$Filter; AllowFail = [bool]$AllowFail } }

function Send-Input {
    $t = $txtInput.Text
    if (-not $script:Proc -or $script:Proc.HasExited) { return }
    try {
        $script:Proc.StandardInput.WriteLine($t)
        $script:Proc.StandardInput.Flush()
        Write-Log (' ' + $t + "`n") ([System.Drawing.Color]::Gold)
    } catch { Write-Err ('שליחת הקלט נכשלה: ' + $_.Exception.Message) }
    $txtInput.Clear()
}

# ---------------------------------------------------------------- בניית הממשק
$font     = New-Object System.Drawing.Font('Segoe UI', 10)
$fontBold = New-Object System.Drawing.Font('Segoe UI', 10, [System.Drawing.FontStyle]::Bold)
$fontMono = New-Object System.Drawing.Font('Consolas', 10)

$form = New-Object System.Windows.Forms.Form
$form.Text = 'RO2RW Manager - הפעלת RO2RW מהמחשב'
$form.Size = New-Object System.Drawing.Size(980, 760)
$form.MinimumSize = New-Object System.Drawing.Size(900, 680)
$form.StartPosition = 'CenterScreen'
$form.Font = $font
$form.RightToLeft = 'Yes'
$form.RightToLeftLayout = $true

$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 80
$timer.Add_Tick({ try { Poll-Process } catch { Write-Err $_.Exception.Message; Stop-All } })

$tabs = New-Object System.Windows.Forms.TabControl
$tabs.Dock = 'Top'
$tabs.Height = 330
$tabs.RightToLeftLayout = $true

function New-Label { param($text, $x, $y, $w = 200, $h = 24) $l = New-Object System.Windows.Forms.Label; $l.Text = $text; $l.Location = New-Object System.Drawing.Point($x, $y); $l.Size = New-Object System.Drawing.Size($w, $h); $l }
function New-Button { param($text, $x, $y, $w = 150, $h = 32) $b = New-Object System.Windows.Forms.Button; $b.Text = $text; $b.Location = New-Object System.Drawing.Point($x, $y); $b.Size = New-Object System.Drawing.Size($w, $h); $b }
function New-Check { param($text, $x, $y, $w = 420) $c = New-Object System.Windows.Forms.CheckBox; $c.Text = $text; $c.Location = New-Object System.Drawing.Point($x, $y); $c.Size = New-Object System.Drawing.Size($w, 24); $c }
function New-Combo { param($items, $x, $y, $w = 200) $c = New-Object System.Windows.Forms.ComboBox; $c.DropDownStyle = 'DropDownList'; [void]$c.Items.AddRange([object[]]$items); $c.Location = New-Object System.Drawing.Point($x, $y); $c.Size = New-Object System.Drawing.Size($w, 26); $c.SelectedIndex = 0; $c }
function New-Num { param($x, $y, $max = 100, $w = 70) $n = New-Object System.Windows.Forms.NumericUpDown; $n.Minimum = 0; $n.Maximum = $max; $n.Location = New-Object System.Drawing.Point($x, $y); $n.Size = New-Object System.Drawing.Size($w, 26); $n }

# ---------- לשונית 1: חיבור וקובץ
$tabConn = New-Object System.Windows.Forms.TabPage
$tabConn.Text = '1. חיבור וחבילה'

$grpAdb = New-Object System.Windows.Forms.GroupBox
$grpAdb.Text = 'מכשיר / ADB'
$grpAdb.Location = New-Object System.Drawing.Point(10, 8); $grpAdb.Size = New-Object System.Drawing.Size(935, 120)
$lblAdb    = New-Label 'ADB: בודק...' 15 25 900
$lblDevice = New-Label 'מכשיר: -' 15 52 900
$lblDevice.Font = $fontBold
$btnRefresh = New-Button 'רענן מצב' 15 80 130
$btnGetAdb  = New-Button 'הורד ADB' 155 80 130
$btnDrivers = New-Button 'עזרה בחיבור' 295 80 130
$grpAdb.Controls.AddRange(@($lblAdb, $lblDevice, $btnRefresh, $btnGetAdb, $btnDrivers))

$grpZip = New-Object System.Windows.Forms.GroupBox
$grpZip.Text = 'חבילת RO2RW'
$grpZip.Location = New-Object System.Drawing.Point(10, 135); $grpZip.Size = New-Object System.Drawing.Size(935, 155)
$lblZipInfo = New-Label 'בודק חבילה...' 15 30 900 24
$lblZipInfo.Font = $fontBold
$lblZipHint = New-Label 'החבילה יורדת אוטומטית מ-SourceForge ונשמרת בתיקיית התוכנה. אין צורך לבחור קובץ.' 15 62 900 24
$lblZipHint.ForeColor = [System.Drawing.Color]::DimGray
$btnDlZip  = New-Button 'הורד מחדש' 15 100 140
$grpZip.Controls.AddRange(@($lblZipInfo, $lblZipHint, $btnDlZip))
$tabConn.Controls.AddRange(@($grpAdb, $grpZip))

# ---------- לשונית 2: הגדרות (config.txt)
$tabCfg = New-Object System.Windows.Forms.TabPage
$tabCfg.Text = '2. הגדרות'
$tabCfg.AutoScroll = $true

$grpSize = New-Object System.Windows.Forms.GroupBox
$grpSize.Text = 'הגדלת מחיצות'
$grpSize.Location = New-Object System.Drawing.Point(10, 5); $grpSize.Size = New-Object System.Drawing.Size(460, 175)
$lblMod = New-Label 'שיטה:' 15 28 60
$cmbMod = New-Combo @('MAX - לפי אחוזים מהמקום הפנוי', 'FIXED - גודל קבוע ב-MB') 80 25 300
$numS  = New-Num 15 85;  $numV = New-Num 100 85; $numP = New-Num 185 85; $numSE = New-Num 270 85; $numOT = New-Num 355 85
$numS.Value = 70; $numV.Value = 10; $numP.Value = 10; $numSE.Value = 5; $numOT.Value = 5
$l1 = New-Label 'System %' 15 62 80; $l2 = New-Label 'Vendor %' 100 62 80; $l3 = New-Label 'Product %' 185 62 80; $l4 = New-Label 'System_ext %' 270 62 85; $l5 = New-Label 'אחרות %' 355 62 80
$lblTotal = New-Label 'סה"כ: 100%' 15 118 300
$lblFixed = New-Label 'הגדלה לכל מחיצה (MB):' 15 145 170
$numFixed = New-Num 190 142 100000 100
$numFixed.Value = 100
$grpSize.Controls.AddRange(@($lblMod, $cmbMod, $l1, $l2, $l3, $l4, $l5, $numS, $numV, $numP, $numSE, $numOT, $lblTotal, $lblFixed, $numFixed))

$grpGen = New-Object System.Windows.Forms.GroupBox
$grpGen.Text = 'כללי'
$grpGen.Location = New-Object System.Drawing.Point(480, 5); $grpGen.Size = New-Object System.Drawing.Size(465, 175)
$chkTimeout = New-Check 'בטל הגבלת זמן ללחצני עוצמת שמע (REMOVE_TIMEOUT_KEY)' 15 28 440
$chkForce   = New-Check 'הפעלה אוטומטית ללא שאלות - TWRP בלבד (FORCE_START)' 15 58 440
$chkWrong   = New-Check 'אם ערך שגוי - עבור לבחירה ידנית (IF_VALUE_WRONG)' 15 88 440
$lblForceNote = New-Label 'כשההפעלה אוטומטית, כל הערכים בחלק התחתון חייבים להיות מלאים.' 15 120 440 44
$lblForceNote.ForeColor = [System.Drawing.Color]::DimGray
$grpGen.Controls.AddRange(@($chkTimeout, $chkForce, $chkWrong, $lblForceNote))

$grpForce = New-Object System.Windows.Forms.GroupBox
$grpForce.Text = 'ערכים להפעלה אוטומטית (FORCE_START)'
$grpForce.Location = New-Object System.Drawing.Point(10, 185); $grpForce.Size = New-Object System.Drawing.Size(935, 110)
$lblWhat = New-Label 'מצב:' 15 28 50
$cmbWhat = New-Combo @('literw', 'fullrw', 'checkrw') 70 25 120
$lblBak  = New-Label 'גיבוי super מקורי:' 210 28 130
$cmbBak  = New-Combo @('false', 'true:row', 'true:sparse', 'true:recovery', 'true:fastboot') 345 25 150
$chkDfe  = New-Check 'DFE_PATCH (רק אם אתה יודע מה זה)' 520 27 400
$chkExt4 = New-Check 'המשך גם אם יש בעיה בעיגון EXT4' 15 65 420
$chkNoData = New-Check 'המשך גם אם האחסון הפנימי לא זמין' 450 65 420
$grpForce.Controls.AddRange(@($lblWhat, $cmbWhat, $lblBak, $cmbBak, $chkDfe, $chkExt4, $chkNoData))
$tabCfg.Controls.AddRange(@($grpSize, $grpGen, $grpForce))

# ---------- לשונית 3: עזרה
$tabHelp = New-Object System.Windows.Forms.TabPage
$tabHelp.Text = 'עזרה'
$txtHelp = New-Object System.Windows.Forms.TextBox
$txtHelp.Multiline = $true; $txtHelp.ReadOnly = $true; $txtHelp.ScrollBars = 'Vertical'; $txtHelp.Dock = 'Fill'
$txtHelp.Text = @'
מה התוכנה עושה?
RO2RW הוא סקריפט שרץ על הטלפון (ב-TWRP או דרך Magisk) והופך את מחיצות המערכת שבתוך super מקריאה בלבד לקריאה וכתיבה.
התוכנה הזו מפעילה אותו מהמחשב דרך כבל USB ו-ADB, ומציגה את כל הפלט כאן.

שלבים:
1. הפעל "ניפוי באגים ב-USB" בטלפון, חבר כבל ואשר את המחשב בטלפון.
2. לשונית "חיבור וחבילה": אם אין ADB לחץ "הורד ADB". חבילת RO2RW יורדת אוטומטית.
3. לשונית "הגדרות": קבע את ההגדרות. ההגדרות נשמרות ב-config.txt בתוך ZIP מותאם.
4. למטה: בחר שיטת הפעלה ולחץ "הפעל".

שיטות הפעלה:
* TWRP - יומן חי במחשב (מומלץ): הטלפון צריך להיות ב-TWRP. ה-ZIP מועתק ומורץ, והפלט מוצג כאן.
  הבחירות נעשות בטלפון: עוצמה (+) עוברת לאפשרות הבאה ועוצמה (-) בוחרת.
  כשההפעלה אוטומטית (FORCE_START) מופעלת, אין שאלות בכלל.
* TWRP - התקנה רגילה (twrp install): אותו דבר, אבל הפלט מוצג רק במסך הטלפון.
* ADB Sideload: מעביר את TWRP למצב sideload ושולח את ה-ZIP.
* Magisk - התקנת כלי RO2RW: מתקין את הסקריפט כמודול במערכת. נדרש אתחול לאחר מכן.
* Magisk - הרצת RO2RW: מריץ את הסקריפט במערכת הפועלת. בשאלות הקלד מספר בתיבה למטה ולחץ "שלח".

חשוב:
* מדובר בשינוי מחיצות מערכת. גבה את המכשיר לפני ההפעלה, האחריות עליך.
* יומני הסקריפט נשמרים בטלפון ב-/sdcard/NEO.LOGS. כפתור "משוך יומנים" מעתיק אותם לתיקייה logs.
* יומני התוכנה נשמרים גם הם בתיקייה logs שליד התוכנה.
'@
$tabHelp.Controls.Add($txtHelp)

$tabs.TabPages.AddRange(@($tabConn, $tabCfg, $tabHelp))

# ---------- פאנל הפעלה
$pnlRun = New-Object System.Windows.Forms.Panel
$pnlRun.Dock = 'Top'
$pnlRun.Height = 92
$lblMode = New-Label 'שיטת הפעלה:' 12 12 95
$cmbMode = New-Combo @(
    'TWRP - יומן חי במחשב (מומלץ)',
    'TWRP - התקנה רגילה (twrp install)',
    'ADB Sideload',
    'Magisk - התקנת כלי RO2RW',
    'Magisk - הרצת RO2RW'
) 110 9 300
$btnRun     = New-Button 'הפעל' 420 6 110 34
$btnRun.Font = $fontBold
$btnRun.BackColor = [System.Drawing.Color]::FromArgb(46, 125, 50); $btnRun.ForeColor = [System.Drawing.Color]::White
$btnStop    = New-Button 'עצור' 535 6 80 34
$btnLogs    = New-Button 'משוך יומנים' 620 6 110 34
$btnClear   = New-Button 'נקה יומן' 735 6 90 34
$btnOpenDir = New-Button 'פתח תיקייה' 830 6 110 34
$btnRecovery = New-Button 'אתחל ל-Recovery' 12 50 140 32
$btnSystem   = New-Button 'אתחל למערכת' 157 50 120 32
$btnFastboot = New-Button 'אתחל ל-Fastboot' 282 50 130 32
$lblWarn = New-Label 'גבה את המכשיר לפני ההפעלה. שינוי מחיצות מערכת נעשה על אחריותך.' 420 56 520
$lblWarn.ForeColor = [System.Drawing.Color]::Firebrick
$pnlRun.Controls.AddRange(@($lblMode, $cmbMode, $btnRun, $btnStop, $btnLogs, $btnClear, $btnOpenDir, $btnRecovery, $btnSystem, $btnFastboot, $lblWarn))

# ---------- קלט ופלט
$pnlInput = New-Object System.Windows.Forms.Panel
$pnlInput.Dock = 'Bottom'
$pnlInput.Height = 40
$lblIn = New-Label 'תשובה לסקריפט:' 10 10 115
$txtInput = New-Object System.Windows.Forms.TextBox
$txtInput.Location = New-Object System.Drawing.Point(130, 7); $txtInput.Size = New-Object System.Drawing.Size(250, 26)
$txtInput.RightToLeft = 'No'
$btnSend = New-Button 'שלח' 390 5 80 30
$lblInHint = New-Label '(למשל מספר אפשרות. במצב Magisk בלבד. ב-TWRP בוחרים בלחצני העוצמה)' 480 10 470
$lblInHint.ForeColor = [System.Drawing.Color]::DimGray
$pnlInput.Controls.AddRange(@($lblIn, $txtInput, $btnSend, $lblInHint))

$txtLog = New-Object System.Windows.Forms.RichTextBox
$txtLog.Dock = 'Fill'
$txtLog.ReadOnly = $true
$txtLog.BackColor = [System.Drawing.Color]::FromArgb(24, 24, 28)
$txtLog.ForeColor = [System.Drawing.Color]::Gainsboro
$txtLog.Font = $fontMono
$txtLog.RightToLeft = 'No'
$txtLog.WordWrap = $true

$status = New-Object System.Windows.Forms.StatusStrip
$statusLbl = New-Object System.Windows.Forms.ToolStripStatusLabel
$statusLbl.Text = 'מוכן'
[void]$status.Items.Add($statusLbl)

# סדר הוספה: Fill ראשון, אחר כך Top/Bottom
$form.Controls.Add($txtLog)
$form.Controls.Add($pnlInput)
$form.Controls.Add($pnlRun)
$form.Controls.Add($tabs)
$form.Controls.Add($status)

# ---------------------------------------------------------------- לוגיקה של הממשק
function Set-Busy {
    param([bool]$busy)
    $btnRun.Enabled = -not $busy
    $btnLogs.Enabled = -not $busy
    $btnRecovery.Enabled = -not $busy; $btnSystem.Enabled = -not $busy; $btnFastboot.Enabled = -not $busy
    $btnDlZip.Enabled = -not $busy; $btnGetAdb.Enabled = -not $busy
    $btnStop.Enabled = $busy
    if ($busy) { $statusLbl.Text = 'פועל...' } else { $statusLbl.Text = 'מוכן'; Update-DeviceStatus }
}

function Update-SizeUi {
    $isMax = ($cmbMod.SelectedIndex -eq 0)
    foreach ($c in @($numS, $numV, $numP, $numSE, $numOT, $l1, $l2, $l3, $l4, $l5, $lblTotal)) { $c.Enabled = $isMax }
    $lblFixed.Enabled = -not $isMax; $numFixed.Enabled = -not $isMax
    $sum = [int]($numS.Value + $numV.Value + $numP.Value + $numSE.Value + $numOT.Value)
    $lblTotal.Text = "סה`"כ: $sum%"
    if ($sum -gt 100) { $lblTotal.ForeColor = [System.Drawing.Color]::Firebrick; $lblTotal.Text += '  (יותר מ-100%!)' }
    else { $lblTotal.ForeColor = [System.Drawing.Color]::Black }
}

function Update-ForceUi { $grpForce.Enabled = $chkForce.Checked }

function Load-ConfigToUi {
    param([string]$Text)
    $mod = Get-ConfigValue $Text 'RW_SIZE_MOD'
    $size = Get-ConfigValue $Text 'RW_SIZE'
    if ($mod -eq 'FIXED') {
        $cmbMod.SelectedIndex = 1
        $n = 0; if ([int]::TryParse($size, [ref]$n)) { $numFixed.Value = [Math]::Min($n, 100000) }
    } else {
        $cmbMod.SelectedIndex = 0
        $map = @{ 'S' = $numS; 'V' = $numV; 'P' = $numP; 'SE' = $numSE; 'OT' = $numOT }
        foreach ($m in [regex]::Matches($size, '(\w+)=(\d+)%')) {
            $k = $m.Groups[1].Value
            if ($map.ContainsKey($k)) { $map[$k].Value = [Math]::Min([int]$m.Groups[2].Value, 100) }
        }
    }
    $chkTimeout.Checked = ((Get-ConfigValue $Text 'REMOVE_TIMEOUT_KEY') -eq 'true')
    $chkForce.Checked   = ((Get-ConfigValue $Text 'FORCE_START') -eq 'true')
    $chkWrong.Checked   = ((Get-ConfigValue $Text 'IF_VALUE_WRONG') -eq 'true')
    $w = Get-ConfigValue $Text 'WHAT_DO_SCRIPT'; if ($cmbWhat.Items.Contains($w)) { $cmbWhat.SelectedItem = $w }
    $b = Get-ConfigValue $Text 'BACKUP_ORIGINAL_SUPER'; if ($cmbBak.Items.Contains($b)) { $cmbBak.SelectedItem = $b }
    $chkDfe.Checked    = ((Get-ConfigValue $Text 'DFE_PATCH') -eq 'true')
    $chkExt4.Checked   = ((Get-ConfigValue $Text 'IF_EXT4_MOUNT_PROBLEM_CONTINUE') -eq 'true')
    $chkNoData.Checked = ((Get-ConfigValue $Text 'IF_NO_DATA_CONTINUE') -eq 'true')
    Update-SizeUi; Update-ForceUi
}

function Build-ConfigFromUi {
    param([string]$Text)
    if ($cmbMod.SelectedIndex -eq 0) {
        $Text = Set-ConfigValue $Text 'RW_SIZE' ("S={0}% V={1}% P={2}% SE={3}% OT={4}%" -f [int]$numS.Value, [int]$numV.Value, [int]$numP.Value, [int]$numSE.Value, [int]$numOT.Value)
        $Text = Set-ConfigValue $Text 'RW_SIZE_MOD' 'MAX'
    } else {
        $Text = Set-ConfigValue $Text 'RW_SIZE' ([string][int]$numFixed.Value)
        $Text = Set-ConfigValue $Text 'RW_SIZE_MOD' 'FIXED'
    }
    $tf = { param($b) if ($b) { 'true' } else { 'false' } }
    $Text = Set-ConfigValue $Text 'REMOVE_TIMEOUT_KEY' (& $tf $chkTimeout.Checked)
    $Text = Set-ConfigValue $Text 'FORCE_START' (& $tf $chkForce.Checked)
    $Text = Set-ConfigValue $Text 'IF_VALUE_WRONG' (& $tf $chkWrong.Checked)
    if ($chkForce.Checked) {
        $Text = Set-ConfigValue $Text 'WHAT_DO_SCRIPT' ([string]$cmbWhat.SelectedItem)
        $Text = Set-ConfigValue $Text 'DFE_PATCH' (& $tf $chkDfe.Checked)
        $Text = Set-ConfigValue $Text 'BACKUP_ORIGINAL_SUPER' ([string]$cmbBak.SelectedItem)
        $Text = Set-ConfigValue $Text 'IF_EXT4_MOUNT_PROBLEM_CONTINUE' (& $tf $chkExt4.Checked)
        $Text = Set-ConfigValue $Text 'IF_NO_DATA_CONTINUE' (& $tf $chkNoData.Checked)
    } else {
        foreach ($k in @('WHAT_DO_SCRIPT', 'DFE_PATCH', 'BACKUP_ORIGINAL_SUPER', 'IF_EXT4_MOUNT_PROBLEM_CONTINUE', 'IF_NO_DATA_CONTINUE')) { $Text = Set-ConfigValue $Text $k '' }
    }
    return $Text
}

function Load-Zip {
    $p = $DefaultZip
    if (-not (Test-Path $p)) { $lblZipInfo.Text = 'החבילה עוד לא הורדה.'; $lblZipInfo.ForeColor = [System.Drawing.Color]::Firebrick; return $false }
    try {
        $cfg = Read-ZipConfig $p
        Load-ConfigToUi $cfg
        $mb = [Math]::Round((Get-Item $p).Length / 1MB, 1)
        $lblZipInfo.Text = "חבילת RO2RW 3.7.3.0s מוכנה ($mb MB)."
        $lblZipInfo.ForeColor = [System.Drawing.Color]::DarkGreen
        return $true
    } catch {
        $lblZipInfo.Text = 'שגיאה: ' + $_.Exception.Message
        $lblZipInfo.ForeColor = [System.Drawing.Color]::Firebrick
        return $false
    }
}

function Get-Ro2rwZip {
    $tmp = $DefaultZip + '.part'
    Download-File $Ro2rwUrl $tmp 'חבילת RO2RW'
    $fs = [IO.File]::OpenRead($tmp); $sig = New-Object byte[] 2; [void]$fs.Read($sig, 0, 2); $fs.Dispose()
    if ($sig[0] -ne 0x50 -or $sig[1] -ne 0x4B) { Remove-Item $tmp -Force; throw 'הקובץ שהתקבל אינו ZIP תקין. בדוק את חיבור האינטרנט ונסה שוב.' }
    Move-Item $tmp $DefaultZip -Force
    $statusLbl.Text = 'מוכן'
    return (Load-Zip)
}

function Ensure-Zip {
    if ((Test-Path $DefaultZip) -and (Load-Zip)) { return $true }
    try { return (Get-Ro2rwZip) } catch { Write-Err ('הורדת החבילה נכשלה: ' + $_.Exception.Message); $statusLbl.Text = 'מוכן'; return $false }
}

function Prepare-CustomZip {
    if (-not (Ensure-Zip)) { throw 'חבילת RO2RW אינה זמינה.' }
    $sum = [int]($numS.Value + $numV.Value + $numP.Value + $numSE.Value + $numOT.Value)
    if ($cmbMod.SelectedIndex -eq 0 -and $sum -gt 100) { throw 'סכום האחוזים גדול מ-100%.' }
    $cfg = Build-ConfigFromUi (Read-ZipConfig $DefaultZip)
    Write-CustomZip -SourceZip $DefaultZip -TargetZip $CustomZip -ConfigText $cfg
    Write-Info ('נבנה ZIP מותאם: ' + $CustomZip)
    return $CustomZip
}

function Update-DeviceStatus {
    $adb = Get-AdbPath
    if (-not $adb) {
        $lblAdb.Text = 'ADB: לא נמצא - לחץ "הורד ADB"'
        $lblAdb.ForeColor = [System.Drawing.Color]::Firebrick
        $lblDevice.Text = 'מכשיר: -'
        return
    }
    $lblAdb.Text = 'ADB: ' + $adb
    $lblAdb.ForeColor = [System.Drawing.Color]::DarkGreen
    $state = Invoke-AdbQuick 'get-state' 5000
    if (-not $state -or $state -match 'error|no devices') {
        $devs = Invoke-AdbQuick 'devices' 5000
        if ($devs -match 'unauthorized') { $lblDevice.Text = 'מכשיר: לא מאושר - אשר את המחשב במסך הטלפון'; $lblDevice.ForeColor = [System.Drawing.Color]::DarkOrange }
        else { $lblDevice.Text = 'מכשיר: לא מחובר'; $lblDevice.ForeColor = [System.Drawing.Color]::Firebrick }
        return
    }
    $model = ''
    if ($state -ne 'sideload') { $model = Invoke-AdbQuick 'shell getprop ro.product.model' 5000 }
    $names = @{ 'device' = 'מערכת פועלת'; 'recovery' = 'Recovery (TWRP)'; 'sideload' = 'מצב Sideload'; 'bootloader' = 'Fastboot' }
    $nice = $state; if ($names.ContainsKey($state)) { $nice = $names[$state] }
    $lblDevice.Text = "מכשיר: $model  |  מצב: $nice"
    $lblDevice.ForeColor = [System.Drawing.Color]::DarkGreen
    $statusLbl.Text = "מחובר: $nice"
}

function Download-File {
    param([string]$Url, [string]$Target, [string]$What)
    $statusLbl.Text = "מוריד $What..."
    Write-Info "מוריד $What..."
    [System.Windows.Forms.Application]::DoEvents()
    $wc = New-Object System.Net.WebClient
    $wc.Headers.Add('User-Agent', 'Mozilla/5.0 (Windows NT 10.0; Win64; x64)')
    $script:dlDone = $false; $script:dlErr = $null; $script:dlWhat = $What
    $wc.add_DownloadProgressChanged({ param($s, $e) $statusLbl.Text = "מוריד $($script:dlWhat)... $($e.ProgressPercentage)%" })
    $wc.add_DownloadFileCompleted({ param($s, $e) $script:dlErr = $e.Error; $script:dlDone = $true })
    $wc.DownloadFileAsync([Uri]$Url, $Target)
    while (-not $script:dlDone) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 50 }
    $wc.Dispose()
    if ($script:dlErr) { throw $script:dlErr }
    Write-Ok "ההורדה הושלמה: $Target"
}

function Confirm-Run {
    param([string]$msg)
    $r = [System.Windows.Forms.MessageBox]::Show($msg + "`n`nלהמשיך?", 'אישור הפעלה', 'YesNo', 'Warning', 'Button2', 'RtlReading, RightAlign')
    return ($r -eq 'Yes')
}

function Require-State {
    param([string[]]$Allowed, [string]$Hint)
    $state = Invoke-AdbQuick 'get-state' 5000
    if ($Allowed -notcontains $state) {
        [System.Windows.Forms.MessageBox]::Show("המכשיר לא במצב המתאים.`n$Hint`n`nמצב נוכחי: $state", 'RO2RW', 'OK', 'Information', 'Button1', 'RtlReading, RightAlign') | Out-Null
        return $false
    }
    return $true
}

function Start-Run {
    $remote = $RemoteZip
    $mode = $cmbMode.SelectedIndex
    Save-Settings

    if ($mode -le 2) {
        if (-not (Require-State @('recovery', 'sideload') 'יש להיכנס ל-TWRP (כפתור "אתחל ל-Recovery").')) { return }
    } else {
        if (-not (Require-State @('device') 'יש לאתחל את הטלפון למערכת, כש-Magisk מותקן.')) { return }
    }

    if ($mode -ne 4) {
        try { $zip = Prepare-CustomZip } catch { Write-Err $_.Exception.Message; return }
    }

    $bin = 'META-INF/com/google/android/update-binary'
    switch ($mode) {
        0 {
            if (-not (Confirm-Run 'RO2RW יורץ עכשיו ב-TWRP, והפלט יוצג כאן. הבחירות נעשות בלחצני העוצמה בטלפון.')) { return }
            $cmd = "mkdir -p /tmp/ro2rw_gui && cd /tmp/ro2rw_gui && unzip -o $remote $bin >/dev/null && sh /tmp/ro2rw_gui/$bin 3 1 $remote"
            Run-Steps @(
                (Step 'מעתיק את ה-ZIP לטלפון...' "push `"$zip`" $remote"),
                (Step 'מריץ RO2RW (בחירה בטלפון: עוצמה + = הבא, עוצמה - = בחירה)' "shell `"$cmd`"" -Filter)
            )
        }
        1 {
            if (-not (Confirm-Run 'ה-ZIP יותקן דרך TWRP. הפלט יוצג במסך הטלפון.')) { return }
            Run-Steps @(
                (Step 'מעתיק את ה-ZIP לטלפון...' "push `"$zip`" $remote"),
                (Step 'מתקין דרך TWRP...' "shell twrp install $remote")
            )
        }
        2 {
            if (-not (Confirm-Run 'TWRP יעבור למצב Sideload וה-ZIP יישלח אליו.')) { return }
            Run-Steps @(
                (Step 'מעביר את TWRP למצב Sideload...' 'shell twrp sideload' -AllowFail),
                (Step 'ממתין למצב Sideload...' 'wait-for-sideload'),
                (Step 'שולח את ה-ZIP...' "sideload `"$zip`"" -AllowFail)
            )
        }
        3 {
            if (-not (Confirm-Run 'כלי RO2RW יותקנו כמודול Magisk. נדרשת הרשאת root (אשר בטלפון).')) { return }
            $flash = '/data/user/0/ro2rw_gui/flash'
            $cmd = "su -c 'mkdir -p $flash && unzip -p $remote $bin > $flash/update-binary && sh $flash/update-binary 3 1 $remote'"
            Run-Steps @(
                (Step 'מעתיק את ה-ZIP לטלפון...' "push `"$zip`" $remote"),
                (Step 'מתקין את המודול...' "shell `"$cmd`"")
            ) { Write-Ok 'אחרי האתחול בחר "Magisk - הרצת RO2RW" ולחץ "הפעל".' }
        }
        4 {
            if (-not (Confirm-Run 'RO2RW יורץ במערכת הפועלת. בשאלות הקלד מספר בתיבה למטה ולחץ "שלח".')) { return }
            Run-Steps @(
                (Step 'מריץ RO2RW במערכת (Magisk)...' "shell `"su -c 'sh /data/adb/modules/RO2RW/system/bin/RO2RW'`"")
            )
            $txtInput.Focus() | Out-Null
        }
    }
}

function Pull-Logs {
    $state = Invoke-AdbQuick 'get-state' 5000
    if (@('device', 'recovery') -notcontains $state) { Write-Err 'אין מכשיר מחובר.'; return }
    $target = Join-Path $LogsDir ('NEO.LOGS-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Path $target | Out-Null
    Run-Steps @(
        (Step 'מושך יומנים מ-/sdcard/NEO.LOGS...' "pull /sdcard/NEO.LOGS `"$target`"" -AllowFail)
    ) { Write-Ok ('היומנים נשמרו ב: ' + $target); Start-Process explorer.exe $target }
}

# ---------------------------------------------------------------- אירועים
$cmbMod.Add_SelectedIndexChanged({ Update-SizeUi })
foreach ($n in @($numS, $numV, $numP, $numSE, $numOT)) { $n.Add_ValueChanged({ Update-SizeUi }) }
$chkForce.Add_CheckedChanged({ Update-ForceUi })

$btnRefresh.Add_Click({ Update-DeviceStatus })
$btnDrivers.Add_Click({
    [System.Windows.Forms.MessageBox]::Show(@'
1. בטלפון: הגדרות > אודות הטלפון > הקש 7 פעמים על "מספר Build".
2. הגדרות > אפשרויות למפתחים > הפעל "ניפוי באגים ב-USB".
3. חבר כבל USB ואשר את ההודעה "לאפשר ניפוי באגים" בטלפון.
4. אם הטלפון לא מזוהה, התקן את מנהל ההתקן Google USB Driver או את מנהל ההתקן של היצרן.
5. ב-TWRP אין צורך באישור, ה-ADB פעיל אוטומטית.
'@, 'עזרה בחיבור', 'OK', 'Information', 'Button1', 'RtlReading, RightAlign') | Out-Null
})

$btnGetAdb.Add_Click({
    try {
        $tmp = Join-Path $WorkDir 'platform-tools.zip'
        Download-File $AdbUrl $tmp 'ADB (platform-tools)'
        if (Test-Path $ToolsDir) { Remove-Item $ToolsDir -Recurse -Force }
        [IO.Compression.ZipFile]::ExtractToDirectory($tmp, $AppDir)
        Remove-Item $tmp -Force
        Write-Ok 'ADB הותקן בתיקיית התוכנה.'
    } catch { Write-Err ('הורדת ADB נכשלה: ' + $_.Exception.Message) }
    Update-DeviceStatus
})

$btnDlZip.Add_Click({
    try { [void](Get-Ro2rwZip) } catch { Write-Err ('ההורדה נכשלה: ' + $_.Exception.Message) }
    $statusLbl.Text = 'מוכן'
})

$btnRun.Add_Click({ Start-Run })
$btnStop.Add_Click({ Write-Err 'נעצר על ידי המשתמש.'; Stop-All })
$btnLogs.Add_Click({ Pull-Logs })
$btnClear.Add_Click({ $txtLog.Clear() })
$btnOpenDir.Add_Click({ Start-Process explorer.exe $AppDir })
$btnSend.Add_Click({ Send-Input })
$txtInput.Add_KeyDown({ param($s, $e) if ($e.KeyCode -eq 'Enter') { $e.SuppressKeyPress = $true; Send-Input } })

$btnRecovery.Add_Click({ Run-Steps @((Step 'מאתחל ל-Recovery...' 'reboot recovery' -AllowFail)) })
$btnSystem.Add_Click({ Run-Steps @((Step 'מאתחל למערכת...' 'reboot' -AllowFail)) })
$btnFastboot.Add_Click({ Run-Steps @((Step 'מאתחל ל-Fastboot...' 'reboot bootloader' -AllowFail)) })

$form.Add_Shown({
    Load-Settings
    Update-SizeUi; Update-ForceUi
    Update-DeviceStatus
    Set-Busy $false
    [void](Ensure-Zip)
    Write-Log "RO2RW Manager מוכן.`nשלבים: 1) חבר טלפון  2) בדוק הגדרות  3) בחר שיטה ולחץ 'הפעל'.`n" ([System.Drawing.Color]::LightGreen)
})
$form.Add_FormClosing({ Save-Settings; Stop-All })

[void]$form.ShowDialog()
