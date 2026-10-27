#Requires -Version 5.1
<#
.SYNOPSIS
  OFNR 운영 첫 설치 준비. 필요한 프로그램을 점검하고 빠진 것을 설치합니다.
.DESCRIPTION
  여러 번 실행해도 안전합니다. 이미 있는 것은 건너뜁니다.
  관리자 권한은 PostgreSQL을 새로 설치할 때만 필요합니다(Windows가 묻습니다).
  실행: powershell -ExecutionPolicy Bypass -File scripts\setup.ps1
#>
param(
  [string]$DataRoot = $(if ($env:OFNR_DATA) { $env:OFNR_DATA } else { 'C:\OFNR' })
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$pgBin = 'C:\Program Files\PostgreSQL\17\bin'
$tools = Join-Path $DataRoot 'tools'
$utf8 = New-Object System.Text.UTF8Encoding $false
$todo = New-Object System.Collections.Generic.List[string]

function Say($text) { Write-Host $text }
function Ok($text) { Write-Host "  [확인] $text" -ForegroundColor Green }
function Warn($text) { Write-Host "  [주의] $text" -ForegroundColor Yellow; $todo.Add($text) }

function Has($name) { [bool](Get-Command $name -ErrorAction SilentlyContinue) }

# Claude Code's installer puts claude.exe in ~\.local\bin, which may not be on PATH yet.
function Update-Path {
  $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User') + ';' + (Join-Path $env:USERPROFILE '.local\bin')
}

function Install-Winget($id, $extra) {
  if (-not (Has 'winget')) { Warn "winget이 없어 $id 를 설치하지 못했습니다. Microsoft Store에서 '앱 설치 관리자'를 설치한 뒤 다시 실행해 주세요."; return $false }
  $wingetArgs = @('install', '-e', '--id', $id, '--accept-source-agreements', '--accept-package-agreements', '--silent') + $extra
  & winget @wingetArgs | Out-Host
  Update-Path
  return $LASTEXITCODE -eq 0
}

# Checks a command; installs it with winget, or with the vendor's install script in a child PowerShell, when missing.
function Ensure($label, $command, $wingetId, $installScriptUrl) {
  # Windows PowerShell turns a native command's redirected stderr into a terminating error under 'Stop'.
  $ErrorActionPreference = 'Continue'
  if (-not (Has $command)) {
    Say "  $label 설치 중..."
    if ($installScriptUrl) {
      & powershell -NoProfile -ExecutionPolicy Bypass -Command "Invoke-RestMethod $installScriptUrl | Invoke-Expression" | Out-Host
      Update-Path
    } elseif ($wingetId) { [void](Install-Winget $wingetId @()) }
  }
  if (Has $command) {
    $version = (& $command --version 2>$null | Select-Object -First 1)
    Ok "$label $version"
  } else {
    Warn "$label 이(가) 없습니다. 설치한 뒤 이 스크립트를 다시 실행해 주세요."
  }
}

function New-Password {
  $bytes = New-Object byte[] 24
  [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
  -join ($bytes | ForEach-Object { $_.ToString('x2') })
}

# Same format as the server's SecretStore: DPAPI (current user) over UTF-8 bytes in <data>\secrets\<name>.bin.
try { Add-Type -AssemblyName System.Security } catch { }
function Save-Secret($name, $value) {
  $dir = Join-Path $DataRoot 'secrets'
  New-Item -ItemType Directory -Force $dir | Out-Null
  $bytes = [Security.Cryptography.ProtectedData]::Protect($utf8.GetBytes($value), $null, 'CurrentUser')
  [IO.File]::WriteAllBytes((Join-Path $dir "$name.bin"), $bytes)
}
function Read-Secret($name) {
  $path = Join-Path (Join-Path $DataRoot 'secrets') "$name.bin"
  if (-not (Test-Path $path)) { return $null }
  $utf8.GetString([Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($path), $null, 'CurrentUser'))
}

function Psql($user, $password, $database, $sql) {
  $ErrorActionPreference = 'Continue'
  $env:PGPASSWORD = $password
  $env:PGCLIENTENCODING = 'UTF8'
  try { & (Join-Path $pgBin 'psql.exe') -h 127.0.0.1 -p 5432 -U $user -d $database -v ON_ERROR_STOP=1 -tAc $sql 2>$null }
  finally { Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue }
}

function Write-Json($path, $value) {
  [IO.File]::WriteAllText($path, ($value | ConvertTo-Json -Depth 10), $utf8)
}

Say "OFNR 운영 설치 준비를 시작합니다. 데이터 폴더: $DataRoot"

# 1. Folders
foreach ($dir in @($DataRoot, (Join-Path $DataRoot 'tmp'), $tools)) { New-Item -ItemType Directory -Force $dir | Out-Null }
Ok "데이터 폴더 $DataRoot"

# 2. Programs
Say ''
Say '[1/4] 프로그램 점검'
Ensure 'Git' 'git' 'Git.Git' $null
Ensure 'Node.js' 'node' 'OpenJS.NodeJS.LTS' $null
# 'py', not 'python': on a PC without Python, python.exe is the Microsoft Store shortcut.
Ensure 'Python' 'py' 'Python.Python.3.12' $null
Ensure 'uv' 'uvx' 'astral-sh.uv' $null
# The Excel and PowerPoint MCP servers are .NET tools, which need the SDK, not only a runtime.
if (-not ((Has 'dotnet') -and (& dotnet --list-sdks | Where-Object { $_ -match '^10\.' }))) { [void](Install-Winget 'Microsoft.DotNet.SDK.10' @()) }
Ensure '.NET SDK' 'dotnet' $null $null
Ensure 'Claude Code' 'claude' $null 'https://claude.ai/install.ps1'
Ensure 'Codex' 'codex' $null 'https://chatgpt.com/codex/install.ps1'

# 3. PostgreSQL 17 (not 18: the phase 2 shared server runs 17, and pg_dump cannot dump a newer server)
Say ''
Say '[2/4] 데이터베이스'
if (-not (Test-Path (Join-Path $pgBin 'psql.exe'))) {
  $super = New-Password
  Save-Secret 'postgres' $super
  Say '  PostgreSQL 17 설치 중... (관리자 권한 확인 창이 뜨면 예를 눌러 주세요)'
  [void](Install-Winget 'PostgreSQL.PostgreSQL.17' @('--override', "--mode unattended --unattendedmodeui none --superpassword $super --serverport 5432 --disable-components pgAdmin,stackbuilder"))
}

# U&'' keeps the Korean probe word out of the console code page.
$probe = "SELECT cardinality(show_trgm(U&'\ACAC\C801\C11C')) > 0"
$dbPassword = Read-Secret 'db'
if (-not (Test-Path (Join-Path $pgBin 'psql.exe'))) {
  Warn 'PostgreSQL 17이 설치되지 않았습니다. 다시 실행하거나 https://www.postgresql.org/download/windows/ 에서 17 버전을 설치해 주세요.'
} elseif ($dbPassword -and (Psql 'ofnr' $dbPassword 'ofnr_ops' $probe) -eq 't') {
  Ok 'ofnr_ops 데이터베이스 (한글 검색 확인)'
} else {
  $super = Read-Secret 'postgres'
  if (-not $super) {
    $secure = Read-Host 'PostgreSQL 관리자(postgres) 비밀번호를 입력해 주세요' -AsSecureString
    $super = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
  }
  if (-not $dbPassword) { $dbPassword = New-Password; Save-Secret 'db' $dbPassword }
  $roleExists = (Psql 'postgres' $super 'postgres' "SELECT 1 FROM pg_roles WHERE rolname = 'ofnr'") -eq '1'
  if ($LASTEXITCODE -ne 0) {
    Warn 'PostgreSQL에 관리자로 접속하지 못했습니다. 서비스가 켜져 있는지, 비밀번호가 맞는지 확인해 주세요.'
  } else {
    $verb = if ($roleExists) { 'ALTER' } else { 'CREATE' }
    [void](Psql 'postgres' $super 'postgres' "$verb ROLE ofnr WITH LOGIN PASSWORD '$dbPassword'")
    if ((Psql 'postgres' $super 'postgres' "SELECT 1 FROM pg_database WHERE datname = 'ofnr_ops'") -ne '1') {
      [void](Psql 'postgres' $super 'postgres' "CREATE DATABASE ofnr_ops OWNER ofnr ENCODING 'UTF8' LOCALE_PROVIDER icu ICU_LOCALE 'ko-KR' TEMPLATE template0")
    }
    [void](Psql 'postgres' $super 'ofnr_ops' 'CREATE EXTENSION IF NOT EXISTS pg_trgm')
    if ((Psql 'ofnr' $dbPassword 'ofnr_ops' $probe) -eq 't') { Ok 'ofnr_ops 데이터베이스와 ofnr 계정 (한글 검색 확인)' }
    else { Warn '데이터베이스는 만들었지만 한글 검색(pg_trgm) 확인에 실패했습니다.' }
  }
}

# Server settings: the DB password stays in secrets\db.bin, never in settings.json.
$settingsPath = Join-Path $DataRoot 'settings.json'
$settings = if (Test-Path $settingsPath) { Get-Content $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json } else { New-Object PSObject }
$values = @{ connectionString = 'Host=127.0.0.1;Port=5432;Database=ofnr_ops;Username=ofnr' }
if (Test-Path (Join-Path $pgBin 'pg_dump.exe')) { $values.pgDumpPath = Join-Path $pgBin 'pg_dump.exe' }
if (Has 'claude') { $values.claudeExe = (Get-Command claude).Source }
if (Has 'codex') { $values.codexExe = (Get-Command codex).Source }
foreach ($key in $values.Keys) { $settings | Add-Member -NotePropertyName $key -NotePropertyValue $values[$key] -Force }
Write-Json $settingsPath $settings
Ok "설정 파일 $settingsPath"

# 4. MCP servers (pinned versions, absolute paths, no npx)
Say ''
Say '[3/4] 외부 도구(MCP) 설치'
if (Has 'npm') {
  & npm install --prefix $tools --no-audit --no-fund illustrator-mcp-server@1.10.3 '@alisaitteke/photoshop-mcp@1.7.37' | Out-Host
}
$dotnetTools = @{ 'sbroenne.excelmcp.mcpserver' = '2.5.6'; 'sbroenne.powerpointmcp.mcpserver' = '0.3.4' }
if (Has 'dotnet') {
  foreach ($id in $dotnetTools.Keys) {
    # install fails when the tool is already there; update then moves it to the pinned version.
    & dotnet tool install --tool-path $tools $id --version $dotnetTools[$id] | Out-Host
    if ($LASTEXITCODE -ne 0) { & dotnet tool update --tool-path $tools $id --version $dotnetTools[$id] | Out-Host }
  }
}

$mcpPath = Join-Path $DataRoot 'mcp.json'
if (Test-Path $mcpPath) {
  Ok "$mcpPath 이(가) 이미 있어 그대로 둡니다."
} else {
  $mcp = Get-Content (Join-Path $repo 'config\mcp.example.json') -Raw -Encoding UTF8 | ConvertFrom-Json
  # Command names of the installed .NET tools, e.g. "sbroenne.excelmcp.mcpserver  2.5.6  mcp-excel".
  $commands = @{}
  if (Has 'dotnet') {
    & dotnet tool list --tool-path $tools | Select-Object -Skip 2 | ForEach-Object {
      $cols = -split $_
      if ($cols.Count -ge 3) { $commands[$cols[0].ToLower()] = Join-Path $tools ($cols[2] + '.exe') }
    }
  }
  foreach ($server in $mcp.mcpServers.PSObject.Properties) {
    $s = $server.Value
    $s.command = $s.command.Replace('C:\OFNR', $DataRoot)
    $s.args = @($s.args | ForEach-Object { $_.Replace('C:\OFNR', $DataRoot) })
    foreach ($e in $s.env.PSObject.Properties) { $e.Value = $e.Value.Replace('C:\OFNR', $DataRoot) }
    if ($s.command -like '*\node.exe' -and (Has 'node')) { $s.command = (Get-Command node).Source }
    if ($s.command -like '*\uvx.exe' -and (Has 'uvx')) { $s.command = (Get-Command uvx).Source }
  }
  if ($commands['sbroenne.excelmcp.mcpserver']) { $mcp.mcpServers.excel.command = $commands['sbroenne.excelmcp.mcpserver'] }
  if ($commands['sbroenne.powerpointmcp.mcpserver']) { $mcp.mcpServers.powerpoint.command = $commands['sbroenne.powerpointmcp.mcpserver'] }
  Write-Json $mcpPath $mcp
  Ok "$mcpPath 작성"
}

$mcp = Get-Content $mcpPath -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($server in $mcp.mcpServers.PSObject.Properties) {
  $paths = @($server.Value.command) + @($server.Value.args | Where-Object { $_ -match '^[A-Za-z]:\\' })
  $missing = @($paths | Where-Object { -not (Test-Path $_) })
  if ($missing.Count -eq 0) { Ok "MCP $($server.Name)" } else { Warn "MCP $($server.Name): 파일이 없습니다 ($($missing -join ', '))" }
}

# 5. Manual steps
Say ''
Say '[4/4] 직접 해 주셔야 하는 일'
Say '  1. Claude Code 로그인: PowerShell에서 claude 를 실행하고 /login 으로 로그인합니다.'
Say '  2. Codex 로그인(ChatGPT를 쓸 때): codex login 을 실행합니다.'
Say '  3. 네이버웍스 관리자 화면 > 보안 > 서비스 권한 > 기본권한 에서 메일 IMAP/SMTP 사용을 허용합니다.'
Say '  4. 대표 계정 > 보안 > 외부 앱 비밀번호 에서 "OFNR 대시보드"용 비밀번호를 만듭니다.'
Say '  5. OFNR 운영 앱 > 설정 > 메일 에 메일 주소와 그 비밀번호를 넣고 연결 확인을 누릅니다.'
Say '  6. 스케치업: mcp_for_sketchup_v0.4.0-signed.rbz 를 확장 관리자에서 설치하고 Plugins > MCP Server > Start Server 를 누릅니다.'
Say "  7. 회사 견적서, 제안서, 계약서 양식을 $(Join-Path $DataRoot 'templates') 폴더에 넣습니다."

Say ''
if ($todo.Count -eq 0) { Say '준비가 끝났습니다. OFNR 운영 앱을 실행해 주세요.' }
else {
  Say "확인이 필요한 항목이 $($todo.Count)개 있습니다:"
  $todo | ForEach-Object { Say "  - $_" }
}
