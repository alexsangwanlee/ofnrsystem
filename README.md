# OFNR 운영

오프너(OFNR)의 메일, 프로젝트, AI 작업을 한 화면에서 관리하는 Windows 앱입니다.

- 네이버웍스 메일을 받아 AI가 분류하고 요약합니다. 메일을 프로젝트에 연결하고 답장을 씁니다.
- 프로젝트를 문의, 기획, 디자인, 제작, 운영, 리포트, 완료 단계로 관리합니다. 프로젝트 폴더는 자동으로 만들어집니다.
- 견적서, 제안서, 문서, 3D 모델, 그래픽 작업을 Claude Code 또는 ChatGPT(Codex)에 맡기고, 진행 로그와 결과 파일을 확인합니다.
- 결과물을 검토하며 남긴 의견이 스킬 규칙으로 쌓여 다음 작업에 반영됩니다.

AI는 메일을 보내지 않습니다. 메일 발송은 언제나 대표가 보내기 버튼을 눌러야 일어납니다.

## 구성

| 부분 | 하는 일 |
|---|---|
| OFNR 운영 앱 (Tauri) | 창, 트레이 아이콘, 알림, 자동 업데이트. 서버를 뒤에서 띄우고 끕니다 |
| 서버 (ASP.NET Core) | 메일 동기화, 작업 큐, AI 엔진 실행, 대시보드 MCP. 이 PC 안(127.0.0.1)에서만 열립니다 |
| PostgreSQL 17 | 프로젝트, 메일, 작업 기록 |
| Claude Code, Codex | 실제 AI 작업. 대표의 구독 로그인으로 돌아갑니다 |
| 외부 도구 (MCP) | 엑셀, 파워포인트, 스케치업, 일러스트레이터, 포토샵 조종 |

데이터는 `C:\OFNR`에, 프로젝트 파일은 `D:\OFNR\Projects`(D 드라이브가 없으면 `C:\OFNR\Projects`)에 쌓입니다.

## 설치

### 준비물

- Windows 10 (1809 이상) 또는 Windows 11
- 네이버웍스 Standard 이상 요금제 (IMAP/SMTP를 쓸 수 있는 요금제)
- Claude 구독. ChatGPT(Codex)도 쓰려면 ChatGPT 구독
- 쓰는 만큼: Office 2016 이상, SketchUp Pro 2026.1, Adobe Illustrator 2024 이상

### 1단계: 설치 스크립트 실행

