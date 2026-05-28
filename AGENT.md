# StarcraftMapUnprotector — Agent Guide

## Project Overview

StarCraft 맵 파일(.scx/.scm)의 보호 구조를 해제하고, scenario.chk를 복구하여 ScmDraft 2에서 편집 가능한 형태로 재패키징하는 C# CLI 도구.

## Architecture

단일 `partial class StarcraftMapUnprotector`를 여러 .cs 파일로 분리한 구조. 외부 라이브러리는 `TkMPQLib`(32비트 네이티브 DLL)만 사용.

### Core Pipeline

```
Input (.scx/.scm/.chk)
  -> MpqExtractor: MPQ 아카이브에서 scenario.chk 추출
  -> ChkParser: CHK 바이너리를 섹션 단위로 파싱
  -> ChkNormalizer: 보호용 섹션 제거, 문자열/트리거/지형 정규화
  -> TerrainRepairer: MTXM/TILE/ISOM/MASK 복구
  -> Program: 정규화된 CHK를 새 MPQ로 패키징
Output (.unprotected.scx)
```

### File Responsibilities

| File | Role |
|------|------|
| `Program.cs` | 엔트리포인트, CLI 파싱, 배치 모드, 통계 출력, MPQ 쓰기 |
| `MpqExtractor.cs` | MPQ에서 scenario.chk 추출, 해시/블록 테이블 복구, deep recovery |
| `ChkParser.cs` | CHK 바이너리 -> Section 리스트 파싱, CHK 판별 |
| `ChkNormalizer.cs` | 문자열 테이블 재구성, 참조 remap, SMLP/가짜 섹션 제거, 트리거 정규화 |
| `FreezeDecryptor.cs` | Freeze05 복호화, 런타임 덤프 적용, EUD 보호 트리거 비활성화 |
| `FreezeKeyRecovery.cs` | Freeze05 키/시드 복구 보조 로직 |
| `FreezeStaticRestorer.cs` | Freeze05 정적 복원 로직 |
| `TerrainRepairer.cs` | 지형 섹션(MTXM/TILE/ISOM/MASK) 후보 선택 및 복구 |
| `ChkTriggerDiag.cs` | 트리거 진단/덤프 유틸리티 (standalone) |
| `UnitNameProbe.cs` | 유닛 이름 문자열 탐색 유틸리티 (standalone) |

### Key Data Structures

- `Section` — CHK 섹션 하나 (Name: 4-char string, Data: byte[])
- `Stats` — 처리 과정의 모든 통계를 누적하는 객체
- `MpqFileEntry` — MPQ 내 추가 파일 (scenario.chk 외)
- `MpqHeaderCandidate` — deep recovery 시 MPQ 헤더 후보

### CHK Section Processing Order

`CanonicalOrder` 배열 순서로 섹션을 정렬하여 출력:

VER -> TYPE -> IVE2 -> VCOD -> IOWN -> OWNR -> SIDE -> COLR -> ERA -> DIM -> MTXM -> TILE -> ISOM -> UNIT -> ... -> SWNM

### Protection Types Handled

1. **SMLP 보호** — 가짜 SMLP 섹션 삽입 -> 제거
2. **중복 섹션 보호** — 동일 이름 섹션 중복 삽입 -> 마지막 것만 유지
3. **가짜 UNIT/TRIG 레코드** — 유효하지 않은 레코드 삽입 -> 필터링
4. **MPQ 해시/블록 테이블 변조** — 인덱스 패치, 테이블 복구, deep recovery
5. **Freeze05 보호** — EUD 트리거 기반 보호 -> 감지 및 제거
6. **지형 데이터 손상** — MTXM/TILE/ISOM 후보 중 최적 선택

## Docs Structure

문서는 코드 변경의 설계 기준이다. 새 기능이나 동작 변경은 아래 문서 중 영향을 받는 파일을 먼저 갱신한다.

| Path | Purpose |
|------|---------|
| `Docs/Architecture/pipeline.md` | 전체 언프로텍트 파이프라인, 단계별 흐름 |
| `Docs/Architecture/file-map.md` | 소스 파일별 책임, 도구/의존성 구조 |
| `Docs/Formats/chk-sections.md` | CHK 섹션 구조와 정규화 규칙 |
| `Docs/Formats/mpq-protection.md` | MPQ 보호/손상 패턴과 복구 전략 |
| `Docs/Formats/freeze05-protection.md` | Freeze05 색인 및 세부 문서 진입점 |
| `Docs/Dev/dev-notes.md` | 빌드, 테스트, 디버깅, 개발 워크플로 |
| `Docs/Text Trigedit *.txt` | Text Trigedit 관련 원문 참고 자료 |

## Build

32비트(x86) 빌드 필수 — TkMPQLib.dll이 32비트 전용.

```powershell
powershell -ExecutionPolicy Bypass -File .\Build-Release.ps1 -Version <version>
```

## CLI Usage

```
StarcraftMapUnprotector.exe [input] [output] [--no-pause] [--raw-chk] [--apply-dump <file>]
```

- 인수 없이 실행: `Maps\Originals` -> `Maps\Outputs` 배치 처리
- `--raw-chk`: CHK 정규화 건너뜀
- `--no-pause`: 완료 후 즉시 종료
- `--apply-dump <file>`: CE 런타임 덤프 적용 (Freeze05 암호화 트리거 복호화)

## Workflow: Docs First

모든 변경은 **문서를 먼저 수정하고, 그 다음에 코드를 수정**한다.

### 순서

1. **Docs 업데이트** — 변경할 내용의 설계, 동작 방식, 영향 범위를 먼저 문서에 반영
2. **코드 수정** — 문서에 기술한 대로 구현
3. **문서 검증** — 구현 결과와 문서가 일치하는지 확인, 필요 시 문서 보정

### 어떤 문서를 수정하는가

| 변경 유형 | 수정할 문서 |
|-----------|-------------|
| 파이프라인 흐름 변경 | `Docs/Architecture/pipeline.md` |
| 파일 추가/삭제/역할 변경 | `Docs/Architecture/file-map.md` |
| CHK 섹션 처리 변경 | `Docs/Formats/chk-sections.md` |
| 보호 기법 대응 추가/변경 | `Docs/Formats/mpq-protection.md`, `Docs/Formats/freeze05-protection.md`, `Docs/Formats/Freeze05/*` |
| 빌드/테스트/디버깅 방법 변경 | `Docs/Dev/dev-notes.md` |
| CLI 옵션 추가/변경 | 이 파일(`AGENT.md`)의 CLI Usage 섹션과 `Docs/Dev/dev-notes.md` |
| 아키텍처/구조 변경 | 이 파일(`AGENT.md`)의 Architecture 섹션과 `Docs/Architecture/*` |

### 왜 Docs First인가

- 코드를 작성하기 전에 설계를 명확히 정리할 수 있다
- 변경의 영향 범위를 문서 단계에서 먼저 파악한다
- 문서와 코드의 괴리를 방지한다
- 리뷰어(또는 미래의 자신)가 변경 의도를 빠르게 이해할 수 있다

### 예외

- 단순 버그 수정 (문서에 영향이 없는 경우)은 코드 먼저 수정해도 됨
- 긴급 핫픽스는 코드 먼저 수정 후 문서를 따라잡는다

## Conventions

- 모든 코드가 `StarcraftMapUnprotector` partial class 안에 있음 (standalone 유틸 제외)
- 인코딩: CP949(한국어) + UTF-8 dual 지원
- CHK 섹션 데이터는 항상 byte[] 로 다룸
- 외부 의존성 최소화 (TkMPQLib만)
- README와 콘솔 출력은 한국어
