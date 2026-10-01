<#
.SYNOPSIS
    Benchmarks AL Runner on Microsoft's complete BaseApp test surface and writes one result
    zip to send back. Issue #5127. Read tools/benchmark/README.md first.

.DESCRIPTION
    Everything lives under -WorkDir: a private .NET runtime, the pinned runner, the pinned
    backup reader, the BC artifacts, the caches and the results. Nothing is installed
    globally, PATH and Defender are not touched, and no administrator rights are needed.
    Delete -WorkDir and the machine is as it was.

    The run either ends with a result zip whose every number was checked, or stops early
    with a message saying what to do. A bucket that produced no number is reported as
    missing in the zip and the script exits 1; it never reports a partial run as complete.

    Re-running the same command resumes: finished downloads and finished buckets are kept.

    Exit codes:
      0  complete: every bucket produced a number, result zip written
      1  finished, but at least one bucket produced no number (the zip says which)
      2  stopped before running anything: a preflight check failed (the message says why)
      3  stopped after the smoke bucket: its result was not sane, so the full run would be
         a wrong number

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\ms-surface-benchmark.ps1
#>
# Must PARSE under Windows PowerShell 5.1: a volunteer's default shell is 5.1, and this file
# relaunches itself under a private PowerShell 7 (Start-Bootstrap). So no `??`, `?.`,
# ternary, `&&`/`||` or `-Parallel` anywhere in this file.
[CmdletBinding()]
param(
    # Where everything goes. Keep it short on Windows (see the long-path check).
    [string] $WorkDir = '',
    # A subset of buckets, comma-separated. Empty = the full surface.
    [string] $Buckets = '',
    # Worker processes. 0 = derive from measured free memory (recommended).
    [int] $Jobs = 0,
    # Stop after the checks and downloads; run nothing.
    [switch] $CheckOnly,
    # Forget finished buckets and measure everything again (downloads are kept).
    [switch] $Fresh,
    # Testing hooks: make the preflight see less memory or disk than there is, so the
    # stop messages can be exercised on a machine that has plenty. Not for volunteers.
    [double] $AssumeFreeMemoryGB = -1,
    [double] $AssumeFreeDiskGB = -1,
    # Testing hook: point the reader at a file that does not exist, so the smoke bucket
    # cannot read the backup — the "Tests: 0" trap the smoke check exists to catch.
    [switch] $SimulateBrokenReader,
    # Testing hook: replace the BC artifact host so the network check fails.
    [string] $ArtifactHostOverride = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

# ─── Pinned inputs ────────────────────────────────────────────────────────────────────────
# Change these together and re-run the Windows workflow (.github/workflows/
# ms-surface-benchmark-windows.yml) before handing out a new command.
$Pins = @{
    RunnerVersion   = '2.12.0'
    RunnerUrl       = 'https://api.nuget.org/v3-flatcontainer/msdyn365bc.al.runner/2.12.0/msdyn365bc.al.runner.2.12.0.nupkg'
    RunnerSha256    = 'B9E4A7E02A9BE65D3AAE1933296BD16120AC25B32BB174CEAB39A3AB11B85CD7'
    # The exact build 2.12.0's 28.5 engine variant was compiled against. A newer 28.5 build
    # crashes that variant at startup (System.Diagnostics.EventLog 10.0.0.0 cannot load),
    # so this must stay equal to a variant shipped in the pinned runner package.
    BcVersion       = '28.5.54151.55364'
    ReaderTag       = 'v0.1.2'
    DotnetVersion   = '8.0.31'
    PwshVersion     = '7.4.20'
}
# SHA256 for the reader (DbReader v0.1.2 SHA256SUMS), SHA512 for .NET (Microsoft's
# releases.json), SHA256 for PowerShell (the release's hashes.sha256).
$Downloads = @{
    'reader-win-x64'   = @{ Url = 'https://github.com/StefanMaron/BusinessCentral.DbReader/releases/download/v0.1.2/bcdb-win-x64.exe';  Algo = 'SHA256'; Hash = 'D54087AC4F762D6AC7AC950A25B863AB975E23CB31FFCE041690E59ED6D899D3' }
    'reader-linux-x64' = @{ Url = 'https://github.com/StefanMaron/BusinessCentral.DbReader/releases/download/v0.1.2/bcdb-linux-x64';    Algo = 'SHA256'; Hash = 'DCF9E508DE450AA44BA89361CCB41368118B10C97D37CF9E8EACCDF52DDE70F6' }
    'dotnet-win-x64'   = @{ Url = 'https://builds.dotnet.microsoft.com/dotnet/Runtime/8.0.31/dotnet-runtime-8.0.31-win-x64.zip';      Algo = 'SHA512'; Hash = '9C55C58694676EE64B0EED2CD6D8CBF58B9AA8288420ACC66841E15CA0099C75D4AF0182D23A641C2342E5A151A325DF4A12FA0BDE2E47C0FB7E9A33E7B09896' }
    'dotnet-linux-x64' = @{ Url = 'https://builds.dotnet.microsoft.com/dotnet/Runtime/8.0.31/dotnet-runtime-8.0.31-linux-x64.tar.gz'; Algo = 'SHA512'; Hash = 'F336BDEC58D54BF50D74A1B38EFA82F7290D976BD2ED98B845EBCBAC42CF0D8CEF504684FC088D4B05F98737B996BF3302E52BD9C23005DEAE7C60780B2652FB' }
    'pwsh-win-x64'     = @{ Url = 'https://github.com/PowerShell/PowerShell/releases/download/v7.4.20/PowerShell-7.4.20-win-x64.zip'; Algo = 'SHA256'; Hash = 'FB88CD3731847006B157978D6C0A426390D346696D91D1ED85B5E5EB88CB2D40' }
}

# The non-empty buckets, in the order and with the reference numbers of the last full Linux
# CI measurement: ms-surface.yml run 35704652590 (BC 28.4.53241.54929, runner from main,
# hosted 4-vCPU runner, one bucket at a time). Minutes order the queue (longest first) and
# set the floor below which more workers cannot shorten the run; Tests are the sanity
# check on each bucket's count. Same list as ms-surface.yml (MsSurfaceWorkflowTests holds
# that one to the artifact's inventory).
$Reference = [ordered]@{
    'Tests-ERM'                      = @{ Tests = 9572; Minutes = 28.1 }
    'Tests-SCM'                      = @{ Tests = 8536; Minutes = 59.4 }
    'Tests-SCM-Service'              = @{ Tests = 1903; Minutes = 18.4 }
    'Tests-SCM-Assembly'             = @{ Tests = 1776; Minutes = 52.2 }
    'Tests-SCM-Manufacturing'        = @{ Tests = 1220; Minutes = 8.7 }
    'Tests-Misc'                     = @{ Tests = 3215; Minutes = 7.9 }
    'Tests-SINGLESERVER'             = @{ Tests = 885;  Minutes = 4.1 }
    'Tests-Bank'                     = @{ Tests = 671;  Minutes = 2.7 }
    'Tests-Data Exchange'            = @{ Tests = 399;  Minutes = 1.8 }
    'Tests-Rapid Start'              = @{ Tests = 387;  Minutes = 5.2 }
    'Tests-Graph'                    = @{ Tests = 77;   Minutes = 0.8 }
    'Tests-Monitor Sensitive Fields' = @{ Tests = 19;   Minutes = 0.7 }
    'Tests-Workflow'                 = @{ Tests = 1071; Minutes = 5.3 }
    'Tests-CRM integration'          = @{ Tests = 984;  Minutes = 2.0 }
    'Tests-Report'                   = @{ Tests = 711;  Minutes = 3.7 }
    'Tests-Cost Accounting'          = @{ Tests = 565;  Minutes = 1.2 }
    'Tests-Integration'              = @{ Tests = 340;  Minutes = 1.2 }
    'Tests-User'                     = @{ Tests = 73;   Minutes = 0.9 }
    'Tests-VAT'                      = @{ Tests = 1222; Minutes = 3.7 }
    'Tests-Dimension'                = @{ Tests = 982;  Minutes = 7.4 }
    'Tests-Marketing'                = @{ Tests = 736;  Minutes = 1.9 }
    'Tests-Fixed Asset'              = @{ Tests = 460;  Minutes = 2.0 }
    'Tests-Resource'                 = @{ Tests = 348;  Minutes = 1.7 }
    'Tests-Physical Inventory'       = @{ Tests = 157;  Minutes = 1.0 }
    'Tests-Job'                      = @{ Tests = 1303; Minutes = 5.6 }
    'Tests-SMB'                      = @{ Tests = 1028; Minutes = 8.5 }
    'Tests-General Journal'          = @{ Tests = 826;  Minutes = 3.3 }
    'Tests-Prepayment'               = @{ Tests = 572;  Minutes = 3.8 }
    'Tests-Cash Flow'                = @{ Tests = 401;  Minutes = 1.6 }
    'Tests-Reverse'                  = @{ Tests = 194;  Minutes = 1.2 }
    'Tests-Permissions'              = @{ Tests = 111;  Minutes = 1.2 }
    'Tests-Upgrade'                  = @{ Tests = 15;   Minutes = 0.5 }
}

# The smoke bucket must land in this window, with test data actually loaded. Reference:
# the nightly ms-bucket run 36811346743 measured Tests-SMB on BC 28.5.54151.55497 at
# 1,028 tests / 729 passed. A run that cannot read the backup reports 0 tests, or a few
# hundred passes; either way the full run would be a wrong number.
$SmokeBucket      = 'Tests-SMB'
$SmokeMinTests    = 950
$SmokeMaxTests    = 1150
$SmokeMinPassed   = 500
# A bucket whose count is this far from its reference is flagged in the summary. The
# reference is BC 28.4 and the run is 28.5, so small differences are expected.
$CountDeviationFlagPct = 10

$TestTimeoutFloorSec = 300      # ms-bucket.yml's default (#3431)
$EmitTimeoutSec      = 3600     # ms-bucket.yml's value
$MinFreeDiskGB       = 30
$MinTotalMemoryGB    = 12
$Company             = 'CRONUS International Ltd_'   # SQL form, trailing underscore

# ─── Small helpers (5.1-safe) ─────────────────────────────────────────────────────────────
$script:OnWindows = ($PSVersionTable.PSEdition -eq 'Desktop') -or ((Get-Variable IsWindows -ErrorAction SilentlyContinue) -and $IsWindows)
$script:LogFile = $null

function Write-Log([string] $Message, [string] $Color = '') {
    $line = '{0}  {1}' -f (Get-Date).ToString('HH:mm:ss'), $Message
    if ($Color) { Write-Host $line -ForegroundColor $Color } else { Write-Host $line }
    if ($script:LogFile) { Add-Content -LiteralPath $script:LogFile -Value $line -Encoding UTF8 }
}

# A stop the volunteer can act on: what happened, then what to do. Never a stack trace.
function Stop-Benchmark([int] $Code, [string] $What, [string[]] $Todo) {
    Write-Host ''
    Write-Log "STOPPED: $What" 'Red'
    foreach ($t in $Todo) { Write-Log "  -> $t" 'Yellow' }
    Write-Log "Nothing was sent anywhere. Fix the above and run the same command again; finished steps are kept." 'Yellow'
    exit $Code
}

function Get-Sha([string] $Path, [string] $Algo) {
    (Get-FileHash -LiteralPath $Path -Algorithm $Algo).Hash.ToUpperInvariant()
}

# Download to a .part file, verify, then rename — a killed download never looks finished.
function Get-VerifiedFile([string] $Url, [string] $Dest, [string] $Algo, [string] $Hash) {
    if (Test-Path -LiteralPath $Dest) {
        if ((Get-Sha $Dest $Algo) -eq $Hash.ToUpperInvariant()) { return }
        Remove-Item -LiteralPath $Dest -Force
    }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Dest) | Out-Null
    $part = "$Dest.part"
    Write-Log "Downloading $Url"
    $old = $ProgressPreference; $ProgressPreference = 'SilentlyContinue'   # 5.1 progress bar is 10x slower
    try { Invoke-WebRequest -Uri $Url -OutFile $part -UseBasicParsing }
    catch { Stop-Benchmark 2 "Could not download $Url ($($_.Exception.Message))." @('Check the internet connection, a proxy or a firewall blocking this host.') }
    finally { $ProgressPreference = $old }
    $got = Get-Sha $part $Algo
    if ($got -ne $Hash.ToUpperInvariant()) {
        Remove-Item -LiteralPath $part -Force
        Stop-Benchmark 2 "The file from $Url does not match its pinned checksum ($Algo $got, expected $Hash)." @(
            'The download was corrupted or replaced. Run again; if it repeats, tell the person who sent you this script and do not continue.')
    }
    Move-Item -LiteralPath $part -Destination $Dest -Force
}

