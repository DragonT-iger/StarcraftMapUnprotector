# File Map

## 핵심 소스

| File | Lines | Description |
|------|-------|-------------|
| `Program.cs` | ~420 | 엔트리포인트, CLI (`--apply-dump` 등), 배치 모드, 통계, MPQ 쓰기, Freeze blob |
| `MpqExtractor.cs` | - | MPQ 추출, 해시/블록 테이블 복구, deep recovery |
| `ChkParser.cs` | - | CHK 바이너리 파싱, 섹션명 유효성 검증, Freeze 파이프라인 호출 |
| `ChkNormalizer.cs` | - | 문자열 테이블 재구성 (STR/STRx), 참조 remap, 섹션 정규화, 트리거 정리 |
| `FreezeDecryptor.cs` | ~415 | Freeze05 복호화: brute-force, 런타임 덤프 적용, EUD 트리거 비활성화 |
| `TerrainRepairer.cs` | - | MTXM/TILE/ISOM/MASK 후보 선택 및 복구 |

## 진단/유틸리티 (standalone)

| File | Description |
|------|-------------|
| `ChkTriggerDiag.cs` | 트리거 구조 덤프 및 진단 (별도 Main) |
| `UnitNameProbe.cs` | UNIx 섹션에서 커스텀 유닛 이름 추출 (별도 Main) |

## 빌드/배포

| File | Description |
|------|-------------|
| `Build-Release.ps1` | 릴리스 빌드 스크립트 (x86, single-file) |
| `Unprotect-All.ps1` | 배치 언프로텍트 PowerShell 래퍼 |

## Tools/ (gitignored — 분석/진단용)

| Directory | Contents |
|-----------|----------|
| `Tools/scripts/` | Python 분석 스크립트 (`analyze_*.py`, `dump_*.py`, `parse_mpq.py`, `extract_trig_pattern.py`) |
| `Tools/diag/` | C# 진단 도구 (`ChkAnalyzer*.cs`, `ChkDump*.cs`, `SectionDump.cs`, `DumpStrings.cs`, `DumpTrig.cs`, `ExtractChk.cs`) |
| `Tools/ce/` | Cheat Engine Lua 스크립트 (`freeze_dump.lua`, `freeze_dump_v4.lua`) |

## 외부 의존성

| Name | Type | Note |
|------|------|------|
| `TkMPQLib.dll` | 네이티브 DLL (32비트) | MPQ 읽기/쓰기. x86 빌드 필수 |
