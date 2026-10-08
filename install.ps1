<#
.SYNOPSIS
  Agent Orchestrator installer for Windows. Run from a clone of the repository:

    .\install.ps1                                   build, install, then run the guided `orch setup`
    .\install.ps1 -NoSetup                          install only
    .\install.ps1 -Start                            start the installed server (no rebuild) and open the dashboard
    .\install.ps1 -SetupArgs '--agents','claude,qoder','--yes'   non-interactive setup
    .\install.ps1 -Uninstall [-Purge]               disconnect agents, remove autostart and the app
                                                    (-Purge: also delete task data and settings)

  Re-run it after `git pull` to upgrade; your settings and tasks are kept.
  If scripts are blocked: powershell -ExecutionPolicy Bypass -File .\install.ps1
#>
param(
  [switch]$NoSetup,
  [switch]$Start,
  [switch]$Uninstall,
  [switch]$Purge,
  [string[]]$SetupArgs = @()
)
$ErrorActionPreference = 'Stop'

$repo = $PSScriptRoot
$root = if ($env:ORCHESTRATOR_HOME) { $env:ORCHESTRATOR_HOME } else { Join-Path $env:LOCALAPPDATA 'agent-orchestrator' }
$app = Join-Path $root 'app'
$orch = Join-Path $app 'orch.exe'

function Say($text) { Write-Host $text -ForegroundColor Cyan }

function Stop-OldServer {
  if (Test-Path $orch) { & $orch stop *> $null }
  # Files of a running server are locked; make sure none is left.
  Get-Process -Name 'Orchestrator.Api' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($app, [StringComparison]::OrdinalIgnoreCase) } |
    Stop-Process -Force -ErrorAction SilentlyContinue
  Start-Sleep -Milliseconds 500
}

function Set-UserPath([switch]$Remove) {
  $current = [Environment]::GetEnvironmentVariable('Path', 'User')
  $parts = @($current -split ';' | Where-Object { $_ -and ($_.TrimEnd('\') -ine $app.TrimEnd('\')) })
  if (-not $Remove) { $parts += $app }
  [Environment]::SetEnvironmentVariable('Path', ($parts -join ';'), 'User')
}

if ($Uninstall) {
  if (Test-Path $orch) { & $orch uninstall }
  Stop-OldServer
  if (Test-Path $app) { Remove-Item -Recurse -Force $app }
  Set-UserPath -Remove
  if ($Purge) { if (Test-Path $root) { Remove-Item -Recurse -Force $root }; Say "Removed $root (tasks, logs, settings)." }
  else { Say "App removed. Tasks and settings kept in $root (use -Purge to delete)." }
  exit 0
}

if ($Start) {
  if (-not (Test-Path $orch)) { throw 'Agent Orchestrator is not installed yet. Run .\install.ps1 first.' }
  & $orch start
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
  & $orch open
  exit 0
}

# ---- prerequisites
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'git is required: https://git-scm.com/download/win' }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET 10 SDK is required: https://dotnet.microsoft.com/download/dotnet/10.0' }
if (-not (dotnet --list-sdks | Select-String '^10\.')) { throw ".NET 10 SDK is required (found: $((dotnet --list-sdks) -join ', ')). https://dotnet.microsoft.com/download/dotnet/10.0" }

$rid = if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'win-arm64' } else { 'win-x64' }

# ---- build (self-contained, so the login task needs no .NET install path; the server has no console window)
$stage = Join-Path ([IO.Path]::GetTempPath()) ("orch-install-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$env:DOTNET_NOLOGO = '1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Say "Building Agent Orchestrator ($rid)..."
try {
  dotnet publish "$repo\src\Orchestrator.Api" -c Release -r $rid --self-contained -o "$stage\app" -p:OrchestratorWindowless=true -v quiet -nologo
  if ($LASTEXITCODE -ne 0) { throw 'Building the server failed.' }
  dotnet publish "$repo\src\Orchestrator.Cli" -c Release -r $rid --self-contained -o "$stage\app" -v quiet -nologo
  if ($LASTEXITCODE -ne 0) { throw 'Building orch failed.' }

  # ---- stop the old version, then swap in the new one
  Stop-OldServer
  New-Item -ItemType Directory -Force $root | Out-Null
  if (Test-Path $app) { Remove-Item -Recurse -Force $app }
  Move-Item "$stage\app" $app
} finally {
  if (Test-Path $stage) { Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue }
}
Say "Installed to $app"

# ---- put orch on the user PATH (new terminals) and on this session's PATH
Set-UserPath
if (-not (($env:Path -split ';') -contains $app)) { $env:Path = "$app;$env:Path" }

if ($NoSetup) { & $orch start } else { & $orch setup @SetupArgs }
Write-Host ''
Say 'Manage platforms any time: orch setup, or open the Setup page shown above. Open a new terminal to use `orch`.'
