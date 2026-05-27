# Development Notes

## 빌드 요구사항

- .NET Framework (TkMPQLib 호환)
- **x86 빌드 필수** — TkMPQLib.dll이 32비트 전용
- Build-Release.ps1 사용 권장

```powershell
powershell -ExecutionPolicy Bypass -File .\Build-Release.ps1 -Version 1.2.0
```

## 테스트 방법

자동 테스트 스위트 없음. 수동 테스트:

1. `Maps\Originals`에 보호된 맵 배치
2. 프로그램 실행
3. `Maps\Outputs`의 결과를 ScmDraft 2에서 열어 확인
4. 콘솔 통계 출력에서 비정상 수치 확인

## CLI 옵션

```
StarcraftMapUnprotector.exe <input.scx> [output.scx] [options]
StarcraftMapUnprotector.exe                           # 배치 모드 (Maps/Originals → Maps/Outputs)
```

| 옵션 | 설명 |
|------|------|
| `--no-pause` | 완료 후 즉시 종료 |
| `--raw-chk` | 정규화 없이 CHK 그대로 리패키징 |
| `--apply-dump <file>` | CE 런타임 덤프 적용 (Freeze05 암호화 트리거 복호화) |

## 디버깅 팁

- `--raw-chk`: 정규화 없이 추출만 수행 — MPQ 추출 문제 격리용
- `--apply-dump`: Freeze 보호 맵의 암호화 트리거를 CE 메모리 덤프로 교체
- `ChkTriggerDiag`: 트리거 구조를 파일로 덤프 — 트리거 관련 문제 분석
- `DumpChkSections()`: 최종 CHK의 섹션 목록과 크기를 콘솔에 출력

## Freeze05 복호화 워크플로

brute-force가 실패하는 맵 (armoha 빌드)은 런타임 메모리 덤프로 복호화:

```
1. StarCraft 1.16.1에서 맵 실행 (리마스터 EUD 에뮬레이션은 제약 있음)
2. Cheat Engine Attach → Tools/ce/freeze_dump.lua 실행
3. 인게임 진입 후 1~2초 대기 → F10
4. freeze_dump.bin 생성 (N × 2400 bytes)
5. StarcraftMapUnprotector.exe map.scx out.scx --apply-dump freeze_dump.bin
```

## Partial Class 구조

`StarcraftMapUnprotector`는 `internal static partial class`로, 각 .cs 파일이 기능 단위로 분리:

- `Program.cs` — Main, CLI, 배치, 통계, MPQ 쓰기
- `MpqExtractor.cs` — MPQ 추출/복구
- `ChkParser.cs` — CHK 파싱, Freeze 파이프라인 호출
- `ChkNormalizer.cs` — 문자열/트리거/섹션 정규화
- `FreezeDecryptor.cs` — Freeze05 복호화, 런타임 덤프 적용, EUD 비활성화
- `TerrainRepairer.cs` — 지형 복구

새 기능 추가 시 관련 파일에 메서드 추가하거나, 새 .cs 파일로 partial class 확장.

## 인코딩 처리

StarCraft 한국어 맵은 CP949 인코딩 사용. 일부 맵은 UTF-8. `ChkNormalizer`에서 두 인코딩을 모두 시도하여 문자열을 읽음.

```csharp
private static readonly Encoding KoreanEncoding = Encoding.GetEncoding(949);
private static readonly Encoding StrictUtf8Encoding = new UTF8Encoding(false, true);
```
