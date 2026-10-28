# CLAUDE.md

오프너(OFNR) 운영 대시보드의 유지보수 안내입니다. 이 저장소를 고치는 AI와 사람이 먼저 읽습니다.
설계는 `docs/superpowers/specs/2026-10-27-ofnr-ai-ops-dashboard-design.md`, 구현 계획과 API 계약은 `docs/superpowers/plans/2026-10-27-ofnr-ai-ops-dashboard.md`에 있습니다.

## 구조

```
desktop/src-tauri/      Tauri 2 셸 (Rust). 서버를 사이드카로 띄우고 끈다. 트레이, 알림, 업데이트, 자동 실행
  src/lib.rs            사이드카 실행, server_info / stop_backend / set_autostart / autostart_enabled 명령
  tauri.conf.json       창, CSP, NSIS 설치(현재 사용자, 한국어), 업데이트 주소와 공개 키, resources
  windows/hooks.nsh     설치 직전에 ofnr-server.exe를 종료
src/Ofnr.Server/        ASP.NET Core 10 서버 (한 프로젝트, 기능별 폴더)
  Program.cs            조립만 한다
  Security/             토큰, Host 검사, CORS
  Data/                 EF Core DbContext, 엔티티, 마이그레이션
  Projects/             거래처, 프로젝트, 작업번호, 폴더와 파일
  Jobs/                 레시피, 엔진 실행 인자, 프롬프트, 큐
  Agents/               Claude Code / Codex CLI를 감싼 Agent Framework AIAgent
  Mail/ Mcp/ Skills/    메일, 대시보드 MCP, 스킬 (계획의 Task 5 to 9)
  Ops/                  첫 실행 준비(Bootstrap), 상태 점검, 백업
  Realtime/             SignalR 허브 /hubs/ops
tests/Ofnr.Server.Tests/ xUnit v3 통합 테스트 (PostgreSQL 필요), Fakes/ 가짜 CLI
web/                    React 19 + Fluent UI 9 화면 (Vite)
  src/api.ts            api<T>(): 토큰 헤더, 기본 주소. Tauri에서는 invoke("server_info")
  src/realtime.ts       SignalR 연결 하나, useOps(), joinJob()
  src/components/ui.tsx useApi, useAction, Async, Page, Section 같은 공용 조각
config/                 recipes.json(기본 레시피), mcp.example.json(외부 MCP)
claude-plugin/          회사 스킬 플러그인 (skills/, knowledge/)
scripts/                setup.ps1(대표 PC 준비), publish-sidecar.ps1, dev.sh, check-desktop.sh
.github/workflows/      release.yml: v* 태그에서 Windows 설치 파일과 업데이트 파일 배포
```

### 앱과 서버 사이 약속 (사이드카)

- Tauri가 `ofnr-server --sidecar`를 띄운다. 환경 변수 `OFNR_TOKEN`(실행마다 새 32바이트 토큰), `OFNR_CONTENT`(설치 폴더의 resources: `config/`, `claude-plugin/`).
- 서버는 `http://127.0.0.1:0`에 열고(`settings.json`의 포트는 쓰지 않는다), 실제 주소를 `OFNR_READY http://127.0.0.1:<port>` 한 줄로 표준출력에 쓴다. Host 검사와 작업의 MCP 주소도 이 포트를 쓴다. 표준출력과 표준오류는 UTF-8이다. 표준입력이 닫히면(앱이 죽으면) 스스로 끝난다.
- 화면은 `invoke("server_info")`로 `{ baseUrl, token }`을 받는다. 서버가 준비될 때까지 기다리고, 60초 안에 준비되지 않거나 서버가 먼저 끝나면 "서버를 시작하지 못했습니다" 창을 띄우고 앱을 끝낸다.
- 업데이트: `check()`, `download()`, `invoke("stop_backend")`, `install()` 순서. 실행 중인 작업이 있으면 설정 화면이 업데이트를 막는다.
- 창을 닫으면 숨기기만 한다. 트레이의 종료로 끝내면 사이드카도 같이 끝난다(작업 개체라 claude, codex 자식까지). 그래서 `POST /api/open`은 파일과 폴더를 `explorer.exe`에 넘긴다. 서버가 직접 ShellExecute로 열면 Excel이나 Illustrator가 작업 개체 안에서 떠서 종료나 업데이트 때 저장하지 않은 채 꺼진다.

