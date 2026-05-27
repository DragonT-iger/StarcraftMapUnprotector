# CHK Section Reference

StarCraft scenario.chk 파일은 4바이트 이름 + 4바이트 크기 + 데이터로 구성된 섹션의 연속.

## 정규화 순서 (CanonicalOrder)

이 도구가 출력하는 섹션 순서:

```
VER  TYPE IVE2 VCOD IOWN OWNR SIDE COLR
ERA  DIM  MTXM TILE ISOM UNIT PUNI UNIx
PUPx UPGx DD2  THG2 MASK MRGN STR  STRx
SPRP FORC WAV  PTEx TECx MBRF TRIG UPRP
UPUS SWNM
```

## 주요 섹션

| Section | Size | Description |
|---------|------|-------------|
| `VER ` | 2 | 맵 버전 |
| `ERA ` | 2 | 틸셋 (0=Badlands ~ 7=Twilight) |
| `DIM ` | 4 | 맵 크기 (width, height in tiles) |
| `MTXM` | W*H*2 | 실제 표시되는 지형 타일 |
| `TILE` | W*H*2 | 에디터용 지형 타일 |
| `ISOM` | ((W/2)+1)*(H+1)*8 | 아이소메트릭 지형 데이터 |
| `MASK` | W*H | 안개(fog) 마스크 |
| `UNIT` | N*36 | 유닛/건물 배치 (레코드당 36바이트) |
| `TRIG` | N*2400 | 트리거 (레코드당 2400바이트) |
| `STR ` | variable | 문자열 테이블 (SC 1.0) |
| `STRx` | variable | 문자열 테이블 (BW 확장) |
| `MRGN` | N*20 | 위치(Location) 정의 |
| `UNIx` | variable | BW 유닛 설정 |
| `SPRP` | 4 | 시나리오 속성 (맵 이름/설명 문자열 ID) |

## 보호용 섹션

| Section | Description |
|---------|-------------|
| `SMLP` | 보호 마커 — 무조건 제거 |

## 문자열 테이블 구조 (STR)

```
Offset 0: ushort numStrings
Offset 2: ushort[numStrings] offsets  (각 문자열의 시작 오프셋)
Offset 2+numStrings*2: 실제 문자열 데이터 (null-terminated)
```

인코딩: CP949 또는 UTF-8 (이 도구는 둘 다 시도)
