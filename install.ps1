# PhoneBox Games Tab - installer / uninstaller (Windows PowerShell 5.1 or PowerShell 7).
# Run it as the Windows user who uses PhoneBox, with the PhoneBox agent running. No administrator rights needed.
#
#   .\install.ps1               build the collector (if needed), add the Games page + rule, start the collector at logon
#   .\install.ps1 -Port 9000    use this agent port instead of detecting it
#   .\install.ps1 -NoRule       do not add the "game running -> Games tab" rule
#   .\install.ps1 -Rebuild      rebuild the collector from source first
#   .\install.ps1 -Uninstall    stop the collector, remove the scheduled task, the page and the rule
param([int]$Port = 0, [switch]$NoRule, [switch]$Rebuild, [switch]$Uninstall)
$ErrorActionPreference = 'Stop'
$taskName = 'PhoneBox Games Tab'
$exe = Join-Path $PSScriptRoot 'bin\PhoneBoxGamesTab.exe'
$dataDir = Join-Path $env:LOCALAPPDATA 'PhoneBoxGamesTab'

function Stop-Collector {
    $sid = (Get-Process -Id $PID).SessionId
    Get-Process PhoneBoxGamesTab -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $sid } | Stop-Process -Force
}

# ---- agent port: -Port, else the --port of this user's PhoneBoxAgent.exe, else the default 8765
if ($Port -eq 0) {
    $Port = 8765
    $me = $env:USERNAME
    foreach ($p in Get-CimInstance Win32_Process -Filter "Name='PhoneBoxAgent.exe'" -ErrorAction SilentlyContinue) {
        $owner = (Invoke-CimMethod -InputObject $p -MethodName GetOwner -ErrorAction SilentlyContinue).User
        if ($owner -eq $me -and $p.CommandLine -match '--port[ =](\d+)') { $Port = [int]$Matches[1] }
    }
}
$base = "http://127.0.0.1:$Port"
"PhoneBox agent: $base"

function Get-Settings {
    try { $r = Invoke-WebRequest "$base/api/settings" -UseBasicParsing -TimeoutSec 5 }
    catch { throw "The PhoneBox agent did not answer at $base. Start PhoneBox (or pass -Port <port>) and try again." }
    $json = [Text.Encoding]::UTF8.GetString($r.RawContentStream.ToArray())
    New-Item -ItemType Directory -Force $dataDir | Out-Null
    $bk = Join-Path $dataDir "settings-backup-$(Get-Date -Format yyyyMMdd-HHmmss).json"
    [IO.File]::WriteAllText($bk, $json, (New-Object Text.UTF8Encoding $false))
    "Settings backup: $bk"
    return @{ Config = ($json | ConvertFrom-Json); Version = $r.Headers['X-Config-Version'] }
}
function Save-Settings($s) {
    $body = [Text.Encoding]::UTF8.GetBytes(($s.Config | ConvertTo-Json -Depth 64))
    $h = @{}; if ($s.Version) { $h['X-Config-Version'] = "$($s.Version)" }
    Invoke-WebRequest "$base/api/settings" -Method Put -Body $body -ContentType 'application/json; charset=utf-8' -Headers $h -UseBasicParsing | Out-Null
}

# ---------------------------------------------------------------------------------------------- uninstall
if ($Uninstall) {
    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) { Stop-ScheduledTask -TaskName $taskName; Unregister-ScheduledTask -TaskName $taskName -Confirm:$false }
    Stop-Collector
    $s = Get-Settings
    $s.Config.pages = @($s.Config.pages | Where-Object { $_.id -ne 'games' })
    $s.Config.rules = @($s.Config.rules | Where-Object { $_.id -ne 'games-tab-auto' })
    Save-Settings $s
    foreach ($k in 'game', 'playing') { try { Invoke-RestMethod "$base/api/push/$k" -Method Delete | Out-Null } catch { } }
    "Removed: scheduled task, collector, Games page and rule. Play history stays in $dataDir (delete it if you want)."
    return
}

# ---------------------------------------------------------------------------------------------- install
# 1) collector
if ($Rebuild -or -not (Test-Path $exe)) {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'bin\PhoneBoxGamesTab.exe is missing and the .NET SDK is not installed. Download a release zip, or install the .NET 10 SDK.' }
    Stop-Collector
    & dotnet publish (Join-Path $PSScriptRoot 'collector') -c Release -o (Join-Path $PSScriptRoot 'bin') --nologo
    if ($LASTEXITCODE) { throw 'Build failed (needs the .NET 10 SDK).' }
}

# 2) Games page (after the last page) + rule
$s = Get-Settings
$html = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'widget\games-widget.html'), [Text.Encoding]::UTF8)
$page = [pscustomobject][ordered]@{
    id = 'games'; title = 'Games'; icon = [char]::ConvertFromUtf32(0x1F3AE); enabled = $true
    idleSec = 0   # stay on the Games tab while playing instead of going back home
    widgets = @([pscustomobject][ordered]@{ id = 'games'; type = 'html'; x = 0; y = 0; w = 12; h = 6; options = [ordered]@{ title = 'Games'; html = $html } })
}
$pages = @($s.Config.pages | Where-Object { $_.id -ne 'games' })
if (@($s.Config.pages | Where-Object { $_.id -eq 'games' }).Count) {   # keep its place if it was already there
    $pages = @($s.Config.pages | ForEach-Object { if ($_.id -eq 'games') { $page } else { $_ } })
} else { $pages += $page }
$s.Config.pages = $pages
$rules = @($s.Config.rules | Where-Object { $_.id -ne 'games-tab-auto' })
if (-not $NoRule) {
    $rules += [pscustomobject][ordered]@{
        id = 'games-tab-auto'; name = 'Game running -> Games tab'; enabled = $true
        when = 'push'; match = 'playing'; then = 'goto'; page = 'games'; whileActive = $true; priority = 6
    }
}
$s.Config.rules = $rules
Save-Settings $s
"Games page added" + $(if ($NoRule) { '.' } else { ', with the rule that opens it while you play.' })

# 3) FPS needs the "Performance Log Users" group (SID S-1-5-32-559)
if (-not ((whoami /groups) -match 'S-1-5-32-559')) {
    Write-Warning ("This Windows user is not in the 'Performance Log Users' group, so FPS will be unavailable " +
        "(play time and hardware still work). An administrator can add it with:`n" +
        "    Add-LocalGroupMember -SID S-1-5-32-559 -Member $env:USERNAME`n" +
        "then sign out and back in.")
}

# 4) scheduled task: start the collector at logon, restart it if it crashes
$user = "$env:USERDOMAIN\$env:USERNAME"
$action = New-ScheduledTaskAction -Execute $exe -Argument "--port $Port" -WorkingDirectory (Split-Path $exe)
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -MultipleInstances IgnoreNew `
    -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 5 -RestartInterval (New-TimeSpan -Minutes 1)
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
Register-ScheduledTask -TaskName $taskName -Description "PhoneBox Games Tab collector: FPS and play time for the PhoneBox Games page ($exe)." `
    -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null
Stop-Collector
Start-ScheduledTask -TaskName $taskName
"Collector started (scheduled task '$taskName', starts at logon). Log: $dataDir\collector.log"
"Open a game to try it."