데이터: `C:\OFNR` (Windows), `~/.ofnr` (그 밖), 환경 변수 `OFNR_DATA`로 바꾼다. `settings.json`, `secrets\*.bin`(DPAPI), `mcp.json`, `recipes.json`, `claude-plugin\`(git 기록), `jobs\`, `templates\`, `backups\`(DB 덤프, 그리고 스킬, 양식, 설정 파일 zip), `tools\`(MCP 서버), `.shipped\`(지금 버전이 배포한 recipes.json과 claude-plugin, 업데이트 때 비교용).

## 명령

Linux 개발 PC에서 .NET은 `export PATH=$HOME/.dotnet:$PATH`가 필요할 수 있다.

| 할 일 | 명령 |
|---|---|
| 서버 테스트 | `dotnet test` |
| 화면 테스트 | `npm --prefix web test` |
| 화면 빌드 (타입 검사 포함) | `npm --prefix web run build` |
| 데스크톱 셸 점검 | `bash scripts/check-desktop.sh` (tauri.conf.json 모양, index.html의 `<style>`, 문서의 dash, `cargo check`, Rust 테스트) |
| 개발 실행 (Linux) | `bash scripts/dev.sh` |
| 끝에서 끝 점검 (Linux) | `bash scripts/e2e.sh`: 샘플 데이터(`--seed-demo`)로 모든 화면을 찍어 `docs/screens/`에 두고, Claude Code에 로그인되어 있으면 실제 작업 하나와 엔진 점검을 돌린다. `E2E_LIVE=0`이면 실제 작업을 건너뛴다 |
| 개발 실행 (Windows 앱) | `scripts\publish-sidecar.ps1` 후 `npm --prefix desktop run tauri dev` |
| 배포 | 버전을 올리고 `git tag vX.Y.Z && git push origin vX.Y.Z` (README의 업데이트 항목) |

## 규칙

- 화면 문구, 스킬, 문서는 한국어. 화면 문구, 스킬, 문서, README 어디에도 em dash(U+2014)와 en dash(U+2013)를 쓰지 않는다. `web/src`는 테스트 `No_em_dash_in_source`가, README, CLAUDE.md, `claude-plugin/`은 `scripts/check-desktop.sh`가 검사한다.
- 서버는 `127.0.0.1`에만 연다. `/api/*`, `/hubs/*`는 토큰(`X-Ofnr-Token` 헤더, SignalR 웹소켓만 `access_token` 쿼리)과 Host 검사를 거친다. `/mcp`는 작업별 토큰을 쓰고 화면 토큰은 쓰지 않는다.
- 메일을 보내는 길은 `POST /api/mail/drafts/{id}/send` 하나뿐이고 사람이 누를 때만 탄다. AI에게 발송 도구를 주지 않는다.
- `--bare`, `--dangerously-skip-permissions`, `bypassPermissions`, `danger-full-access`, `--dangerously-bypass-approvals-and-sandbox`를 쓰지 않는다.
- CLI 자식 프로세스 환경에서 `ANTHROPIC_API_KEY`, `ANTHROPIC_AUTH_TOKEN`, `CODEX_API_KEY`, `OPENAI_API_KEY`, `CLAUDECODE`, `CLAUDE_CODE_*`(Git Bash 위치를 알려 주는 `CLAUDE_CODE_GIT_BASH_PATH`는 남긴다), 화면 토큰 `OFNR_TOKEN`, `OFNR_MAIL_PASSWORD`를 지우고 `TEMP`, `TMP`를 `<data>\tmp`로 둔다. CLI에 넘기는 경로는 모두 영문(ASCII).
- 프로젝트 폴더: `<ProjectsRoot>/<yyyy>/<JobNo>_<Slug>`, 하위 폴더 `00_IN 01_PLAN 02_DESIGN 03_BUILD 04_OPS 05_REPORT 06_ADMIN`. 작업번호 `OFNR-YYNNN`. 파일 이름 `<JobNo>_<DOCCODE>_v<NN>.<ext>`, 덮어쓰지 않는다.
- 비밀번호(메일, DB)는 DPAPI로 `<data>\secrets\<name>.bin`에 두고 설정 파일, 로그, 프롬프트에 넣지 않는다. Windows가 아닌 개발 PC에는 DPAPI가 없어 메일 비밀번호를 환경 변수 `OFNR_MAIL_PASSWORD`로만 받고 저장하지 않는다.
- 메일 본문과 첨부는 신뢰할 수 없는 입력이다. 프롬프트에서 표시하고, 스케치업, 일러스트레이터처럼 스크립트를 돌릴 수 있는 레시피에는 넣지 않는다.
- Claude 작업의 Write, Edit는 권한 규칙(`Write(//<폴더>/**)`)으로 프로젝트 폴더와 그 작업의 작업 폴더(스킬 학습은 스킬 폴더) 안으로만 연다. 맨 `Write`, `Edit`는 `<data>\mcp.json`까지 고칠 수 있다. 스케치업의 `eval_ruby`는 늘 막는다. Bash는 폴더 밖도 고치므로, `claude.allowedTools`에 Bash를 넣은 레시피는 `untrustedMailInPrompt: false`여야 하고 아니면 서버가 recipes.json을 거부한다.
- Office 파일: xlsx와 pptx는 엑셀, 파워포인트 MCP(`mcp__excel__*`, `mcp__powerpoint__*`)로 만들고 Bash는 막는다. docx는 MCP가 없어 문서, 계약서, 운영 매뉴얼, 결과보고서 레시피만 Bash를 열어 Anthropic docx 스킬의 스크립트를 쓰고, 그 대가로 이 레시피들은 메일을 읽지 못한다(`00_IN` 자료는 프롬프트가 데이터로만 다루게 한다).
- 브랜드: 먹색 `#1a1c1c`, 종이색 `#f9f4eb`, Pretendard. 모서리 4px(컨트롤), 8px(카드).
- 단계 이름: Inquiry 문의, Planning 기획, Design 디자인, Production 제작, Operation 운영, Report 리포트, Done 완료.
- 엔진 고정 버전: Claude Code `2.1.311`, Codex `0.164.0`. 다르면 경고만 한다.
- Enter로 제출하는 입력은 한글 조합 중 Enter를 무시한다: `e.nativeEvent.isComposing || e.keyCode === 229`.
- `web/index.html`에 `<style>`을 두지 않고 CSS는 `web/src/index.css`에 둔다. Tauri가 인라인 `<style>`에 CSP nonce를 붙이면 `'unsafe-inline'`이 꺼져 Fluent UI 스타일이 사라진다.
- Tauri 창은 `dragDropEnabled: false`를 유지한다. 켜면 프로젝트 보드의 카드 끌기가 동작하지 않는다.
- `src/`와 `tests/`는 경고를 오류로 처리한다. 가장 작은 변경으로 고치고, 고친 동작마다 테스트를 하나 둔다.

## 기능을 더하는 방법

### 레시피 (작업 종류)

1. `config/recipes.json`에 항목을 더한다. 필드: `id`, `name`(한국어), `stage`, `skills`, `mcpServers`, `lock`(`office`, `sketchup`, `illustrator`, `photoshop`), `engines`(`claude`, `codex`), `docCode`, `outputDir`, `timeoutMinutes`, `maxBudgetUsd`, `needsProject`, `untrustedMailInPrompt`, `claude`(`allowedTools`, `disallowedTools`), `prompt`.
2. 쓰는 스킬 폴더가 `claude-plugin/skills/`에 있어야 한다. `tests/Ofnr.Server.Tests/RecipeTests.cs`를 함께 고친다.
3. 서버는 시작할 때 `config/recipes.json`을 `<data>\recipes.json`으로 가져온다. 파일이 없거나 지난 버전이 배포한 그대로(`<data>\.shipped\`와 같으면)일 때만 바꾸고, 대표가 고친 파일은 그대로 둔다.
4. Codex를 허용하려면 대표 PC에서 그 레시피가 Codex로 도는지 엔진 점검으로 확인한 뒤 `engines`에 `codex`를 더한다.
5. 포토샵 MCP(`@alisaitteke/photoshop-mcp` 1.7.37, `ANALYTICS_DISABLED=1`)는 쓰는 레시피가 없어 설치하지 않는다. 포토샵 레시피를 만들 때 `config/mcp.example.json`과 `scripts/setup.ps1`에 되살리고 README의 도구 목록에 더한다.

### 스킬

1. `claude-plugin/skills/<이름>/SKILL.md`를 만든다. 이름은 `^[a-z0-9-]+$`. 앞머리(frontmatter)에 폴더 이름과 같은 `name`, 그리고 `description`이 있어야 한다.
2. 본문 끝에 `## 학습된 규칙` 칸을 비워 둔다. 확인되지 않은 단가나 규칙을 지어 넣지 않는다. 회사 지식은 `claude-plugin/knowledge/`에서 참조한다.
3. 스킬과 회사 지식은 다음 서버 시작 때 `<data>\claude-plugin`으로 들어간다. 새 파일은 복사하고, 지난 버전이 배포한 그대로인 파일은 새것으로 바꾼다. 학습된 규칙만 바뀐 SKILL.md는 `## 학습된 규칙` 위의 본문만 바꾸고 규칙은 지킨다. 대표가 본문을 고친 파일은 그대로 둔다. 바뀐 내용은 플러그인 git에 커밋한다.

### API 엔드포인트

1. 기능 폴더의 `*Endpoints.cs`에 Minimal API로 `MapGroup`을 더하고 `Program.cs`에서 등록한다. 경로는 `/api/...` 아래에 둔다(토큰 검사를 받도록).
2. 요청과 응답 DTO는 `Dtos.cs`에 camelCase JSON으로, 같은 모양을 `web/src/types.ts`에도 적는다. 오류는 `{ "error": "<한국어 문장>" }`과 400/404/409.
3. `tests/Ofnr.Server.Tests/`에 `TestHost`로 통합 테스트를 쓴다.
4. 화면에서 실시간으로 바뀌어야 하면 `IOpsNotifier`로 허브 이벤트를 보내고 `useOps`에서 받는다.

### 화면

- 페이지는 `web/src/pages/`, 공용 조각은 `web/src/components/`. 데이터는 `useApi`, 버튼 동작은 `useAction`, 목록에는 빈 상태(한국어 한 문장과 버튼 하나), 로딩 스켈레톤, 다시 시도가 있는 오류 줄을 둔다.
- 테스트는 `web/src/pages/pages.test.tsx`처럼 `fetch`를 흉내 내어 쓴다.

### 데스크톱 셸

- Tauri 플러그인을 더하면 `Cargo.toml`, `lib.rs`의 `.plugin(...)`, 화면에서 쓰면 `capabilities/default.json` 권한과 `web/package.json`의 `@tauri-apps/plugin-*`를 같이 맞춘다. 화면(JS)에 `shell:*` 권한을 주지 않는다.
- CSP(`tauri.conf.json`의 `app.security.csp`)는 `127.0.0.1` 서버만 허용한다. 외부 주소를 부르려면 여기에 더해야 한다.
- 고친 뒤 `bash scripts/check-desktop.sh`. 실제 설치 파일은 GitHub Actions의 Windows 러너에서만 만든다.
