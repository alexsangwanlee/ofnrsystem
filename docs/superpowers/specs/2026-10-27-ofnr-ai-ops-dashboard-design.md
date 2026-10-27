# OFNR AI 운영 대시보드 설계서

- 작성일: 2026-10-27
- 상태: 초안 v2 (기술 구성 확정, 일부 세부는 조사 반영 중)
- 대상: 주식회사 오프너(OFNR). 1차는 대표 1인이 Windows PC에서 쓰고, 2차에 직원 공유로 넓힌다.

## 1. 목적

오프너의 업무는 문의 메일에서 시작해 기획, 디자인, 제작, 운영, 리포트로 흘러간다.
이 도구는 그 흐름을 한 화면에 모으고, 반복 작업을 AI에게 맡기는 운영 도구다.

성공 기준:

1. 네이버웍스 메일이 들어오면 대시보드에서 분류, 요약, 프로젝트 연결까지 끝난다.
2. 견적서, 제안서, 문서, 3D 모델, 그래픽 작업을 버튼 하나로 AI에게 맡기고, 진행 로그와 결과 파일을 대시보드에서 확인한다.
3. AI 실행 엔진을 Claude Code와 ChatGPT(Codex) 중에서 고를 수 있다.
4. 실제 작업을 검토하며 남긴 수정 의견이 스킬 규칙으로 쌓여 다음 작업에 반영된다.
5. 대표가 Claude Code로 이 저장소를 열어 기능을 고치고 늘릴 수 있다. 새 버전은 앱 자동 업데이트로 받는다.

## 2. 대표가 말한 것과 가정

대표가 말한 것:

- 네이버웍스 메일을 주로 쓴다.
- 메일과 프로젝트를 Windows에서 AI와 함께 관리하고 분업한다.
- 일러스트레이터, 스케치업, PPT, DOCX, Excel 작업(견적서, 제안서, 3D, 디자인)을 업무에 맞춰 진행한다.
- 홈페이지에 붙이지 않는다. Windows 앱으로 직접 관리하고 계속 업데이트한다.
- 유지보수까지 생각한 제대로 된 제작을 원한다.
- 홈페이지에서 확인할 수 없는 내용은 비워 두고, 실제 작업을 하며 채운다.
- 기술 구성: C#/.NET 10, ASP.NET Core, React + TypeScript, Tauri 2, PostgreSQL, EF Core + Npgsql, Microsoft Agent Framework, MCP C# SDK, SignalR, Tauri Updater. AI는 Claude Code 또는 ChatGPT를 연결해 쓴다.
- 1차는 대표 혼자 쓰고, 나중에 직원도 쓴다.

가정 (대표 확인 필요):

- AI 실행은 PC에 설치된 Claude Code CLI와 OpenAI Codex CLI를 대표의 구독 로그인으로 돌린다. Anthropic 문서상 대표 본인 PC에서 본인 로그인으로 돌리는 헤드리스 실행은 구독 한도에서 차감된다. API 키는 선택 사항이다.
- 프로젝트 파일은 PC의 한 폴더(기본 `D:\OFNR\Projects`)에 쌓는다.
- AI는 메일을 직접 보내지 않는다. 회신은 초안까지 만들고 대표가 확인 후 보낸다.
- 네이버웍스는 Standard 이상 요금제다. IMAP/SMTP는 이 요금제부터 쓸 수 있다.

## 3. 범위

1차 범위:

- 메일함 동기화, AI 분류와 요약, 프로젝트 연결, 회신 작성과 발송
- 거래처, 프로젝트 보드(6단계와 수주 게이트), 프로젝트 상세(메일, 작업, 파일, 활동), 프로젝트 폴더 자동 생성
- AI 작업: 레시피 선택, 실행 큐, 실시간 로그, 승인 요청, 결과 파일 버전 관리, 검토 후 승인 또는 수정 요청
- 오늘 화면: 확인 필요, 미분류 메일, 다가오는 일정, 실행 중 작업
- 에이전트 화면: 실행 상태, 앱 잠금, 추정 비용, MCP 상태
- 스킬: 회사 지식과 작업별 스킬, 검토 의견을 규칙으로 쌓는 학습 구조
- 설정: 메일 계정, 작업 폴더, AI 엔진, MCP 서버, 동시 실행 수
- 설치, 자동 업데이트, 백업

뺀 것 (요청이 오면 추가):