1. 이 저장소를 내려받습니다. GitHub에서 Code > Download ZIP을 누르고 압축을 풉니다.
2. 압축을 푼 폴더에서 PowerShell을 열고 아래를 실행합니다.

   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts\setup.ps1
   ```

스크립트가 하는 일:

- Git, Node.js, Python, uv, .NET SDK, Claude Code, Codex를 점검하고 없으면 설치합니다.
- PostgreSQL 17을 설치합니다.
- 빠진 프로그램(PostgreSQL, .NET SDK, Node.js, Git)을 설치하는 동안 Windows가 관리자 권한 확인 창을 띄울 수 있습니다. 예를 눌러 주세요.
- `ofnr` 계정과 `ofnr_ops` 데이터베이스를 만들고 한글 검색이 되는지 확인합니다. 비밀번호는 Windows 암호화(DPAPI)로 `C:\OFNR\secrets`에 저장되고 설정 파일에는 들어가지 않습니다.
- 엑셀, 파워포인트, 스케치업, 일러스트레이터, 포토샵 MCP 서버를 `C:\OFNR\tools`에 설치하고 `C:\OFNR\mcp.json`을 만듭니다. 버전은 모두 고정되어 있습니다.
- Claude Code에 Anthropic 문서 스킬(docx, pdf, pptx, xlsx)을 플러그인으로 설치합니다. Claude 작업이 워드, PDF 문서를 만들 때 씁니다.
- 마지막에 직접 해 주셔야 하는 일을 보여 줍니다.

여러 번 실행해도 안전합니다. 이미 있는 것은 건너뜁니다. 중간에 `[주의]`가 나오면 그 내용을 해결하고 다시 실행해 주세요.

### 2단계: 앱 설치

1. 릴리스 페이지에서 `ofnr-ops_<버전>_x64-setup.exe`를 내려받아 실행합니다.
2. 처음 한 번은 "Windows의 PC 보호" 창이 뜹니다. **추가 정보**를 누른 뒤 **실행**을 누릅니다. 1차 배포는 코드 서명 없이 나가기 때문입니다. 이후 자동 업데이트에서는 다시 묻지 않습니다.
3. 관리자 권한은 필요 없습니다. 현재 사용자 계정에 설치됩니다.

## 처음 실행

1. **Claude Code 로그인**: PowerShell에서 `claude`를 실행하고 `/login`으로 로그인합니다.
2. **Codex 로그인** (ChatGPT를 쓸 때): `codex login`을 실행합니다.
3. **네이버웍스 메일 연결**
   1. 네이버웍스 관리자 화면에서 보안 > 서비스 권한 > 기본권한으로 가서 메일 IMAP/SMTP 사용을 허용합니다.
   2. 대표 계정에서 보안 > 외부 앱 비밀번호로 가서 "OFNR 대시보드"용 비밀번호를 만듭니다. 평소 로그인 비밀번호가 아닙니다.
   3. 앱에서 설정 > 메일에 메일 주소를 넣고 변경 저장을 누릅니다. 앱 비밀번호 칸에 방금 만든 비밀번호를 넣고 비밀번호 저장, 연결 확인을 누릅니다.
   4. 서버 주소는 기본값(IMAP `imap.worksmobile.com` 993, SMTP `smtp.worksmobile.com` 465) 그대로 둡니다.
4. **프로젝트 폴더**: 설정 > 작업 폴더에서 확인합니다. 영문, 숫자로 된 경로만 쓸 수 있습니다. Claude Code가 한글 경로에서 파일을 잘 찾지 못하기 때문입니다.
5. **회사 양식**: `C:\OFNR\templates` 폴더에 회사 양식을 넣습니다. 파일 이름은 문서 코드로 시작해야 합니다.

   | 문서 | 파일 이름 예 |
   |---|---|
   | 견적서 | `QUOTE.xlsx` |
   | 제안서 | `PROP.pptx` |
   | 문서(계약서, 공문) | `DOC.docx` |
   | 운영 매뉴얼 | `OPSMANUAL.docx` |
   | 결과보고서 | `REPORT.pptx` |

   양식이 없으면 AI가 기본 구성으로 만들고 결과에 "회사 양식 없음"을 표시합니다.
6. **상태 점검**: 설정 > 상태 점검에서 모든 항목에 확인 표시가 있는지 봅니다. AI 엔진 줄의 **엔진 점검**을 한 번씩 눌러 실제 작업이 도는지 확인합니다.
7. **자동 실행**: 설정 > 앱에서 "Windows에 로그인하면 자동으로 실행"을 켭니다.
8. **스케치업** (3D 작업을 할 때): `mcp_for_sketchup_v0.4.0-signed.rbz`를 https://github.com/zinin/sketchup-mcp2/releases 에서 받아 스케치업 확장 관리자에서 설치합니다. 3D 작업 전에 Plugins > MCP Server > Start Server를 누릅니다. 대시보드는 임의 Ruby 실행 도구(eval_ruby)를 막아 두지만, Plugins > MCP Server > Settings...에서 Enable Ruby evaluation도 꺼 두면 더 안전합니다.
9. **일러스트레이터** (그래픽 작업을 할 때): 작업 전에 일러스트레이터를 열어 둡니다.

## 매일 쓰기

- **오늘**: 확인이 필요한 일(AI 질문, 결과 검토, 실패한 작업), 미분류 메일, 다가오는 설치/오픈/철수 일정, 실행 중인 작업을 한 번에 봅니다.
- **메일**: 메일을 읽고 AI 요약과 추천 프로젝트를 확인합니다. 프로젝트 연결, 새 프로젝트 만들기, 답장 쓰기, 작업 맡기기를 여기서 합니다. 첨부 파일은 프로젝트의 `00_IN` 폴더에 저장됩니다.
- **프로젝트**: 단계별 보드입니다. 카드를 다음 단계로 옮기면 그 단계에 맞는 작업을 추천합니다. 추천만 하고 자동으로 실행하지는 않습니다.
- **작업 상세**: AI의 진행 로그가 실시간으로 올라옵니다. 끝나면 결과 파일을 열어 보고 완료하거나, 후속 지시로 고치게 합니다.
- **에이전트**: 실행 중, 대기 중인 작업, 앱 잠금(스케치업, 일러스트레이터, Office는 한 번에 하나씩), 이번 달 추정 비용을 봅니다.

알아 두면 좋은 것:

- 창을 닫아도 앱은 트레이(작업 표시줄 오른쪽 아래)에서 계속 돌며 메일을 받고 작업을 진행합니다. 완전히 끄려면 트레이 아이콘을 누르고 **종료**를 고릅니다.
- 결과 파일 이름은 `OFNR-26031_QUOTE_v03.xlsx`처럼 작업번호, 문서 코드, 버전으로 정해집니다. 덮어쓰지 않고 버전을 올립니다.
- AI가 판단이 필요하면 질문을 남기고 작업을 멈춥니다. 오늘 화면에서 답하면 같은 작업이 이어서 진행됩니다.
- 스케치업이나 일러스트레이터가 꺼져 있으면 작업이 "앱 준비 필요"로 기다립니다. 앱을 켜고 다시 시도하면 됩니다.

## 스킬 학습

1. 작업이 끝나면 상태가 검토로 바뀝니다.
2. 결과 파일을 보고 작업 상세의 검토 의견 칸에 고칠 점을 씁니다. **스킬에 반영**에서 해당 스킬을 고르고 보내기를 누릅니다.
3. AI가 그 스킬의 `학습된 규칙`에 날짜와 출처 프로젝트를 붙여 한 줄 규칙을 추가합니다. 다음 작업부터 이 규칙을 따릅니다.
4. 스킬 화면에서 규칙을 직접 고치거나, 변경 이력을 보고 이전 버전으로 되돌릴 수 있습니다.

스킬 파일은 `C:\OFNR\claude-plugin\skills\<스킬 이름>\SKILL.md`에 있고, 바뀔 때마다 자동으로 기록(git 커밋)됩니다. Claude Code와 Codex가 같은 스킬을 쓰므로 엔진을 바꿔도 규칙이 그대로 갑니다.

## 업데이트

### 앱에서 업데이트 받기

1. 설정 > 앱에서 **업데이트 확인**을 누릅니다. 실행 중인 작업이 있으면 끝날 때까지 누를 수 없습니다. 업데이트하려면 서버를 꺼야 하고, 그러면 실행 중인 작업이 끊기기 때문입니다.
2. 새 버전이 있으면 **지금 업데이트**를 누릅니다. 내려받기가 끝나면 서버가 멈추고 설치 화면이 진행 표시줄과 함께 자동으로 진행됩니다.
3. 새 버전의 서버가 처음 뜰 때 데이터베이스 구조가 바뀌면, 바꾸기 직전에 자동으로 백업합니다.
4. 새 버전의 작업 종류(`recipes.json`), 스킬, 회사 지식도 이때 들어옵니다. 대표가 고치지 않은 파일만 바뀝니다. 스킬은 학습된 규칙을 그대로 두고 그 위의 본문만 새것으로 바뀝니다. 대표가 스킬 본문이나 `C:\OFNR\recipes.json`을 직접 고쳤다면 그 파일은 그대로 남으니, 새 내용이 필요하면 설치 폴더의 같은 파일과 비교해 옮깁니다.

### 새 버전 만들어 배포하기 (개발자)

처음 한 번:

1. 업데이트 서명 키를 만듭니다.

   ```powershell
   npm --prefix desktop ci
   npm --prefix desktop run tauri signer generate -- -w $env:USERPROFILE\.tauri\ofnr-ops.key
   ```

   `ofnr-ops.key.pub` 파일의 **내용**을 `desktop/src-tauri/tauri.conf.json`의 `plugins.updater.pubkey`에 붙여 넣습니다. 개인 키(`ofnr-ops.key`)와 그 비밀번호는 USB 같은 오프라인 장소에도 백업해 둡니다. 개인 키를 잃어버리면 이미 설치된 앱에 다시는 업데이트를 보낼 수 없습니다.
2. 설치 파일만 올릴 **공개** 저장소를 GitHub에 만듭니다(예: `ofnr-ops-releases`). README 하나를 커밋해 `main` 브랜치를 만들어 둡니다. 비공개 저장소의 릴리스는 앱이 로그인 없이 읽을 수 없습니다.
3. `OWNER`를 실제 GitHub 계정 이름으로 바꿉니다. 두 곳입니다: `desktop/src-tauri/tauri.conf.json`의 `plugins.updater.endpoints`, `.github/workflows/release.yml`의 `owner`.
4. 이 저장소의 Settings > Secrets and variables > Actions에 비밀 값 세 개를 넣습니다.
   - `TAURI_SIGNING_PRIVATE_KEY`: 개인 키 파일의 내용
   - `TAURI_SIGNING_PRIVATE_KEY_PASSWORD`: 개인 키 비밀번호
   - `RELEASES_PAT`: 릴리스 저장소에 Contents 쓰기 권한이 있는 GitHub 토큰

버전마다:

1. `desktop/src-tauri/tauri.conf.json`의 `version`을 올립니다(예: `0.2.0`). `desktop/src-tauri/Cargo.toml`, `desktop/package.json`, `src/Ofnr.Server/Ofnr.Server.csproj`의 버전도 같이 맞춥니다.
2. 커밋하고 태그를 올립니다.

   ```powershell
   git tag v0.2.0
   git push origin v0.2.0
   ```

3. GitHub Actions(`release`)가 Windows에서 화면, 서버, 앱을 빌드하고 설치 파일, 서명 파일(`.sig`), `latest.json`을 릴리스 저장소에 올립니다. 설치된 앱은 `latest.json`을 보고 새 버전을 찾습니다.

## 백업과 복구

- 서버가 하루 한 번 데이터베이스를 `C:\OFNR\backups\ofnr-<날짜-시각>.dump`로 백업하고 최근 14개를 남깁니다. 데이터베이스 구조를 바꾸는 업데이트 직전에도 백업합니다.
- 프로젝트 파일은 OneDrive나 NAS 동기화로 지킵니다.
- `C:\OFNR\secrets`의 비밀번호는 이 PC의 이 Windows 계정에서만 풀 수 있습니다. 새 PC로 옮기면 `setup.ps1`을 다시 실행하고 메일 앱 비밀번호를 다시 넣어 주세요.

데이터베이스 복구:

1. 트레이 아이콘에서 **종료**를 눌러 앱을 끕니다.
2. PowerShell에서 아래를 실행합니다. 마지막 줄의 백업 파일 이름은 복구할 파일로 바꿉니다. 관리자 비밀번호는 `setup.ps1`이 PostgreSQL을 설치할 때 만들어 암호화해 둔 것을 씁니다. PostgreSQL을 직접 설치했다면 그때 정한 비밀번호를 `$env:PGPASSWORD`에 넣으세요.

   ```powershell
   Add-Type -AssemblyName System.Security
   $env:PGPASSWORD = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes('C:\OFNR\secrets\postgres.bin'), $null, 'CurrentUser'))
   & 'C:\Program Files\PostgreSQL\17\bin\pg_restore.exe' -h 127.0.0.1 -U postgres -d ofnr_ops --clean --if-exists 'C:\OFNR\backups\ofnr-20261027-0300.dump'
   ```

3. 앱을 다시 실행합니다.

## 문제 해결

| 증상 | 확인할 것 |
|---|---|
| "서버를 시작하지 못했습니다" 창이 뜸 | `C:\OFNR\settings.json`이 깨졌는지 봅니다. 고치기 어려우면 그 파일 이름을 바꾸고 `setup.ps1`을 다시 실행한 뒤 앱을 켭니다. 백신이 `ofnr-server.exe`를 막았는지도 확인합니다 |
| 상태 점검에서 데이터베이스 오류 | 서비스 앱(services.msc)에서 `postgresql-x64-17`이 실행 중인지 봅니다. 그다음 `setup.ps1`을 다시 실행합니다 |
| AI 엔진 "로그인 필요" | `claude`를 실행해 `/login`, Codex는 `codex login`. 그다음 설정 > 상태 점검에서 엔진 점검 |
| 메일 연결 확인 실패 | 관리자 화면의 IMAP/SMTP 허용, 외부 앱 비밀번호(일반 비밀번호가 아님), 요금제를 확인합니다 |
| 작업이 "앱 준비 필요"에서 멈춤 | 스케치업을 열고 MCP Server > Start Server, 또는 일러스트레이터를 엽니다. Office 작업이면 같은 파일을 연 창을 닫습니다 |
| "프로젝트 폴더에 AI 설정 파일이 있어 실행을 멈췄습니다" | 고객이 보낸 자료에 `.claude`, `.mcp.json`, `CLAUDE.md` 같은 AI 설정이 섞여 있습니다. 내용을 확인하고 지운 뒤 다시 실행합니다 |
| MCP 도구 오류 | `setup.ps1`을 다시 실행하면 `[주의]`로 빠진 파일을 알려 줍니다. `C:\OFNR\mcp.json`을 새로 만들려면 그 파일을 지우고 다시 실행합니다 |
| 업데이트 설치가 끝나지 않음 | 트레이에서 종료한 뒤 릴리스 페이지의 설치 파일을 직접 실행합니다 |
| 한글을 입력하고 Enter를 눌렀는데 저장되지 않음 | 한글 조합 중의 Enter는 글자 확정으로만 씁니다. 한 번 더 누르거나 저장 버튼을 누릅니다 |

## 2차 계획 (직원 공유)

- PostgreSQL을 사무실 서버로 옮깁니다. 같은 17 버전이라 백업을 그대로 복원하면 됩니다.
- 서버를 사무실 PC에서 늘 켜 두고, 직원의 앱이 그 주소를 바라보게 합니다.
- 직원 로그인과 권한을 켭니다.
- 스케치업, 일러스트레이터, Office를 조종하는 작업은 그 앱이 깔린 PC에서 돌아야 하므로, 디자인 PC마다 작업 실행부를 따로 띄웁니다.
- AI 엔진은 직원별 좌석이나 API 키를 씁니다. 대표의 구독 로그인을 같이 쓰면 약관 위반입니다.
- 직원에게 배포하기 전에 OV 코드 서명 인증서를 삽니다. Azure 서명 서비스는 한국 법인이 쓸 수 없습니다.

## 개발

구조, 규칙, 기능을 더하는 방법은 [CLAUDE.md](CLAUDE.md)에 있습니다. Claude Code로 이 저장소를 열면 그 내용을 읽고 작업합니다.

| 할 일 | 명령 |
|---|---|
| 서버 테스트 | `dotnet test` |
| 화면 테스트, 빌드 | `npm --prefix web test`, `npm --prefix web run build` |
| 데스크톱 셸 점검 (Linux) | `bash scripts/check-desktop.sh` |
| Linux에서 개발 실행 | `bash scripts/dev.sh` (PostgreSQL, 서버, 화면을 띄우고 열 주소를 보여 줍니다) |
| Windows에서 앱으로 실행 | `scripts\publish-sidecar.ps1` 다음 `npm --prefix desktop run tauri dev` |
