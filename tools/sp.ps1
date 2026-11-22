<#
SP Flash Tool from the terminal (console mode, flash_tool.exe -b -i config.xml).

  .\sp.ps1 readback -Chip MT6765 -Out D:\dump -Partitions boot,vbmeta,super
  .\sp.ps1 readback -Chip MT6765 -Out D:\dump -Scatter X_scatter.txt        # every partition in the scatter (no userdata)
  .\sp.ps1 flash    -Chip MT6765 -Files boot=D:\boot.img,vbmeta=D:\vbmeta.img
  .\sp.ps1 flash    -Chip MT6765 -Scatter X_scatter.txt -Dir D:\edited       # partitions marked is_download in the scatter

Download only, never format. Add -DryRun to just write the config, -Port COM5 for UART/VCOM, -Ufs for UFS storage.
SP location: -Sp <flash_tool.exe>, or $env:SP_FLASH_TOOL, or C:\RO2RW\tools\SPFlashTool, or the Downloads copy.
#>
param(
    [Parameter(Mandatory, Position = 0)][ValidateSet('readback', 'flash')][string]$Mode,
    [Parameter(Mandatory)][ValidatePattern('^MT[0-9]{4}[A-Z]?$')][string]$Chip,
    [string]$Out,
    [string[]]$Partitions,
    [string]$Scatter,
    [string[]]$Files,
    [string]$Dir,
    [string]$Sp,
    [string]$Da,
    [string]$Port,
    [switch]$Ufs,
    [switch]$DryRun,
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
# "powershell -File sp.ps1 ... -Partitions a,b" (and cmd.exe) pass "a,b" as ONE string, so split commas ourselves
if ($Partitions) { $Partitions = @($Partitions | ForEach-Object { $_ -split ',' } | Where-Object { $_ }) }
if ($Files) { $Files = @($Files | ForEach-Object { $_ -split ',(?=[^,=]+=)' } | Where-Object { $_ }) }

if (-not $Sp) {
    $Sp = @($env:SP_FLASH_TOOL, 'C:\RO2RW\tools\SPFlashTool\flash_tool.exe',
        'D:\Downloads\SP_Flash_Tool_v5.1924_Win\SP_Flash_Tool_v5.1924_Win\flash_tool.exe') |
        Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}
if (-not $Sp -or -not (Test-Path $Sp)) { throw 'flash_tool.exe not found; pass -Sp' }
$spDir = Split-Path -Parent (Resolve-Path $Sp)
if (-not $Da) { $Da = Join-Path $spDir 'MTK_AllInOne_DA.bin' }
if (-not (Test-Path $Da)) { throw "DA not found: $Da" }

function Esc($s) { [Security.SecurityElement]::Escape($s) }
function ScatterNames($path, $all) {
    $names = foreach ($l in Get-Content $path) {
        if ($l -match '^\s*-?\s*partition_name\s*:\s*(\S+)') { $Matches[1] }
    }
    $names | Where-Object { $_ -notin 'preloader', 'pgpt', 'sgpt', 'none' -and ($all -or $_ -ne 'userdata') } | Select-Object -Unique
}

$conn = if ($Port) { "<connection type=`"BromUART`" port=`"$(Esc $Port)`" timeout-count=`"3600000`" />" }
        else { '<connection type="BromUSB" high-speed="true" power="AutoDetect" timeout-count="3600000" com-port="" />' }

$work = Join-Path ([IO.Path]::GetTempPath()) ('sp_cli_' + (Get-Date -Format 'yyyyMMdd_HHmmss') + '_' + [guid]::NewGuid().ToString('N').Substring(0, 6))
New-Item -ItemType Directory $work | Out-Null

if ($Mode -eq 'readback') {
    if (-not $Out) { throw '-Out is required for readback' }
    if (-not $Partitions) {
        if (-not $Scatter) { throw 'give -Partitions or -Scatter' }
        $Partitions = @(ScatterNames $Scatter $false)
    }
    New-Item -ItemType Directory -Force $Out | Out-Null
    $items = ($Partitions | ForEach-Object {
        "        <readback-rom-item readback-partion=`"$(Esc $_)`">$(Esc (Join-Path (Resolve-Path $Out) "$_.img"))</readback-rom-item>" }) -join "`r`n"
    $cmd = "<readback-withoutscatter><readback-list>`r`n$items`r`n</readback-list></readback-withoutscatter>"
    $scatterXml = ''
} else {
    $pairs = @()
    if ($Files) {
        foreach ($f in $Files) { $n, $p = $f -split '=', 2; $pairs += , @($n, (Resolve-Path $p).Path) }
    } elseif ($Scatter -and $Dir) {
        $cur = $null; $dl = $false; $file = $null
        $flush = { if ($cur -and $dl -and $file -and $file -ne 'NONE' -and (Test-Path (Join-Path $Dir (Split-Path $file -Leaf)))) { $script:pairs += , @($cur, (Join-Path (Resolve-Path $Dir) (Split-Path $file -Leaf))) } }
        foreach ($l in Get-Content $Scatter) {
            if ($l -match '^\s*-?\s*partition_index\s*:') { & $flush; $cur = $null; $dl = $false; $file = $null }
            elseif ($l -match '^\s*partition_name\s*:\s*(\S+)') { $cur = $Matches[1] }
            elseif ($l -match '^\s*file_name\s*:\s*(\S+)') { $file = $Matches[1] }
            elseif ($l -match '^\s*is_download\s*:\s*(\S+)') { $dl = $Matches[1] -eq 'true' }
        }
        & $flush
    } else { throw 'give -Files name=path,... or -Scatter with -Dir' }
    if (-not $pairs) { throw 'nothing to flash' }
    $items = ($pairs | ForEach-Object { "        <download-rom-item partition=`"$(Esc $_[0])`">$(Esc $_[1])</download-rom-item>" }) -join "`r`n"
    $cmd = "<download-withoutScatter><download-list>`r`n$items`r`n</download-list></download-withoutScatter>"
    Write-Host ("Flash: " + (($pairs | ForEach-Object { $_[0] }) -join ', '))
}

$cfg = Join-Path $work 'config.xml'
@"
<?xml version="1.0" encoding="UTF-8" ?>
<flashtool-config version="2.0">
  <general>
    <chip-name>$Chip</chip-name>
    <storage-type>$(if ($Ufs) { 'UFS' } else { 'EMMC' })</storage-type>
    <download-agent>$(Esc $Da)</download-agent>
    <scatter></scatter>
    <rom-list></rom-list>
    $conn
  </general>
  <commands>
    $cmd
  </commands>
</flashtool-config>
"@ | Set-Content -Encoding UTF8 $cfg
Write-Host "config: $cfg"
if ($DryRun) { exit 0 }

if ($Mode -eq 'flash' -and -not $Force) {
    if ((Read-Host 'Flash to the device? (y/N)') -ne 'y') { exit 1 }
}
Write-Host 'Connect the powered-off device now...'
$a = @('-b', '-i', "`"$cfg`""); if ($Port) { $a += @('-p', $Port) }
$p = Start-Process -FilePath $Sp -ArgumentList $a -WorkingDirectory $spDir -NoNewWindow -Wait -PassThru
Write-Host "exit code: $($p.ExitCode)"
if ($Mode -eq 'readback') {
    foreach ($n in $Partitions) {
        $f = Join-Path $Out "$n.img"
        if ((Test-Path $f) -and (Get-Item $f).Length -gt 0) { '{0,-16} {1,12:N0} bytes' -f $n, (Get-Item $f).Length }
        else { Write-Warning "$n not read" }
    }
}
exit $p.ExitCode