function ConvertTo-Slug([string] $Name) { return ($Name -replace '[ /\\]', '_') }

# Quote one argument the way CommandLineToArgvW / .NET parse it back.
function ConvertTo-ArgString([string[]] $Arguments) {
    $parts = foreach ($a in $Arguments) {
        if ($a -ne '' -and $a -notmatch '[\s"]') { $a; continue }
        $sb = New-Object System.Text.StringBuilder
        [void]$sb.Append('"')
        $bs = 0
        foreach ($ch in $a.ToCharArray()) {
            if ($ch -eq '\') { $bs++; continue }
            if ($ch -eq '"') { [void]$sb.Append('\' * (2 * $bs + 1)); [void]$sb.Append('"'); $bs = 0; continue }
            if ($bs) { [void]$sb.Append('\' * $bs); $bs = 0 }
            [void]$sb.Append($ch)
        }
        [void]$sb.Append('\' * (2 * $bs))
        [void]$sb.Append('"')
        $sb.ToString()
    }
    return ($parts -join ' ')
}

function Get-FreeMemoryGB {
    if ($AssumeFreeMemoryGB -ge 0) { return [double]$AssumeFreeMemoryGB }
    if ($script:OnWindows) {
        # Available = free + standby, the number Task Manager shows. The perf class name is
        # not localised, unlike the "\Memory\Available MBytes" counter path.
        $m = Get-CimInstance -ClassName Win32_PerfFormattedData_PerfOS_Memory
        return [math]::Round([double]$m.AvailableMBytes / 1024, 2)
    }
    $line = (Get-Content /proc/meminfo | Where-Object { $_ -like 'MemAvailable:*' })
    return [math]::Round(([double](($line -split '\s+')[1])) / 1MB, 2)
}

function Get-TotalMemoryGB {
    if ($script:OnWindows) {
        return [math]::Round([double](Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB, 1)
    }
    $line = (Get-Content /proc/meminfo | Where-Object { $_ -like 'MemTotal:*' })
    return [math]::Round(([double](($line -split '\s+')[1])) / 1MB, 1)
}

function Get-FreeDiskGB([string] $Path) {
    if ($AssumeFreeDiskGB -ge 0) { return [double]$AssumeFreeDiskGB }
    $root = [System.IO.Path]::GetPathRoot([System.IO.Path]::GetFullPath($Path))
    if (-not $script:OnWindows) {
        $out = & df -Pk $Path | Select-Object -Last 1
        return [math]::Round(([double](($out -split '\s+')[3])) / 1MB, 1)
    }
    return [math]::Round((New-Object System.IO.DriveInfo $root).AvailableFreeSpace / 1GB, 1)
}

# ─── Bootstrap: relaunch under a private PowerShell 7 when started from 5.1 ───────────────
function Resolve-WorkDir {
    if ($WorkDir) { return [System.IO.Path]::GetFullPath($WorkDir) }
    $homeDir = [Environment]::GetFolderPath('UserProfile')
    return (Join-Path $homeDir 'al-runner-benchmark')
}

$script:Work = Resolve-WorkDir

if ($PSVersionTable.PSVersion.Major -lt 7 -or ($PSVersionTable.PSVersion.Major -eq 7 -and $PSVersionTable.PSVersion.Minor -lt 4)) {
    if (-not $script:OnWindows) {
        Stop-Benchmark 2 "PowerShell $($PSVersionTable.PSVersion) is too old." @('Install PowerShell 7.4 or newer (https://aka.ms/powershell) and run the script with pwsh.')
    }
    if (-not [Environment]::Is64BitOperatingSystem) {
        Stop-Benchmark 2 'This is a 32-bit Windows.' @('The benchmark needs 64-bit Windows 10 or 11 on an x64 processor.')
    }
    $pwshDir = Join-Path $script:Work 'tools\pwsh'
    $pwshExe = Join-Path $pwshDir 'pwsh.exe'
    if (-not (Test-Path -LiteralPath $pwshExe)) {
        Write-Host "This is Windows PowerShell $($PSVersionTable.PSVersion). Fetching a private PowerShell $($Pins.PwshVersion) into $pwshDir (not installed system-wide)."
        $d = $Downloads['pwsh-win-x64']
        $zip = Join-Path $script:Work 'downloads\pwsh.zip'
        Get-VerifiedFile $d.Url $zip $d.Algo $d.Hash
        if (Test-Path -LiteralPath $pwshDir) { Remove-Item -LiteralPath $pwshDir -Recurse -Force }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::ExtractToDirectory($zip, $pwshDir)
    }
    $argv = New-Object System.Collections.Generic.List[string]
    $argv.Add('-NoProfile'); $argv.Add('-ExecutionPolicy'); $argv.Add('Bypass'); $argv.Add('-File'); $argv.Add($PSCommandPath)
    foreach ($k in $PSBoundParameters.Keys) {
        $v = $PSBoundParameters[$k]
        if ($v -is [System.Management.Automation.SwitchParameter]) { if ($v.IsPresent) { $argv.Add("-$k") } }
        else { $argv.Add("-$k"); $argv.Add([string]$v) }
    }
    if (-not $PSBoundParameters.ContainsKey('WorkDir')) { $argv.Add('-WorkDir'); $argv.Add($script:Work) }
    & $pwshExe @argv
    exit $LASTEXITCODE
}

# ═══ From here on: PowerShell 7.4+ ═════════════════════════════════════════════════════════
$script:OnWindows = $IsWindows
Add-Type -AssemblyName System.IO.Compression.ZipFile
$ScriptSha = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
$SessionStamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')

$Dirs = [ordered]@{
    Tools     = Join-Path $script:Work 'tools'
    Downloads = Join-Path $script:Work 'downloads'
    Artifacts = Join-Path $script:Work 'artifacts'      # AL_RUNNER_ARTIFACTS_ROOT
    Cache     = Join-Path $script:Work 'cache'          # AL_RUNNER_CACHE_ROOT
    AlCache   = Join-Path $script:Work 'al-cache'       # --cache (private, as ms-bucket.yml)
    TestData  = Join-Path $script:Work 'testdata'
    BucketSrc = Join-Path $script:Work 'buckets'
    Tmp       = Join-Path $script:Work 'tmp'
    RunCwd    = Join-Path $script:Work 'run'            # cwd for the runner: no tests/expectations here
    Results   = Join-Path $script:Work 'results'
}
foreach ($d in $Dirs.Values) { New-Item -ItemType Directory -Force -Path $d | Out-Null }
$script:LogFile = Join-Path $Dirs.Results "benchmark-$SessionStamp.log"

# One run per folder at a time: two would share the caches and halve each other's numbers.
$lockPath = Join-Path $script:Work 'benchmark.lock'
try { $script:Lock = [System.IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None') }
catch { Stop-Benchmark 2 "Another benchmark is already running in $($script:Work)." @('Wait for it to finish, or close it, then run again.') }

Write-Log "AL Runner Microsoft-surface benchmark  (script sha256 $ScriptSha)" 'Cyan'
Write-Log "Working folder: $($script:Work)   (delete this folder to remove everything this script created)"

# ─── Preflight ────────────────────────────────────────────────────────────────────────────
$arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
if ($arch -ne 'X64') {
    Stop-Benchmark 2 "This machine is $arch." @('The pinned backup reader and engine are x64 only. Use an x64 machine.')
}
$rid = if ($script:OnWindows) { 'win-x64' } elseif ($IsLinux) { 'linux-x64' } else { '' }
if (-not $rid) { Stop-Benchmark 2 'macOS is not supported by this script.' @('Use Windows 10/11 or Linux, x64.') }

$machine = [ordered]@{
    os               = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
    osArchitecture   = $arch
    logicalProcessors = [Environment]::ProcessorCount
    totalMemoryGB    = Get-TotalMemoryGB
    pwsh             = $PSVersionTable.PSVersion.ToString()
}
if ($script:OnWindows) {
    try {
        $cpu = @(Get-CimInstance Win32_Processor)
        $machine.cpu = ($cpu | Select-Object -First 1).Name.Trim()
        $machine.physicalCores = ($cpu | Measure-Object -Property NumberOfCores -Sum).Sum
        $machine.cpuMaxClockMHz = ($cpu | Select-Object -First 1).MaxClockSpeed
        $osInfo = Get-CimInstance Win32_OperatingSystem
        $machine.windows = "$($osInfo.Caption) $($osInfo.Version) build $($osInfo.BuildNumber)"
    } catch { $machine.cpuInfoError = $_.Exception.Message }
    try {
        $letter = ([System.IO.Path]::GetPathRoot($script:Work)).Substring(0, 1)
        $disk = Get-Partition -DriveLetter $letter | Get-Disk
        $pd = Get-PhysicalDisk | Where-Object { $_.DeviceId -eq [string]$disk.Number }
        $machine.disk = "$($pd.FriendlyName) ($($pd.MediaType), $($pd.BusType))"
    } catch { $machine.disk = "unknown ($($_.Exception.Message))" }
    try { $machine.powerPlan = ((& powercfg /getactivescheme) -join ' ').Trim() } catch { }
    try {
        $mp = Get-MpComputerStatus
        $machine.defenderRealTime = [bool]$mp.RealTimeProtectionEnabled
    } catch { $machine.defenderRealTime = 'unknown' }
    try {
        $lp = Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem' -Name LongPathsEnabled -ErrorAction Stop
        $machine.longPathsEnabled = ($lp.LongPathsEnabled -eq 1)
    } catch { $machine.longPathsEnabled = $false }
} else {
    try {
        $machine.cpu = ((Get-Content /proc/cpuinfo | Where-Object { $_ -like 'model name*' } | Select-Object -First 1) -split ':', 2)[1].Trim()
        $machine.physicalCores = @((Get-Content /proc/cpuinfo | Where-Object { $_ -like 'core id*' }) | Sort-Object -Unique).Count
    } catch { }
}
Write-Log ("Machine: {0}; {1} logical processors; {2} GB RAM; {3}" -f $machine.cpu, $machine.logicalProcessors, $machine.totalMemoryGB, $machine.os)

$freeDisk = Get-FreeDiskGB $script:Work
$freeMem  = Get-FreeMemoryGB
Write-Log "Free disk on the working folder's drive: $freeDisk GB; available memory: $freeMem GB"
if ($machine.totalMemoryGB -lt $MinTotalMemoryGB -or $freeMem -lt 6) {
    Stop-Benchmark 2 "Not enough memory: $($machine.totalMemoryGB) GB installed, $freeMem GB available. One worker on the largest bucket needs about 6 GB available, and the machine needs at least $MinTotalMemoryGB GB installed." @(
        'Close other programs and run again, or use a machine with more memory.')
}
if ($freeDisk -lt $MinFreeDiskGB) {
    Stop-Benchmark 2 "Not enough free disk space: $freeDisk GB free where the working folder is, $MinFreeDiskGB GB needed." @(
        'Free some space, or pass -WorkDir pointing at a drive with more room, e.g. -WorkDir D:\alrb')
}
if ($script:OnWindows -and -not $machine.longPathsEnabled -and $script:Work.Length -gt 40) {
    Stop-Benchmark 2 "The working folder path is $($script:Work.Length) characters long and Windows long paths are off. The runner's caches nest deep enough to pass the 260-character limit." @(
        'Pass a short folder, e.g. -WorkDir C:\alrb (run again with that added to the command).')
}
if ($script:OnWindows -and $machine.defenderRealTime -eq $true) {
    Write-Log "Microsoft Defender real-time protection is ON. It scans every file the runner writes and can slow the run noticeably." 'Yellow'
    Write-Log "  This script does not change Defender. If you want, exclude the working folder yourself (admin PowerShell):" 'Yellow'
    Write-Log "    Add-MpPreference -ExclusionPath '$($script:Work)'" 'Yellow'
    Write-Log "  and remove it afterwards with Remove-MpPreference -ExclusionPath '$($script:Work)'. The result records whether it was on." 'Yellow'
}

$artifactHost = 'https://bcartifacts-exdbf9fwegejdqak.b02.azurefd.net'
if ($ArtifactHostOverride) { $artifactHost = $ArtifactHostOverride }
$probes = [ordered]@{
    'BC artifacts (Microsoft CDN)' = "$artifactHost/sandbox/$($Pins.BcVersion)/platform"
    'NuGet (the runner package)'   = $Pins.RunnerUrl
    'GitHub (the backup reader)'   = $Downloads["reader-$rid"].Url
    '.NET runtime download'        = $Downloads["dotnet-$rid"].Url
}
foreach ($name in $probes.Keys) {
    try {
        $r = Invoke-WebRequest -Uri $probes[$name] -Method Head -TimeoutSec 30 -MaximumRedirection 5 -UseBasicParsing
        if ([int]$r.StatusCode -ge 400) { throw "HTTP $($r.StatusCode)" }
    } catch {
        Stop-Benchmark 2 "Cannot reach $name at $($probes[$name]): $($_.Exception.Message)" @(
            'Check the internet connection. A company proxy or firewall may block this host; try from another network.')
    }
}
Write-Log 'Network: all four download hosts answer.'

# ─── Tools: .NET runtime, runner, reader — all private to the working folder ──────────────
function Install-Tools {
    $dn = $Downloads["dotnet-$rid"]
    $dotnetDir = Join-Path $Dirs.Tools "dotnet-$($Pins.DotnetVersion)"
    $dotnetExe = Join-Path $dotnetDir ($(if ($script:OnWindows) { 'dotnet.exe' } else { 'dotnet' }))
    if (-not (Test-Path -LiteralPath (Join-Path $dotnetDir '.complete'))) {
        $arc = Join-Path $Dirs.Downloads ([System.IO.Path]::GetFileName($dn.Url))
        Get-VerifiedFile $dn.Url $arc $dn.Algo $dn.Hash
        if (Test-Path -LiteralPath $dotnetDir) { Remove-Item -LiteralPath $dotnetDir -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $dotnetDir | Out-Null
        if ($script:OnWindows) { [System.IO.Compression.ZipFile]::ExtractToDirectory($arc, $dotnetDir) }
        else { & tar -xzf $arc -C $dotnetDir; if ($LASTEXITCODE) { Stop-Benchmark 2 "Could not unpack $arc." @('Delete the downloads folder and run again.') } }
        Set-Content -LiteralPath (Join-Path $dotnetDir '.complete') -Value $dn.Hash
    }

    $runnerDir = Join-Path $Dirs.Tools "al-runner-$($Pins.RunnerVersion)"
    if (-not (Test-Path -LiteralPath (Join-Path $runnerDir '.complete'))) {
        $pkg = Join-Path $Dirs.Downloads "msdyn365bc.al.runner.$($Pins.RunnerVersion).nupkg"
        Get-VerifiedFile $Pins.RunnerUrl $pkg 'SHA256' $Pins.RunnerSha256
        if (Test-Path -LiteralPath $runnerDir) { Remove-Item -LiteralPath $runnerDir -Recurse -Force }
        # A .nupkg is a zip. Extracting it ourselves avoids `dotnet tool install`, which
        # needs the SDK and writes outside this folder.
        [System.IO.Compression.ZipFile]::ExtractToDirectory($pkg, $runnerDir)
        Set-Content -LiteralPath (Join-Path $runnerDir '.complete') -Value $Pins.RunnerSha256
    }
    $runnerDll = Join-Path $runnerDir 'tools/net8.0/any/al-runner.dll'

    $rd = $Downloads["reader-$rid"]
    $readerExe = Join-Path $Dirs.Tools ("bcbak-$($Pins.ReaderTag)/" + $(if ($script:OnWindows) { 'bcbak.exe' } else { 'bcbak' }))
    Get-VerifiedFile $rd.Url $readerExe $rd.Algo $rd.Hash
    if (-not $script:OnWindows) { & chmod +x $readerExe }

    foreach ($p in @($dotnetExe, $runnerDll, $readerExe)) {
        if (-not (Test-Path -LiteralPath $p)) { Stop-Benchmark 2 "Expected file missing after unpacking: $p" @("Delete $($Dirs.Tools) and run again.") }
    }
    return @{ Dotnet = $dotnetExe; Runner = $runnerDll; RunnerDir = Split-Path -Parent $runnerDll; Reader = $readerExe }
}
$Tools = Install-Tools

$readerVersion = (& $Tools.Reader --version 2>&1 | Select-Object -First 1)
if ($LASTEXITCODE -ne 0) {
    Stop-Benchmark 2 "The backup reader does not start: $readerVersion" @('Your antivirus may have blocked it. Check its quarantine, then run again.')
}
Write-Log "Runner $($Pins.RunnerVersion); backup reader: $readerVersion; .NET $($Pins.DotnetVersion) (private copy)"

# Process-scoped environment for every runner process. Nothing here outlives this script.
$env:DOTNET_ROOT = Split-Path -Parent $Tools.Dotnet
$env:DOTNET_MULTILEVEL_LOOKUP = '0'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:AL_RUNNER_ARTIFACTS_ROOT = $Dirs.Artifacts
$env:AL_RUNNER_CACHE_ROOT = $Dirs.Cache
# The runner probes <cache>/bcbak/bcbak without an .exe suffix, so on Windows the cache
# probe cannot find the reader; naming the file directly works on every OS.
$env:AL_RUNNER_BCBAK = $Tools.Reader
if ($SimulateBrokenReader) { $env:AL_RUNNER_BCBAK = Join-Path $Dirs.Tools 'no-such-reader/bcbak' }
$env:TMP = $Dirs.Tmp; $env:TEMP = $Dirs.Tmp; $env:TMPDIR = $Dirs.Tmp
$env:AL_RUNNER_EMIT_TIMEOUT_SEC = [string]$EmitTimeoutSec
# The same per-process GC settings the runner's own --jobs gives each worker
# (ParallelFanOut.WorkerEnvironment): one heap, conserve memory, no background GC. Soft
# knobs — they cost time, never results. DOTNET_GCHeapHardLimit is deliberately absent:
# it silently changes test results (#2712).
$env:DOTNET_GCHeapCount = '1'
$env:DOTNET_GCConserveMemory = '9'
$env:DOTNET_gcConcurrent = '0'

# ─── BC artifacts, bucket sources, backup ─────────────────────────────────────────────────
# Through the runner package's own provisioning library, the code tools/DownloadArtifacts
# and ms-bucket.yml use, rather than a second spelling of the CDN's ZIP layout.
Add-Type -LiteralPath (Join-Path $Tools.RunnerDir 'AlRunner.Provisioning.dll')
# The library may log from a thread-pool thread, where a PowerShell script block cannot run,
# so its log callback is a plain .NET method.
Add-Type -TypeDefinition @'
public static class BenchmarkLog
{
    private static readonly object Gate = new object();
    public static string FilePath;
    public static void Write(string message)
    {
        lock (Gate)
        {
            var line = System.DateTime.Now.ToString("HH:mm:ss") + "    " + message;
            System.Console.WriteLine(line);
            if (FilePath != null) System.IO.File.AppendAllText(FilePath, line + System.Environment.NewLine);
        }
    }
}
'@
[BenchmarkLog]::FilePath = $script:LogFile
$logAction = [System.Delegate]::CreateDelegate([Action[string]], [BenchmarkLog].GetMethod('Write'))

function Invoke-Provision([string] $Marker, [string] $What, [scriptblock] $Do) {
    if (Test-Path -LiteralPath $Marker) { return }
    Write-Log "Fetching $What ..."
    $rc = & $Do
    if ($rc -ne 0) {
        Stop-Benchmark 2 "Could not fetch $What from Microsoft's artifact CDN (exit $rc)." @(
            'This is usually a network interruption. Run the same command again; it continues where it stopped.')
    }
    Set-Content -LiteralPath $Marker -Value (Get-Date).ToString('o')
}

$bc = $Pins.BcVersion
$platformApps = Join-Path $Dirs.Artifacts "$bc/platform-apps"
$testApps     = Join-Path $Dirs.Artifacts "$bc/test-apps"
$bakDir       = Join-Path $Dirs.TestData $bc
$bakPath      = Join-Path $bakDir 'BusinessCentral-W1.bak'

Invoke-Provision (Join-Path $platformApps '.benchmark-complete') "the BC $bc platform apps" { [AlRunner.Provisioning.ArtifactDownloader]::PlatformApps($bc, $platformApps, 'w1', $logAction) }
Invoke-Provision (Join-Path $testApps '.benchmark-complete') "the BC $bc test apps" { [AlRunner.Provisioning.ArtifactDownloader]::TestApps($bc, $testApps, $logAction) }
Invoke-Provision (Join-Path $bakDir '.benchmark-complete') "the BC $bc demo backup (about 1 GB)" { [AlRunner.Provisioning.ArtifactDownloader]::TestData($bc, $bakDir, 'w1', $logAction) }
if (-not (Test-Path -LiteralPath $bakPath)) { Stop-Benchmark 2 "The backup is missing at $bakPath after the download reported success." @("Delete $bakDir and run again.") }

# The bucket list.
$allBuckets = @($Reference.Keys)
if ($Buckets) {
    $selected = @($Buckets -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    $unknown = @($selected | Where-Object { $allBuckets -notcontains $_ })
    if ($unknown.Count) { Stop-Benchmark 2 "Unknown bucket(s): $($unknown -join ', ')." @("Known buckets: $($allBuckets -join ', ')") }
    $dupes = @($selected | Group-Object | Where-Object { $_.Count -gt 1 } | ForEach-Object { $_.Name })
    if ($dupes.Count) { Stop-Benchmark 2 "Bucket(s) named twice, which would count them twice: $($dupes -join ', ')." @('Name each bucket once.') }
    $runBuckets = $selected
} else { $runBuckets = $allBuckets }
$surfaceIsFull = ($runBuckets.Count -eq $allBuckets.Count)

$needSources = @($runBuckets) + @($SmokeBucket) | Select-Object -Unique
foreach ($b in $needSources) {
    $dest = Join-Path $Dirs.BucketSrc (ConvertTo-Slug $b)
    if (Test-Path -LiteralPath (Join-Path $dest '.benchmark-complete')) { continue }
    $staging = Join-Path $Dirs.Tmp 'bucket-staging'
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    Write-Log "Fetching bucket source $b ..."
    $rc = [AlRunner.Provisioning.ArtifactDownloader]::TestSources($bc, $staging, $b, $logAction)
    $unpacked = Join-Path $staging $b
    if ($rc -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $unpacked 'app.json'))) {
        Stop-Benchmark 2 "Could not fetch the source of bucket '$b' (exit $rc)." @('Run the same command again; if it repeats, report the message above.')
    }
    if (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest -Recurse -Force }
    Move-Item -LiteralPath $unpacked -Destination $dest
    Set-Content -LiteralPath (Join-Path $dest '.benchmark-complete') -Value (Get-Date).ToString('o')
}

if ($CheckOnly) {
    Write-Log 'Checks and downloads done (-CheckOnly): nothing was run.' 'Green'
    exit 0
}

# ─── Running one bucket ───────────────────────────────────────────────────────────────────
$CaveatRegexes = @(
    '^\s*=== .* (?:COMPILE|EXEC) FAIL ===',
    '^\s*\S.*?: (?:EMIT-TIMEOUT|EMIT-FAIL|EMIT-EXCLUDED|EMIT-ZERO|PARTIAL-EMIT-DROP|AL-DIAGNOSTIC-FAIL|COMPILE-FAIL|EXEC-FAIL|TEST-TIMEOUT-ABORT)\b',
    '^\s*NOT RUN:', '^\s*resume:', 'carried from earlier attempt'
)

function Get-ChildPids([int] $RootPid) {
    $all = @{}
    if ($script:OnWindows) {
        foreach ($p in (Get-CimInstance -ClassName Win32_Process -Property ProcessId, ParentProcessId)) {
            $all[[int]$p.ProcessId] = [int]$p.ParentProcessId
        }
    } else {
        foreach ($d in (Get-ChildItem /proc -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^\d+$' })) {
            try {
                $stat = [System.IO.File]::ReadAllText("/proc/$($d.Name)/stat")
                $after = $stat.Substring($stat.LastIndexOf(')') + 2) -split ' '
                $all[[int]$d.Name] = [int]$after[1]
            } catch { }
        }
    }
    $result = New-Object System.Collections.Generic.List[int]
    $result.Add($RootPid)
    $i = 0
    while ($i -lt $result.Count) {
        $parent = $result[$i]
        foreach ($k in $all.Keys) { if ($all[$k] -eq $parent -and -not $result.Contains($k)) { $result.Add($k) } }
        $i++
    }
    return $result
}

function Start-Bucket([string] $Bucket, [string] $Phase, [int] $JobsNow) {
    $slug = ConvertTo-Slug $Bucket
    $out = Join-Path $Dirs.Results "$Phase/$slug"
    if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    $testTimeout = [math]::Max($TestTimeoutFloorSec, 60 * $JobsNow)
    $runnerArgs = @(
        $Tools.Runner,
        (Join-Path $Dirs.BucketSrc $slug),
        '--bc-version', $bc,
        '--package-cache', $platformApps,
        '--package-cache', $testApps,
        '--cache', $Dirs.AlCache,
        '--test-timeout', [string]$testTimeout,
        '--output-junit', (Join-Path $out 'junit.xml'),
        '--out', (Join-Path $out 'results.json'),
        "--test-data=$bakPath",
        '--test-data-company', $Company,
        '--quiet'
    )
    $argString = ConvertTo-ArgString $runnerArgs
    Set-Content -LiteralPath (Join-Path $out 'command.txt') -Value ("`"$($Tools.Dotnet)`" $argString")
    $p = Start-Process -FilePath $Tools.Dotnet -ArgumentList $argString -WorkingDirectory $Dirs.RunCwd `
        -RedirectStandardOutput (Join-Path $out 'stdout.log') -RedirectStandardError (Join-Path $out 'stderr.log') `
        -NoNewWindow -PassThru
    # Touch the handle now: without it, Start-Process's Process object can report a null
    # ExitCode once the process has exited.
    $null = $p.Handle
    return [pscustomobject]@{
        Bucket = $Bucket; Slug = $slug; Out = $out; Process = $p; Started = Get-Date
        PeakTreeMB = 0.0; PeakProcessMB = 0.0; JobsAtLaunch = $JobsNow; TestTimeoutSec = $testTimeout
    }
}

function Update-Memory($Run) {
    $tree = 0.0
    foreach ($procId in (Get-ChildPids $Run.Process.Id)) {
        try {
            $pr = [System.Diagnostics.Process]::GetProcessById($procId)
            $tree += $pr.WorkingSet64
            $peak = [double]$pr.PeakWorkingSet64 / 1MB
            if ($peak -gt $Run.PeakProcessMB) { $Run.PeakProcessMB = [math]::Round($peak, 0) }
        } catch { }
    }
    $treeMB = $tree / 1MB
    if ($treeMB -gt $Run.PeakTreeMB) { $Run.PeakTreeMB = [math]::Round($treeMB, 0) }
}

# Reads what a finished bucket produced and decides whether it is a number.
function Complete-Bucket($Run) {
    $p = $Run.Process
    $p.WaitForExit()
    $rc = $p.ExitCode
    $wall = [math]::Round(((Get-Date) - $Run.Started).TotalSeconds, 1)
    $junitPath = Join-Path $Run.Out 'junit.xml'
    $stdout = @(); if (Test-Path -LiteralPath (Join-Path $Run.Out 'stdout.log')) { $stdout = Get-Content -LiteralPath (Join-Path $Run.Out 'stdout.log') }
    $stderr = @(); if (Test-Path -LiteralPath (Join-Path $Run.Out 'stderr.log')) { $stderr = Get-Content -LiteralPath (Join-Path $Run.Out 'stderr.log') }
    $lines = @($stdout) + @($stderr)
    $reasons = @($lines | Where-Object { $l = $_; @($CaveatRegexes | Where-Object { $l -match $_ }).Count -gt 0 } | Select-Object -Unique | Select-Object -First 20)
    $rec = [ordered]@{
        bucket = $Run.Bucket; status = 'no-number'; exitCode = $rc
        tests = $null; passed = $null; failed = $null; errors = $null; skipped = $null
        wallSeconds = $wall; startedUtc = $Run.Started.ToUniversalTime().ToString('o'); finishedUtc = (Get-Date).ToUniversalTime().ToString('o')
        peakProcessTreeWorkingSetMB = $Run.PeakTreeMB; peakSingleProcessWorkingSetMB = $Run.PeakProcessMB
        workersAtLaunch = $Run.JobsAtLaunch; testTimeoutSeconds = $Run.TestTimeoutSec
        testDataLoaded = [bool]($lines | Where-Object { $_ -match "from 'BusinessCentral-W1\.bak' company" } | Select-Object -First 1)
        referenceTests = $null; countDeviationPct = $null
        caveats = $reasons
    }
    if ($Reference.Contains($Run.Bucket)) { $rec.referenceTests = $Reference[$Run.Bucket].Tests }
    if (Test-Path -LiteralPath $junitPath) {
        try {
            [xml]$x = Get-Content -LiteralPath $junitPath -Raw
            $root = $x.testsuites
            $rec.tests = [int]$root.tests; $rec.failed = [int]$root.failures; $rec.errors = [int]$root.errors
            $rec.skipped = [int]$root.skipped
            $rec.passed = $rec.tests - $rec.failed - $rec.errors - $rec.skipped
        } catch { $rec.caveats += "junit.xml unreadable: $($_.Exception.Message)" }
    }
    # A number is: the runner said the tests ran (exit 0/1), or ran with lost suites (exit 3,
    # which the reasons above name), AND a JUnit file with at least one test. Exit 3 is the
    # runner's "a suite was lost" code — ms-surface.yml run 35704652590 read it as "no
    # number", but its JUnit held real counts; it is reported here as partial, with why.
    if ($null -ne $rec.tests -and $rec.tests -gt 0) {
        if ($rc -eq 0 -or $rc -eq 1) { $rec.status = 'measured' }
        elseif ($rc -eq 3) { $rec.status = 'partial' }
    }
    if ($null -ne $rec.tests -and $rec.referenceTests) {
        $rec.countDeviationPct = [math]::Round(100.0 * ($rec.tests - $rec.referenceTests) / $rec.referenceTests, 1)
    }
    $obj = [pscustomobject]$rec
    $obj | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Run.Out 'bucket.json') -Encoding UTF8
    return $obj
}

function Format-Duration([double] $Seconds) {
    $t = [TimeSpan]::FromSeconds($Seconds)
    return ('{0}h{1:00}m{2:00}s' -f [int][math]::Floor($t.TotalHours), $t.Minutes, $t.Seconds)
}

# ─── Smoke bucket ─────────────────────────────────────────────────────────────────────────
$smokeJson = Join-Path $Dirs.Results "smoke/$(ConvertTo-Slug $SmokeBucket)/bucket.json"
$smoke = $null
if (-not $Fresh -and (Test-Path -LiteralPath $smokeJson)) {
    $prev = Get-Content -LiteralPath $smokeJson -Raw | ConvertFrom-Json
    if ($prev.status -eq 'measured' -and $prev.tests -ge $SmokeMinTests) { $smoke = $prev; Write-Log "Smoke bucket already measured in an earlier session: $($prev.tests) tests, $($prev.passed) passed." }
}
if (-not $smoke) {
    Write-Log "Smoke run: $SmokeBucket alone, to prove the setup before spending hours (first run compiles a lot; expect 5-20 minutes)." 'Cyan'
    $run = Start-Bucket $SmokeBucket 'smoke' 1
    while (-not $run.Process.HasExited) { Update-Memory $run; Start-Sleep -Seconds 5 }
    $smoke = Complete-Bucket $run
    Write-Log ("Smoke: exit {0}, {1} tests, {2} passed, {3} failed, wall {4}, peak memory {5} MB" -f $smoke.exitCode, $smoke.tests, $smoke.passed, $smoke.failed, (Format-Duration $smoke.wallSeconds), $smoke.peakProcessTreeWorkingSetMB)
}
$smokeProblems = @()
if ($smoke.status -ne 'measured') { $smokeProblems += "the runner did not produce a clean number (exit $($smoke.exitCode), status $($smoke.status))" }
if ($null -eq $smoke.tests -or $smoke.tests -lt $SmokeMinTests -or $smoke.tests -gt $SmokeMaxTests) { $smokeProblems += "it reported $($smoke.tests) tests; expected $SmokeMinTests-$SmokeMaxTests" }
if ($null -eq $smoke.passed -or $smoke.passed -lt $SmokeMinPassed) { $smokeProblems += "only $($smoke.passed) passed; expected at least $SmokeMinPassed with the demo data loaded" }
if (-not $smoke.testDataLoaded) { $smokeProblems += 'the log never confirms the demo company was read from the backup' }
if ($smokeProblems.Count) {
    $todo = @("Read $($Dirs.Results)/smoke/$(ConvertTo-Slug $SmokeBucket)/stdout.log and stderr.log.")
    foreach ($c in @($smoke.caveats)) { $todo += "runner said: $c" }
    $todo += 'Send that folder to the person who asked you to run this; do not start the full run.'
    Stop-Benchmark 3 ("The smoke bucket is not sane, so a full run would produce a wrong number: " + ($smokeProblems -join '; ') + '.') $todo
}
Write-Log 'Smoke bucket is sane.' 'Green'

# ─── Worker count ─────────────────────────────────────────────────────────────────────────
$minutes = @{}; foreach ($b in $runBuckets) { $minutes[$b] = [double]$Reference[$b].Minutes }
$sumMin = ($runBuckets | ForEach-Object { $minutes[$_] } | Measure-Object -Sum).Sum
$maxMin = ($runBuckets | ForEach-Object { $minutes[$_] } | Measure-Object -Maximum).Maximum
# Per worker: the smoke bucket's measured peak, scaled for the largest buckets. Tests-ERM
# and Tests-SCM load far more objects than Tests-SMB; 2.5x covers what ms-bucket.yml has
# recorded for Tests-ERM relative to Tests-SMB, with a 4 GB floor.
$smokePeakGB = [double]$smoke.peakProcessTreeWorkingSetMB / 1024
$perWorkerGB = [math]::Round([math]::Max(4.0, 2.5 * $smokePeakGB), 1)
$freeMem = Get-FreeMemoryGB
$reserveGB = [math]::Round([math]::Max(4.0, 0.10 * $machine.totalMemoryGB), 1)
$byMemory = [int][math]::Floor(($freeMem - $reserveGB) / $perWorkerGB)
$cores = if ($machine.Contains('physicalCores') -and $machine.physicalCores) { [int]$machine.physicalCores } else { [int][math]::Max(1, $machine.logicalProcessors / 2) }
$byFloor = [int][math]::Ceiling($sumMin / $maxMin) + 1
$caps = [ordered]@{
    'memory'         = $byMemory
    'physical cores' = $cores
    'bucket count'   = $runBuckets.Count
    'largest bucket' = $byFloor
}
$derived = [int]($caps.Values | Measure-Object -Minimum).Minimum
$binding = ($caps.Keys | Where-Object { $caps[$_] -eq $derived } | Select-Object -First 1)
Write-Log ("Worker sizing: {0} GB available, {1} GB kept free, {2} GB per worker (smoke peak {3:N1} GB x 2.5, at least 4)" -f $freeMem, $reserveGB, $perWorkerGB, $smokePeakGB)
Write-Log ("  caps: memory {0}, physical cores {1}, bucket count {2}, largest bucket {3} (no more than ceil({4:N0} / {5:N0} minutes) + 1 workers can shorten a run whose longest bucket cannot be split)" -f $byMemory, $cores, $runBuckets.Count, $byFloor, $sumMin, $maxMin)
if ($Jobs -gt 0) {
    $workers = $Jobs
    Write-Log "Workers: $workers (set with -Jobs; derived value would have been $derived, bound by $binding)" 'Yellow'
    if ($Jobs -gt $byMemory) { Write-Log "  -Jobs is above what memory allows ($byMemory). New buckets will still wait until enough memory is free." 'Yellow' }
} else {
    $workers = $derived
    Write-Log "Workers: $workers (bound by $binding)" 'Cyan'
}
if ($workers -lt 1) {
    Stop-Benchmark 2 "Not enough free memory for even one worker: $freeMem GB available, $reserveGB GB kept free, $perWorkerGB GB per worker." @('Close other programs and run again.')
}

# ─── The surface ──────────────────────────────────────────────────────────────────────────
$phase = 'surface'
$configKey = "$($Pins.RunnerVersion)|$bc|$($Pins.ReaderTag)|$Company"
$queue = New-Object System.Collections.Generic.List[string]
$done = [ordered]@{}
foreach ($b in ($runBuckets | Sort-Object { -$minutes[$_] })) {
    $bj = Join-Path $Dirs.Results "$phase/$(ConvertTo-Slug $b)/bucket.json"
    $keyFile = Join-Path $Dirs.Results "$phase/$(ConvertTo-Slug $b)/config.txt"
    if (-not $Fresh -and (Test-Path -LiteralPath $bj) -and (Test-Path -LiteralPath $keyFile) -and ((Get-Content -LiteralPath $keyFile -Raw).Trim() -eq $configKey)) {
        $prev = Get-Content -LiteralPath $bj -Raw | ConvertFrom-Json
        if ($prev.status -ne 'no-number') { $done[$b] = $prev; continue }
    }
    $queue.Add($b)
}
$carried = $done.Count
if ($done.Count) { Write-Log "Resuming: $($done.Count) bucket(s) already measured in an earlier session are kept; $($queue.Count) to run." 'Yellow' }

$sessionStart = Get-Date
$running = New-Object System.Collections.Generic.List[object]
$lastBeat = Get-Date
$minAvailGB = [double]::MaxValue
try {
    while ($queue.Count -gt 0 -or $running.Count -gt 0) {
        # Launch while there is a free slot AND the memory to back it. The second check is
        # what keeps a wrong per-worker estimate from overcommitting the machine.
        while ($queue.Count -gt 0 -and $running.Count -lt $workers) {
            $avail = Get-FreeMemoryGB
            if ($running.Count -gt 0 -and $avail - $perWorkerGB -lt $reserveGB) { break }
            $b = $queue[0]; $queue.RemoveAt(0)
            $r = Start-Bucket $b $phase $workers
            Set-Content -LiteralPath (Join-Path $r.Out 'config.txt') -Value $configKey
            $running.Add($r)
            Write-Log ("start  {0}  ({1} running, {2} queued, {3} GB available)" -f $b, $running.Count, $queue.Count, $avail)
        }
        Start-Sleep -Seconds 10
        $avail = Get-FreeMemoryGB
        if ($avail -lt $minAvailGB) { $minAvailGB = $avail }
        foreach ($r in @($running)) {
            if ($r.Process.HasExited) {
                $rec = Complete-Bucket $r
                $running.Remove($r) | Out-Null
                $done[$r.Bucket] = $rec
                $color = 'Green'; if ($rec.status -eq 'partial') { $color = 'Yellow' } elseif ($rec.status -ne 'measured') { $color = 'Red' }
                Write-Log ("done   {0}: {1}, exit {2}, {3} tests, {4} passed, {5} failed, {6}, peak {7} MB  [{8}/{9}]" -f $r.Bucket, $rec.status, $rec.exitCode, $rec.tests, $rec.passed, $rec.failed, (Format-Duration $rec.wallSeconds), $rec.peakProcessTreeWorkingSetMB, $done.Count, $runBuckets.Count) $color
                foreach ($c in @($rec.caveats | Select-Object -First 3)) { Write-Log "         $c" $color }
            } else { Update-Memory $r }
        }
        if (((Get-Date) - $lastBeat).TotalSeconds -ge 300 -and $running.Count) {
            $lastBeat = Get-Date
            $desc = ($running | ForEach-Object { '{0} {1}' -f $_.Bucket, (Format-Duration ((Get-Date) - $_.Started).TotalSeconds) }) -join ', '
            Write-Log "still running: $desc; $($done.Count)/$($runBuckets.Count) done; $avail GB available; elapsed $(Format-Duration ((Get-Date) - $sessionStart).TotalSeconds)"
        }
    }
} finally {
    foreach ($r in @($running)) {
        if (-not $r.Process.HasExited) {
            Write-Log "Stopping $($r.Bucket) (interrupted); it will run again next time." 'Yellow'
            try { $r.Process.Kill($true) } catch { }
        }
    }
}
$sessionWall = ((Get-Date) - $sessionStart).TotalSeconds

# ─── Summary and result zip ───────────────────────────────────────────────────────────────
$records = @($runBuckets | ForEach-Object { $done[$_] })
function Get-Total([string] $Field) {
    $t = 0
    foreach ($r in $records) { if ($null -ne $r.$Field) { $t += [int]$r.$Field } }
    return $t
}
$missing = @($records | Where-Object { $_.status -eq 'no-number' } | ForEach-Object { $_.bucket })
$partial = @($records | Where-Object { $_.status -eq 'partial' } | ForEach-Object { $_.bucket })
$deviating = @($records | Where-Object { $null -ne $_.countDeviationPct -and [math]::Abs($_.countDeviationPct) -gt $CountDeviationFlagPct } | ForEach-Object { "$($_.bucket) ($($_.countDeviationPct)%)" })
$resumed = ($carried -gt 0)
$verdict = 'COMPLETE'
if ($missing.Count) { $verdict = 'INCOMPLETE' }

$summary = [ordered]@{
    verdict = $verdict
    fullSurface = $surfaceIsFull
    buckets = $runBuckets.Count
    tests = Get-Total 'tests'; passed = Get-Total 'passed'; failed = Get-Total 'failed'; errors = Get-Total 'errors'; skipped = Get-Total 'skipped'
    referenceTests = ($runBuckets | ForEach-Object { $Reference[$_].Tests } | Measure-Object -Sum).Sum
    noNumber = $missing; partial = $partial; countDeviationOver10Pct = $deviating
    wallSecondsThisSession = [math]::Round($sessionWall, 0)
    wallIsWholeRun = (-not $resumed)
    workers = $workers; workersBoundBy = $(if ($Jobs -gt 0) { '-Jobs' } else { $binding }); perWorkerGB = $perWorkerGB; reservedGB = $reserveGB
    minAvailableMemoryGBDuringRun = $(if ($minAvailGB -eq [double]::MaxValue) { $null } else { $minAvailGB })
    smoke = $smoke
    versions = [ordered]@{ runner = $Pins.RunnerVersion; bc = $bc; reader = $readerVersion; dotnet = $Pins.DotnetVersion; pwsh = $PSVersionTable.PSVersion.ToString(); scriptSha256 = $ScriptSha }
    environment = [ordered]@{ DOTNET_GCHeapCount = $env:DOTNET_GCHeapCount; DOTNET_GCConserveMemory = $env:DOTNET_GCConserveMemory; DOTNET_gcConcurrent = $env:DOTNET_gcConcurrent; AL_RUNNER_EMIT_TIMEOUT_SEC = $env:AL_RUNNER_EMIT_TIMEOUT_SEC }
    machine = $machine
    perBucket = $records
}
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Dirs.Results 'summary.json') -Encoding UTF8

$md = New-Object System.Collections.Generic.List[string]
$md.Add("# AL Runner Microsoft-surface benchmark: $verdict")
$md.Add('')
$md.Add("Runner $($Pins.RunnerVersion), BC $bc, reader $($Pins.ReaderTag), $workers worker(s), $($machine.cpu), $($machine.logicalProcessors) logical processors, $($machine.totalMemoryGB) GB")
$md.Add('')
$md.Add('| bucket | status | tests | passed | failed | errors | skipped | wall | peak MB | ref tests |')
$md.Add('|---|---|---:|---:|---:|---:|---:|---:|---:|---:|')
foreach ($r in $records) {
    $md.Add(('| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} | {9} |' -f $r.bucket, $r.status, $r.tests, $r.passed, $r.failed, $r.errors, $r.skipped, (Format-Duration $r.wallSeconds), $r.peakProcessTreeWorkingSetMB, $r.referenceTests))
}
$md.Add(('| **total** | | **{0}** | **{1}** | **{2}** | **{3}** | **{4}** | **{5}** | | {6} |' -f $summary.tests, $summary.passed, $summary.failed, $summary.errors, $summary.skipped, (Format-Duration $sessionWall), $summary.referenceTests))
$md.Add('')
if ($resumed) { $md.Add('This run was resumed: the wall time above covers the last session only, not the whole surface.') }
if ($missing.Count) { $md.Add("No number from: $($missing -join ', ')") }
if ($partial.Count) { $md.Add("Partial (a suite was lost; see caveats in summary.json): $($partial -join ', ')") }
if ($deviating.Count) { $md.Add("Test count more than $CountDeviationFlagPct% away from the reference: $($deviating -join ', ')") }
$md | Set-Content -LiteralPath (Join-Path $Dirs.Results 'summary.md') -Encoding UTF8

# The zip: results only, not the multi-GB caches. Logs contain the working-folder path,
# which includes the Windows user name; summary.json does not contain the machine name.
$zipPath = Join-Path $script:Work "al-runner-benchmark-$SessionStamp.zip"
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -Path (Join-Path $Dirs.Results '*') -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host ''
Get-Content -LiteralPath (Join-Path $Dirs.Results 'summary.md') | ForEach-Object { Write-Host $_ }
Write-Host ''
if ($verdict -eq 'COMPLETE') {
    Write-Log "Done. Please send this file back: $zipPath" 'Green'
    exit 0
}
Write-Log "Finished, but $($missing.Count) bucket(s) produced no number: $($missing -join ', '). Please send this file anyway: $zipPath" 'Yellow'
Write-Log 'Running the same command again re-runs only the missing buckets.' 'Yellow'
exit 1
