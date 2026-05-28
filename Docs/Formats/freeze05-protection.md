# Freeze05 Protection

Freeze05 보호 방식의 대표 진입점이다. 상세 역공학 기록은 `Freeze05/` 전용 폴더로 분리했고, 이 파일은 현재 상태와 읽는 순서를 유지한다.

원본 소스: [phu54321/euddraft/freeze/](https://github.com/phu54321/euddraft/tree/master/freeze)
실제 배포: [armoha/euddraft](https://github.com/armoha/euddraft) — freezeMpq.pyd (컴파일된 C++ 확장)

## 현재 상태

| 레벨 | 의미 | 상태 | 비고 |
|------|------|------|------|
| Lv0 | 에디터에서 열기만 가능 | 가능 | ScmDraft로 열기 |
| Lv1 | 보호 트리거 제거, 편집 가능 | 구현 완료 | EUD 비활성화 + 트리거 복호화 |
| Lv2 | Lv1 + 게임 정상 실행 | 진행 중 | trigger body 복호화 완료, keycalc/MPQ 구조 의존성 검증 중 |
| Lv3 | 완전 복원 | 미착수 | Freeze 보호 흔적 완전 제거 |

Lv2의 현재 접근은 EUD VM을 살려두고 trigger body만 미리 복호화한 뒤 flag에서 `0x80000000` 비트를 제거하여 런타임 `decryptTrigger` 이중 복호화를 스킵시키는 방식이다. 단, MPQ 구조가 바뀌면 keycalc 입력이 달라져 런타임 cryptKey/offset 계산이 깨질 수 있으므로 원본 MPQ 구조 보존이 핵심 리스크다.

## 읽는 순서

1. [개요와 보호 레벨](Freeze05/overview.md)
2. [Lv2 전략 전환](Freeze05/lv2-strategy.md)
3. [키와 파일 내 구조](Freeze05/keys-and-file-structure.md)
4. [트리거 암호화 상세](Freeze05/trigger-crypto.md)
5. [EUD 보호 트리거 식별](Freeze05/eud-triggers.md)
6. [freezeMpq.pyd 분석](Freeze05/freeze-mpq.md)
7. [구현 노트](Freeze05/implementation.md)
8. [런타임 덤프 레거시 경로](Freeze05/runtime-dump-legacy.md)
9. [armoha freeze 모듈 추출 기록](Freeze05/armoha-extraction.md)
10. [연구 로그와 다음 단계](Freeze05/research-log.md)

## 세부 문서

| 문서 | 내용 |
|------|------|
| [overview.md](Freeze05/overview.md) | Freeze05 개요, 빌드/런타임 동작, 보호 레벨 |
| [lv2-strategy.md](Freeze05/lv2-strategy.md) | Lv2 전략 전환, VM 보존, flag 무력화, MPQ 보존 리스크 |
| [keys-and-file-structure.md](Freeze05/keys-and-file-structure.md) | Freeze 마커, 키 계보, triggerKeyVal, 파일 단독 key 복구 |
| [trigger-crypto.md](Freeze05/trigger-crypto.md) | T2/Mix2, ComputeCryptKeyVal, encrypt/decryptTrigger, wlist, flag 구조 |
| [eud-triggers.md](Freeze05/eud-triggers.md) | EUD 보호 트리거 식별, Lv1 비활성화, obfpatch 상태 |
| [freeze-mpq.md](Freeze05/freeze-mpq.md) | freezeMpq.pyd 역할, keycalc, 내부 상수/함수 맵 |
| [implementation.md](Freeze05/implementation.md) | 관련 C# 구현 파일, Lv2 처리 흐름, BuildNormalizedChk 순서 |
| [runtime-dump-legacy.md](Freeze05/runtime-dump-legacy.md) | CE 런타임 덤프, `--apply-dump`, 레거시 진단 파이프라인 |
| [armoha-extraction.md](Freeze05/armoha-extraction.md) | armoha freeze 모듈 추출 경로, 패키지 구조, unFreeze 흐름 |
| [research-log.md](Freeze05/research-log.md) | 다음 단계, 테스트맵 결과, flag 마스크 분석, exec_flags 관찰 |

## 유지보수 규칙

- 새 발견이나 실험 결과는 먼저 [research-log.md](Freeze05/research-log.md)에 기록한다.
- 구현 흐름이나 파일 책임이 바뀌면 [implementation.md](Freeze05/implementation.md)와 이 색인을 함께 갱신한다.
- 런타임 덤프 기반 절차는 현재 Lv2 본선 전략이 아니므로 [runtime-dump-legacy.md](Freeze05/runtime-dump-legacy.md)에만 보관한다.
