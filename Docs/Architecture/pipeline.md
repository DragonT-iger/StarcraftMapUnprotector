# Unprotect Pipeline

## 전체 흐름

```
입력 파일 (.scx / .scm / .chk)
│
├─ LooksLikeChk? ─── yes ──→ 바로 CHK로 처리
│                     no
│
▼
MpqExtractor.ExtractScenarioChk()
│
├─ TkMPQ 정상 오픈 시도
│   ├─ RecoverShiftedProtectedTables  (해시/블록 테이블 오프셋 복구)
│   ├─ PatchProtectedHashIndexes      (해시 인덱스 보정)
│   ├─ ExtractExtraFiles              (scenario.chk 외 파일 추출)
│   └─ TryReadScenarioChkFromMpq      (staredit\scenario.chk 읽기)
│
├─ TkMPQ 실패 시 → Deep Recovery
│   └─ TryRecoverScenarioChkAggressively
│       (파일 바이트를 직접 스캔하여 MPQ 헤더 후보 탐색)
│
▼
ChkParser.ParseChk()
│  CHK 바이너리 → List<Section> 파싱
│  (SMLP 등 비표준 섹션명은 건너뜀)
│
▼  [--raw-chk 플래그 시 여기서 바로 출력]
│
ChkNormalizer.BuildNormalizedChk()
│
├─ Freeze05 처리 (ProcessFreezeProtection)
│   ├─ --apply-dump 지정 시: 런타임 덤프로 암호화 트리거 패치
│   ├─ seedKey 존재 시: brute-force 복호화 시도
│   └─ EUD 보호 트리거 in-place 비활성화 (플레이어 바이트 클리어)
├─ SMLP 섹션 제거
├─ 중복 섹션 → 마지막 것만 유지
├─ 분할된 섹션 병합
├─ 가짜 UNIT 레코드 필터링
├─ 가짜 TRIG 레코드 필터링
├─ 트리거 주석 제거
├─ 트리거 문자열/위치 참조 정규화
├─ 문자열 테이블 재구성 (STR/STRx)
├─ 위치(MRGN) 복구
├─ TerrainRepairer: MTXM/TILE/ISOM/MASK 복구
├─ 누락 기본 섹션 추가
└─ CanonicalOrder로 섹션 정렬
│
▼
WriteStandardMpq()
│  정규화된 CHK + 추가 파일 + Freeze blob → 새 MPQ 패키징
│
▼
출력 파일 (.unprotected.scx)
```

## MPQ Deep Recovery

보호된 MPQ가 TkMPQ에서 크래시를 일으키는 경우의 최후 수단.

1. 파일 바이트를 스캔하여 `MPQ\x1A` 시그니처 탐색
2. 각 후보의 해시/블록 테이블 오프셋 계산
3. 후보별로 TkMPQ 재오픈 시도
4. scenario.chk 추출 성공 시 해당 후보 채택

## Terrain Repair Strategy

MTXM/TILE이 여러 후보가 있을 때:
- 중복 섹션 중 바이트 수가 맞는 것 필터링
- "정보량"이 높은 것을 우선 선택 (low-information grid 배제)
- MTXM과 TILE 간 매칭률 계산
- ISOM은 후보 선택 또는 ERA 기반 생성