- 직원 로그인과 권한 (2차. 15장의 확장 경로만 설계에 반영)
- 회계 원장, 세금계산서 발행, 급여
- 캘린더 양방향 동기화, 네이버웍스 메신저 알림 봇
- 한글(HWP) 문서 자동화, 포토리얼 렌더 자동화
- 모바일 앱

## 4. 기술 구성

| 영역 | 선택 | 역할 |
|---|---|---|
| 핵심 언어 | C# / .NET 10 | 서버 전체 |
| 서버 | ASP.NET Core 10 Minimal API | REST API, 화면 파일 제공, 백그라운드 작업 |
| 화면 | React + TypeScript (Vite), Fluent UI v9 | 대시보드 UI. Windows 느낌의 Fluent를 오프너 먹색(#1a1c1c)과 종이색(#f9f4eb), Pretendard로 꾸민다 |
| 데스크톱 앱 | Tauri 2 | 창, 트레이, 알림, 로그인 시 자동 실행, 파일 열기, 서버 프로세스 관리 |
| 데이터베이스 | PostgreSQL | 1차는 대표 PC, 2차는 사무실 서버 |
| DB 연결 | EF Core 10 + Npgsql | 엔티티, 마이그레이션 |
| AI 시스템 | Microsoft Agent Framework 1.x | AI 엔진 공통 규격(AIAgent). Claude Code와 Codex를 같은 규격의 에이전트로 감싼다 |
| AI 엔진 | Claude Code CLI, OpenAI Codex CLI | 실제 작업 수행. 스킬, MCP, 파일 작업은 엔진이 처리한다 |
| 외부 도구 연결 | MCP C# SDK | 대시보드 자체 MCP 서버(프로젝트 정보, 승인 요청, 결과 보고), 외부 MCP 상태 점검 |
| 실시간 동기화 | SignalR | 작업 로그 스트리밍, 승인 요청, 화면 갱신 |
| 메일 | MailKit / MimeKit | 네이버웍스 IMAP 수신, SMTP 발송 |
| 자동 업데이트 | Tauri Updater | GitHub Releases에 올린 설치 파일로 업데이트 |

확인한 버전(2026-10-27): .NET SDK 10.0.403, Microsoft.Agents.AI 1.26.0, ModelContextProtocol.AspNetCore 2.3.0, Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3, MailKit 4.18.2, Claude Code 2.1.311.

Agent Framework에는 Claude Code나 Codex CLI용 공식 패키지가 없다(NuGet에는 Anthropic API, OpenAI API, GitHub Copilot용만 있다). 그래서 두 CLI를 감싸는 에이전트 클래스를 직접 만든다.

## 5. 구조

```
[Tauri 앱: 창, 트레이, 알림, 파일 열기]
      |  화면 + REST + SignalR (127.0.0.1, 실행마다 바뀌는 토큰)
      v
[Ofnr.Server: ASP.NET Core, Tauri가 띄우는 사이드카 프로세스]
      |-- PostgreSQL (EF Core)
      |-- 메일 동기화 (MailKit, 네이버웍스 IMAP/SMTP)
      |-- 작업 큐 (동시 실행 수, 앱 잠금, 엔진별 동시 실행)
      |-- 대시보드 MCP 서버 (/mcp, 작업마다 1회용 토큰)
      `-- AI 엔진 실행 (Agent Framework AIAgent)
              |-- Claude Code CLI (claude -p, stream-json)
              `-- Codex CLI (codex exec --json)
                     `-- 외부 MCP: 일러스트레이터, 포토샵, 스케치업, 엑셀, 파워포인트
```

- 서버는 대표가 로그인한 세션의 일반 프로세스로 돈다. 일러스트레이터, 스케치업, Office의 COM 조종과 CLI 로그인 정보는 Windows 서비스(세션 0)에서 쓸 수 없기 때문이다.
- 1차에서는 Tauri가 서버를 사이드카로 띄우고 끈다. Tauri 앱은 로그인 시 자동 실행된다(작업 스케줄러를 쓰게 되면 실행 시간 제한을 0으로 둔다. 기본값 3일이 지나면 꺼진다). 2차에서는 Tauri가 사무실 서버 주소를 바라본다(15장).
- 서버 안의 작업 실행부는 별도 모듈로 둔다. 2차에서 디자인 PC마다 실행부만 따로 돌리기 위해서다.

### 5.1 솔루션 구성

```
ofnr-ops/
  src/Ofnr.Server/          ASP.NET Core 서버 (한 프로젝트, 기능별 폴더)
    Data/                   DbContext, 엔티티, 마이그레이션
    Mail/                   동기화, 발송, 분류
    Projects/               거래처, 프로젝트, 폴더, 결과물
    Jobs/                   레시피, 큐, 잠금, 실행 기록, 승인
    Agents/                 ClaudeCodeAgent, CodexAgent (Agent Framework)
    Mcp/                    대시보드 MCP 도구
    Realtime/               SignalR 허브
  tests/Ofnr.Server.Tests/  xUnit, PostgreSQL 통합 테스트
  web/                      React + TypeScript 화면
  desktop/                  Tauri 2 앱 (src-tauri)
  claude-plugin/            회사 스킬 묶음 (Claude Code 플러그인 형식)
  config/                   레시피, MCP 설정 예시
  scripts/                  설치, 백업, 개발 스크립트 (PowerShell)
  docs/                     설계서, 운영 안내
  CLAUDE.md                 AI가 저장소를 고칠 때 읽는 안내
```

계층을 여러 프로젝트로 나누지 않는다. 서버 한 프로젝트 안에서 기능별 폴더로 나눠 AI가 찾고 고치기 쉽게 한다.

## 6. 데이터 모델

| 엔티티 | 핵심 필드 |
|---|---|
| User 사용자 | 이름, 이메일, 역할(owner/staff). 1차는 대표 1명 자동 로그인 |
| Client 거래처 | 이름, 사업자번호, 담당자, 메일 도메인(자동 매칭용), 메모 |
| Project 프로젝트 | 작업번호(`OFNR-26031`), 프로젝트명, 거래처, 단계, 상태(진행/보류/실주/완료), 장소, 설치/오픈/종료/철수 일자, 견적액/계약액/실행원가, 폴더 경로, 지침 |
| MailMessage 메일 | IMAP 폴더/UID/UIDVALIDITY, Message-ID, In-Reply-To, References, 스레드 키, 보낸이, 받는이, 제목, 받은 시각, 본문(텍스트, 정제된 HTML), 첨부 목록, 프로젝트, 처리 상태(신규/연결/답장 필요/완료), AI 분류, AI 요약, AI 추출 필드 |
| MailDraft 회신 초안 | 원본 메일, 받는이, 제목, 본문, 첨부, 작성자(대표/AI), 상태(초안/발송) |
| Job 작업 | 프로젝트, 레시피, 요청 내용, 상태(대기/실행/확인 필요/검토/완료/취소/실패), 엔진(claude/codex), 출처(메일/단계/수동), 연결 메일 |
| JobRun 실행 | 작업, 순번, 엔진 세션 ID, 상태(queued/working/blocked/done/failed/stopped), 시작/종료, 추정 비용, 토큰, 오류 |
| JobEvent 실행 로그 | 실행, 순번, 종류(text/tool/tool_result/result/error/system), 요약, 원문 |
| Approval 확인 요청 | 실행, 종류(tool/question/deliverable), 내용, 응답(accept/edit/respond/ignore), 결정 시각 |
| Artifact 결과물 | 프로젝트, 작업, 종류, 파일 경로, 버전, 상태(초안/내부승인/고객발송/고객승인) |
| Feedback 검토 의견 | 작업, 내용, 스킬 반영 여부, 반영 스킬, 반영 작업 |
| Activity 활동 | 모든 변경의 시간순 기록 |

레시피(작업 종류)는 DB가 아니라 `config/recipes.json`으로 둔다. 스킬과 함께 git으로 버전 관리하기 위해서다.

## 7. 화면

1. **오늘**: 머리에 확인 필요, 실행 중, 이번 주 일정 건수. 확인 필요 카드(승인, 결과물 검토, 실패, 앱 준비 필요), 미분류 메일, 다가오는 설치/오픈/철수 일정, 실행 중 작업.
2. **메일**: 목록, 본문, AI 패널(요약, 추천 프로젝트, 추출 필드). 프로젝트 연결, 새 프로젝트 만들기(추출 필드로 미리 채움), 답장 쓰기, AI 회신 초안, 작업 맡기기. 첨부는 프로젝트 `00_IN`에 저장.
3. **프로젝트 보드**: 6단계 칸반. 카드에 거래처, 프로젝트명, D-n, 금액, 확인 필요/실행 중 표시. 단계 머리에 건수와 금액 합계.
4. **프로젝트 상세**: 단계 표시와 단계별 추천 작업. 탭: 개요, 메일, 작업, 파일(결과물과 버전), 활동, 지침.
5. **작업 상세**: 실시간 로그(도구 호출은 접힘), 승인 카드, 결과 파일, 레시피/엔진/추정 비용. 중지, 후속 지시, 폴더 열기. 검토 의견과 스킬 반영 체크.
6. **에이전트**: 상태별 실행 목록, 앱 잠금, 동시 실행 수, 월별/레시피별 추정 비용, 엔진과 MCP 서버 상태.
7. **스킬**: 스킬 목록, 학습된 규칙 보기와 직접 수정, 변경 이력과 되돌리기.
8. **설정**: 메일 계정, 작업 폴더, 기본 AI 엔진, 동시 실행 수, 백업.

## 8. 프로젝트 단계와 폴더

단계: 문의 → 기획 → [수주/계약 게이트] → 디자인 → 제작 → 운영 → 리포트 → 완료. 별도 상태로 보류, 실주.

| 단계 | 추천 작업 (레시피 → 결과물) |
|---|---|
| 문의 | 요구사항 정리(md), 회신 초안 |
| 기획 | 제안서(pptx), 견적서(xlsx), 계약서 초안(docx) |
| 디자인 | 3D 모델(스케치업), 키비주얼과 그래픽(일러스트레이터) |
| 제작 | 실행예산과 발주서(xlsx) |
| 운영 | 운영 매뉴얼(docx), 일일 리포트 |
| 리포트 | 결과보고서(pptx), 정산서(xlsx) |

카드를 다음 단계로 옮기면 그 단계의 추천 작업을 제안만 한다. 자동으로 실행하지 않는다. 수주 게이트는 견적서와 제안서가 없으면 경고만 하고 막지 않는다.

```
D:\OFNR\Projects\2026\OFNR-26031_TNF-Seongsu\
  00_IN       받은 자료, 메일 첨부
  01_PLAN     제안서, 견적서, 계약서
  02_DESIGN   3D, KV, 렌더
  03_BUILD    발주, 시공
  04_OPS      운영 매뉴얼, 일일 리포트
  05_REPORT   결과보고서
  06_ADMIN    계약, 정산, 세금계산서
```

- 폴더 이름은 영문(ASCII)으로 만든다. Claude Code는 한글 경로에서 세션 폴더가 섞이고 파일 검색이 실패하는 버그가 있다(이슈 #40946, #84966). 한글 프로젝트명은 DB에 둔다.
- 파일 이름은 `{작업번호}_{문서}_v{NN}.{확장자}` (예: `OFNR-26031_QUOTE_v03.xlsx`). 덮어쓰지 않고 버전을 올린다. 고객 승인 후에만 `_FINAL`을 붙인다.
- 폴더 약칭(`TNF-Seongsu`)은 24자 이내로 제한한다. 데스크톱 Excel은 218자가 넘는 경로를 열지 못한다.
- 결과물 감지는 실행 전후의 파일 크기와 수정 시각만 비교한다. 내용을 읽으면 OneDrive 온디맨드 파일이 내려받아지기 때문이다.

## 9. AI 작업 실행

### 9.1 레시피

레시피 하나가 작업 종류 하나다.

| 필드 | 뜻 |
|---|---|
| id, name, stage | 예: `quote`, 견적서, 기획 |
| skills | 쓸 스킬 (예: `ofnr:quote`) |
| mcpServers | 이 작업에만 붙일 외부 MCP 이름 (예: `excel`) |
| lock | 앱 잠금 (`sketchup`, `illustrator`, `photoshop`, `office`, 없음) |
| engines | 쓸 수 있는 엔진 (`claude`, `codex`) |
| claude | 권한 모드, 허용 도구, 차단 도구 |
| codex | 샌드박스 수준 |
| outputDir, docCode | 결과물 폴더와 문서 코드(QUOTE, PROP 등) |
| maxBudgetUsd | 1회 실행 추정 비용 상한 (Claude) |
| prompt | 프롬프트 틀 |

기본 레시피: 요구사항 정리, 회신 초안, 견적서, 제안서, 문서(계약서, 공문, 운영 매뉴얼), 3D 모델, 그래픽, 결과보고서, 스킬 학습, 자유 지시.

### 9.2 실행 흐름

1. 대표가 작업을 만든다(메일, 프로젝트, 단계 제안에서).
2. 큐가 동시 실행 수(기본 2)와 앱 잠금을 보고 순서를 정한다. 스케치업, 일러스트레이터, 포토샵, Office COM은 각각 한 번에 하나만 돈다.
3. 실행 직전에 필요한 앱 상태를 확인한다(스케치업 MCP 포트 9876 등). 안 되면 "스케치업을 열고 MCP 서버를 켜 주세요" 확인 필요 카드를 띄우고 대기한다.
4. 엔진을 띄운다. 작업 폴더는 프로젝트 폴더다. 프롬프트에는 프로젝트 정보, 대표의 지시, 연결 메일 발췌(신뢰할 수 없는 입력 표시), 결과물 이름 규칙이 들어간다. 프롬프트는 명령줄이 아니라 표준입력으로 넘긴다.
5. 엔진 이벤트를 JobEvent로 저장하고 SignalR로 화면에 흘린다.
6. 엔진이 승인이 필요한 도구를 쓰려 하면 승인 카드를 띄우고 대표의 결정을 기다린다(9.4).
7. 끝나면 새 파일과 바뀐 파일을 찾아 Artifact로 등록하고 작업을 `검토`로 바꾼다. 알림을 띄운다.
8. 대표가 승인하거나, 수정 의견과 함께 후속 지시를 보낸다. 후속 지시는 같은 엔진 세션을 이어서 돈다.

성공 판정은 결과 이벤트의 `is_error`와 프로세스 종료 코드로 한다. 모르는 이벤트 종류는 무시한다.

### 9.3 엔진별 실행

두 엔진 모두 작업마다 전용 실행 폴더(`C:\OFNR\jobs\<작업ID>`, 영문 경로)에서 돈다. 프로젝트 폴더는 쓰기 허용 폴더로 추가한다. 실행 폴더에는 프롬프트, 결과 스키마, 결과 파일, Codex용 스킬 사본이 들어간다. 고객 프로젝트 폴더에는 결과물만 남는다.

| 항목 | Claude Code 2.1.311 | Codex 0.164.0 (ChatGPT) |
|---|---|---|
| 실행 | `claude.exe -p --output-format stream-json --verbose` | `codex.exe exec --json --skip-git-repo-check --ignore-user-config` |
| 폴더 | 작업 디렉터리 = 실행 폴더, `--add-dir <프로젝트 폴더>` | `-C <실행 폴더>`, `-s workspace-write`, `--add-dir <프로젝트 폴더>` |
| 회사 스킬 | `--plugin-dir claude-plugin` | 필요한 스킬 폴더를 `<실행 폴더>\.agents\skills\`로 복사 |
| 추가 지시 | `--append-system-prompt` | `-c developer_instructions=<JSON 문자열>` |
| MCP | 작업별 `--mcp-config` + `--strict-mcp-config` | `-c mcp_servers.<이름>.*` 덮어쓰기, `default_tools_approval_mode="approve"`, `tool_timeout_sec=900`, `required=true` |
| 구조화 출력 | `--json-schema` → 결과 이벤트의 `structured_output` | `--output-schema <파일>` → 마지막 `agent_message` |
| 권한 | `--permission-mode acceptEdits` + `--allowedTools` + `--permission-prompt-tool` | `-c approval_policy="never"` + 샌드박스 |
| 이어서 실행 | `--resume <세션 ID>` | `exec ... resume <스레드 ID> -` (옵션은 resume 앞) |
| 비용 | 결과 이벤트의 추정 비용, `--max-budget-usd` | 토큰 사용량만. 시간 제한으로 대신한다 |
| 실패 판정 | 결과 이벤트 `is_error` 또는 종료 코드 | `turn.failed` 또는 종료 코드. `error` 이벤트는 재시도 알림이라 실패가 아니다. 시작 실패는 표준오류에만 나온다 |

공통 규칙:

- 실행 파일을 직접 띄운다(`.cmd` 경유 금지). 표준입력에 프롬프트를 쓰고 닫는다. 표준입출력은 UTF-8.
- 자식 프로세스 환경에서 `CLAUDECODE`, `CLAUDE_CODE_*` 변수를 지운다.
- 결과 스키마는 OpenAI 엄격 모드 규칙으로 쓴다(모든 필드 required, additionalProperties false, 선택 값은 null 허용). Claude도 이 스키마를 받는다.
- 이어서 실행은 세션 ID로만 한다(`--continue` 금지). 이어갈 수 있게 Codex는 `--ephemeral`을 쓰지 않는다.
- 엔진 버전을 기록하고, 고정한 버전과 다르면 에이전트 화면에 경고를 띄운다.
- Codex는 같은 PC에서 한 번에 하나만 돌린다(로그인 토큰 갱신 충돌 방지).
- 시간 제한(레시피별, 기본 30분)을 넘으면 프로세스 트리째 종료한다.

엔진별 레시피 제한 (1차):

- 스케치업, 일러스트레이터, 포토샵 레시피는 Claude 전용으로 시작한다. Codex의 Windows 샌드박스에서 COM 조종이 되는지는 대표 PC에서 점검 후 연다.
- 워드 문서 레시피는 Claude 전용이다. Anthropic 문서 스킬은 라이선스상 Codex에서 쓸 수 없다.
- 엑셀, 파워포인트 레시피는 두 엔진 모두 쓸 수 있다. 실제 Office를 조종하는 MCP(sbroenne)를 쓰기 때문이다.
- Codex의 헤드리스 MCP 승인 버그(openai/codex#24135)에 대비해, 시작할 때 대시보드 MCP를 부르는 점검 작업을 한 번 돌리고 결과를 에이전트 화면에 표시한다.

### 9.4 대시보드 MCP 서버

엔진과 대시보드가 주고받는 통로다. MCP C# SDK로 서버 안의 `/mcp`에 연다. 작업마다 1회용 토큰을 만들어 그 작업의 프로젝트만 보게 한다.

| 도구 | 역할 |
|---|---|
| `get_project` | 프로젝트 정보와 지침 |
| `get_mail_thread` | 연결된 메일 (읽기 전용, 신뢰할 수 없는 입력 표시) |
| `save_reply_draft` | 회신 초안 저장. 발송은 하지 않는다 |
| `report_progress` | 진행 상황 한 줄 보고 |
| `approval_prompt` | Claude의 권한 확인 요청을 받아 승인 카드로 띄우고 대표의 결정을 돌려준다 |

승인 대기는 메모리에 둔다. 서버가 재시작되면 해당 실행은 `stopped`가 되고 후속 지시로 이어 갈 수 있다.

## 10. 메일 연동

- IMAP(`imap.worksmobile.com:993`, SSL) 수신, SMTP(`smtp.worksmobile.com:465`, SSL) 발송. MailKit을 쓴다.
- 네이버웍스 메일 API는 쓰지 않는다. 서비스 계정 토큰이 막혀 있고, Message-ID 같은 스레드 헤더를 주지 않으며, 답장 필드가 없다.
- 사전 작업: 관리자 화면 `보안 > 서비스 권한 > 기본권한`에서 메일 IMAP/SMTP 허용. 대표 계정 `보안 > 외부 앱 비밀번호`에서 "OFNR 대시보드"용 비밀번호 생성.
- 비밀번호는 DPAPI(현재 사용자 범위)로 암호화해 둔다. 설정 파일, 로그, AI 프롬프트에 넣지 않는다.
- 폴더 이름(보낸메일함, 스팸메일함)은 첫 연결 때 실제 목록을 읽어 매핑한다.
- 답장은 In-Reply-To와 References를 넣어 스레드를 유지한다. 보낸메일함에 자동 저장되지 않으면 IMAP APPEND로 복사본을 넣는다(설정에서 선택).
- 메일 HTML은 HtmlSanitizer로 정제하고, 화면에서는 스크립트를 막은 iframe에 띄운다.
- 분류: 새 메일을 묶어서 AI에 보내 분류(신규 문의, 견적 요청, 시안 피드백, 계약/정산, 협력업체, 공공 입찰, 광고/스팸, 기타), 두 줄 요약, 추출 필드(브랜드, 기간, 장소, 면적, 예산, 마감, 담당자), 추천 프로젝트를 받는다. 분류는 도구 없이 실행한다.
- 자동 매칭 순서: 제목의 작업번호, 거래처 메일 도메인, AI 추천.

## 11. 외부 도구

| 도구 | 선택 | 메모 |
|---|---|---|
| 일러스트레이터 | `illustrator-mcp-server` (ie3jp) 1.10.3 | COM 조종, 패널 설치 없음. 메일에서 시작한 작업에서는 임의 스크립트 도구를 막는다 |
| 포토샵 (선택) | `@alisaitteke/photoshop-mcp` 1.7.37 | `ANALYTICS_DISABLED=1` 필수 |
| 스케치업 | SketchUp Pro 2026.1 + `sketchup-mcp2` 0.4.0 | `uvx sketchup-mcp2==0.4.0`, 서명된 .rbz 설치. 2027은 검증 후 업그레이드 |
| 스케치업 (기획) | Trimble 공식 커넥터 | 빠른 볼륨 스터디만. 기존 파일 편집 불가, 단색만 |
| 엑셀 | `Sbroenne.ExcelMcp.McpServer` 2.5.6 | 실제 Excel COM. 양식 서식과 수식 유지, PDF 내보내기 |
| 파워포인트 | `Sbroenne.PowerPointMcp.McpServer` 0.3.4 | 실제 PowerPoint COM. 마스터와 레이아웃 적용, PDF와 이미지 |
| 워드, PDF | Anthropic 문서 스킬(docx, pdf, xlsx, pptx) | 플러그인으로 설치. 윈도우에서 LibreOffice 변환 스크립트는 깨지므로 PDF는 Office로 만든다 |

- MCP 등록은 `npx` 대신 실행 파일 절대 경로로 한다. Windows의 `cmd /c` 문제와 예고 없는 업그레이드를 피한다.
- MCP 프로세스의 TEMP는 영문 경로(`C:\OFNR\tmp`)로 둔다.
- 버전은 모두 고정하고, 업데이트 후에는 점검(11.1)을 다시 돌린다.
- 한글 폰트(Pretendard 정적 TTF, Noto Sans KR)는 모든 사용자용으로 설치하고, 양식 파일 테마에서 동아시아 글꼴로 지정한다.

### 11.1 첫 설치 점검

- 일러스트레이터: 폰트 목록, CMYK mm 문서 생성, 한글 텍스트, PDF 내보내기, 저장
- 스케치업: 버전 확인, 상자 하나 그리기, 장면 이미지 내보내기
- 엑셀, 파워포인트: 양식 열기, 값 넣기, PDF 내보내기

## 12. 스킬과 학습 구조

대표 요청: 홈페이지에서 확인할 수 없는 내용은 비워 두고, 엑셀 견적서, 제안서, 스케치업 작업을 실제로 하면서 업데이트할 수 있게 만든다.

원칙:

- 확인된 사실만 넣는다. 홈페이지에서 확인한 회사 소개, 4개 서비스의 진행 방식과 산출물, 자주 묻는 질문 답변만 회사 지식(`claude-plugin/knowledge/company.md`)에 넣었다.
- 단가, 견적 양식, 제안서 디자인 규칙, 모델링 규칙처럼 확인되지 않은 내용은 각 스킬의 `학습된 규칙` 칸을 비워 두고 시작한다.
- 실제 양식은 파일로 받는다. `templates` 폴더에 회사 견적서 엑셀, 제안서 PPT, 계약서 워드를 넣으면 스킬이 그 양식을 우선 쓴다. 없으면 기본 구성으로 만들고 결과에 "회사 양식 없음"을 표시한다.

스킬 목록(`ofnr` 플러그인): mail-triage, mail-reply, brief, quote, proposal, document, sketchup, illustrator, report, skill-learning. 모든 스킬은 회사 지식 문서를 참고한다.

흐름:

1. 작업이 끝나면 상태가 `검토`가 된다.
2. 대표가 결과 파일을 열어 보고 수정 의견을 쓴다. 의견마다 `스킬에 반영`을 고를 수 있다.
3. 고른 의견은 `스킬 학습` 작업으로 큐에 들어간다. AI가 해당 스킬의 `학습된 규칙` 칸에 날짜와 출처 프로젝트를 붙여 한 줄 규칙으로 추가한다.
4. 스킬 파일이 바뀌면 자동으로 git 커밋된다. 스킬 화면에서 변경 이력을 보고 되돌릴 수 있다.
5. 대표가 스킬 화면에서 규칙을 직접 고쳐 써도 된다.

같은 스킬 파일을 Claude Code와 Codex가 함께 쓴다. 엔진을 바꿔도 쌓인 규칙이 그대로 간다.

## 13. 보안

- 서버는 `127.0.0.1`에만 연다. 실행할 때마다 만든 토큰을 Tauri가 화면에 넘기고, 모든 API와 SignalR 요청에 그 토큰을 요구한다. Host 헤더도 검사한다. 웹페이지가 로컬 서버를 몰래 부르는 공격(CSRF, DNS 리바인딩)을 막는다.
- 메일 본문과 첨부는 신뢰할 수 없는 입력이다. 프롬프트에서 따로 표시하고, 메일 분류는 도구 없이 돌린다.
- AI에게 메일 발송 도구를 주지 않는다. 발송은 대표가 버튼을 눌러야 일어난다.
- `bypassPermissions`, `--dangerously-skip-permissions`, Codex `danger-full-access`는 쓰지 않는다.
- 엔진은 프로젝트 폴더의 `.mcp.json`을 읽지 않는다(`--strict-mcp-config`).
- 메일 비밀번호는 DPAPI로 암호화한다.
- 대시보드 MCP의 작업 토큰은 작업이 끝나면 폐기한다.
- 직원이 대표의 Claude/ChatGPT 로그인을 같이 쓰지 않는다(약관상 계정 공유 금지). 2차에는 직원별 좌석이나 API 키를 쓴다.

## 14. 설치, 업데이트, 백업, 유지보수

- 설치: Tauri 설치 파일(NSIS) 하나. 첫 실행 점검 화면이 PostgreSQL, Claude Code, Codex, Git for Windows, MCP 서버 설치 상태를 확인하고 빠진 것을 안내한다. PostgreSQL은 17을 winget으로 무인 설치하고(2차 공유 서버와 같은 메이저 버전), DB는 한글 검색이 되도록 ICU 로캘로 만든다. 설치 스크립트(`scripts/setup.ps1`)가 이 과정을 한다.
- 업데이트: Tauri Updater가 GitHub Releases(설치 파일만 올리는 공개 저장소)에서 새 버전을 받는다. 빌드는 GitHub Actions의 Windows 러너가 한다. 실행 중인 작업이 없을 때만 업데이트하고, 설치 직전에 서버를 끈다.
- 코드 서명: 한국 법인은 Azure 서명 서비스를 쓸 수 없다. 1차는 서명 없이 대표 PC에 설치하고(처음 한 번 SmartScreen에서 "추가 정보 > 실행"), 직원 배포 전에 OV 코드 서명 인증서를 산다.
- DB 마이그레이션: 서버 시작 시 자동 적용. 적용할 마이그레이션이 있으면 그 직전에 자동 백업한다.
- 백업: 매일 `pg_dump`로 DB를 백업 폴더에 남긴다(14일 보관). 프로젝트 폴더는 OneDrive나 NAS 동기화에 맡긴다.
- 유지보수: 저장소의 `CLAUDE.md`에 구조와 규칙을 적어 둔다. 대표가 Claude Code로 저장소를 열면 바로 고칠 수 있다.
- 엔진 CLI는 자동 업데이트된다. 대시보드는 첫 이벤트의 엔진 버전을 기록하고, 이벤트 해석 실패가 나면 에이전트 화면에 경고를 띄운다.

## 15. 2차 확장 경로 (직원 공유)

- PostgreSQL을 사무실 서버로 옮긴다(백업 복원).
- 서버를 사무실 PC에서 상시 실행하고, 각 직원의 Tauri 앱이 그 주소를 바라본다.
- 로그인과 권한(ASP.NET Core Identity)을 켠다. User 테이블은 1차부터 있다.
- 일러스트레이터, 스케치업, Office를 조종하는 작업은 그 앱이 깔린 PC의 로그인 세션에서 돌아야 한다. 작업 실행부를 디자인 PC마다 띄워 서버에서 작업을 받아 가게 한다. 1차에서 실행부를 별도 모듈로 둔 이유다.
- AI 엔진 로그인은 직원별 좌석이나 API 키로 바꾼다.

## 16. 테스트

- 서버: xUnit. PostgreSQL 통합 테스트, 엔진 이벤트 해석(녹화한 Claude stream-json과 Codex JSON), 메일 파싱과 스레드, 폴더와 파일 이름 규칙, 큐와 잠금, 보안 토큰과 Host 검사.
- 엔진 연동: 실제 CLI로 짧은 작업 1회(선택 실행).
- 화면: 타입 검사, 빌드, Playwright로 주요 화면 확인(밝은/어두운 테마).

## 17. 대표 확인 필요

- 네이버웍스 관리자에서 IMAP/SMTP 허용, 외부 앱 비밀번호 발급
- 회사 견적서, 제안서, 계약서 양식 파일(`templates` 폴더)
- 프로젝트 폴더 위치
- SketchUp Pro 버전, Adobe 앱 버전, Office 버전
- Codex에 쓸 ChatGPT 요금제
