# Freeze05 Runtime Dump Legacy Path

## Freeze 덤프 CSV 포맷


`DumpFreezeEudTriggers()`가 생성:


| 컬럼 | 설명 |

|------|------|

| trigger_index | TRIG 섹션 내 트리거 인덱스 (0-based) |

| action_index | 트리거 내 액션 인덱스 (0-63) |

| epd_player | SetDeaths 대상 player 번호 (>27이면 EUD) |

| value | SetDeaths 값 |

| modifier | 7=SetTo, 8=Add, 9=Subtract |

| unit | SetDeaths 대상 유닛 타입 |

| is_eud_trigger | Freeze EUD 트리거 여부 (1/0) |


### EPD 주소 계산


```

target_epd = player + unit × 12

메모리 주소 = 0x58A364 + target_epd × 4

```


주의: `player == 13`은 CurrentPlayer 동적 타깃이다. Freeze VM은

`0x6509B0`(CurrentPlayer 레지스터)을 SetDeaths로 직접 수정한 뒤

`SetDeaths(CurrentPlayer, ...)`를 사용하므로, 이 경우 target_epd는

런타임의 CurrentPlayer 값으로 해석해야 한다.


예: `player=203155, unit=0`이면 `target_epd=203155`,

`0x58A364 + 203155 * 4 = 0x6509B0`이다.


## 런타임 메모리 덤프 (레거시 진단 경로)


초기 연구 단계에서 사용했던 진단 경로다. 현재는 `triggerKey` 직접 탐색이 구현되어

있으므로 키 복구용 폴백으로 취급하지 않는다. 특히 Lv2/playable 복원에는 부적합하다.


이유:


- Freeze VM은 매 프레임 offset을 복호화한 뒤 트리거 실행 후 다시 암호화한다.

- 덤프 시점이 `decryptOffsets()`와 `encryptOffsets()` 사이임을 보장하기 어렵다.

- 리마스터 EUD 에뮬레이션은 실제 구조체가 아닌 가상 1.16.1 메모리에 쓰기를 리다이렉트한다.

- body 일부가 정상처럼 보여도 nextptr/offset 상태는 불완전할 수 있다.

- 따라서 덤프 기반 패치는 편집용 일부 확인에는 쓸 수 있어도, 단일 파일 playable Lv2 복원에는 쓰지 않는다.


### 트리거 메모리 레이아웃 (런타임)


```

CHK 파일:  [trigger body: 2400 bytes] × N개 (연속 배열)

메모리:    [prev: 4][next: 4][trigger body: 2400 bytes] × N개

           = 2408 bytes per trigger

```


| 항목 | 값 |

|------|-----|

| 트리거 베이스 주소 | `0x51CA08` (일반적, 버전별 차이 가능) |

| 트리거 크기 (메모리) | 2408 bytes (8-byte linked list header + 2400 body) |

| n번째 트리거 body 시작 | `base + n * 2408 + 8` |

| EPD 변환 | `triggerEPD += 2` = 8바이트 헤더 스킵 (dword 단위) |

| Deaths 테이블 베이스 | `0x58A364` (EPD=0 기준점) |


### 메모리 덤프 절차


1. **게임 실행**: 테스트 맵을 싱글플레이로 시작

2. **타이밍**: Freeze EUD 트리거는 매 프레임 실행 → 첫 프레임 후 복호화 완료

3. **덤프**: Cheat Engine 등으로 트리거 메모리 영역 전체 덤프

4. **추출**: 각 트리거에서 8바이트 헤더 제거, 2400바이트 body만 수집

5. **검증**: 암호화됐던 트리거(flag에 0x80000000 비트)의 body가 정상인지 확인

6. **적용**: 복호화된 body를 CHK TRIG 섹션에 기록


### 키 역산 (옵션)


암호화 전/후 데이터를 모두 확보하면 키를 역산할 수 있다:


```

encrypted_dword + adddw = decrypted_dword

→ adddw = decrypted_dword - encrypted_dword

→ adddw = mix2(w, i) 에서 w 역산

→ wlist에서 key 역산

```


키를 역산하면 향후 같은 알고리즘의 다른 맵도 자동 복호화 가능.


### 주요 참고 스크립트


| 파일 | 역할 |

|------|------|

| `Tools/scripts/analyze_trig_base2.py` | 트리거 베이스 주소 분석, stride 2408 검증 |

| `Tools/scripts/analyze_freeze.py` | EUD 트리거 상수 전파 시뮬레이션 |

| `Tools/scripts/extract_trig_pattern.py` | CHK TRIG에서 CE AOBScan 패턴 추출 |

| `Tools/ce/freeze_dump.lua` | CE 메모리 덤프 스크립트 v3 (1.16.1용) |

| `Tools/ce/freeze_dump_v4.lua` | CE 메모리 덤프 스크립트 v4 (리마스터 에뮬레이트 메모리 탐색) |

| `ChkTriggerDiag.cs` | 트리거 분류 (Freeze-EUD / 게임플레이 / 일반) |


## 런타임 덤프 레거시 경로


### CE 메모리 덤프 + 도구 통합 파이프라인


Freeze 암호화 트리거를 복호화하기 위한 **런타임 메모리 덤프 → CHK 패치**

파이프라인을 구현했었다. 현재는 Lv2 복원 경로에서 사용하지 않는다.

Freeze가 매 프레임 암호화/복호화를 반복하기 때문에 완전한 상태를 안정적으로

캡처하지 못하며, `triggerKey`도 파일 단독 탐색으로 이미 복구 가능하다.


#### 전체 흐름


```

1. 게임 실행 (StarCraft 1.16.1 권장)

2. Cheat Engine Attach → freeze_dump.lua 실행

3. 인게임에서 F10 → freeze_dump.bin 생성 (N × 2400 bytes)

4. StarcraftMapUnprotector.exe map.scx --apply-dump freeze_dump.bin

   → 암호화 트리거를 덤프 데이터로 교체, EUD 트리거 비활성화

5. 결과: 편집 가능한 .unprotected.scx

```


#### --apply-dump 처리 (ApplyRuntimeDump)


```

입력: CHK TRIG 데이터 + CE 덤프 파일

│

├─ 각 트리거를 인덱스 기준 1:1 매칭

├─ CHK에서 flag ≥ 0x80000000인 트리거만 패치 대상

├─ 덤프 body 유효성 검증 (condition type ≤ 23, action type ≤ 63)

├─ 유효한 경우: 덤프 body → CHK body 복사 (2400 bytes)

├─ exec_flags 복원: (flag - 0x80000000) & 0x0F

└─ 무효한 경우: 경고 출력 후 스킵

```


#### CE 스크립트 (freeze_dump.lua)


트리거 배열 탐색 전략:


| 버전 | 전략 | 대상 |

|------|------|------|

| v1 | 하드코딩 `0x51CA08` | 1.16.1 전용 |

| v2 | CHK "TRIG" 섹션 헤더 AOBScan | 실패 (리마스터가 헤더 제거) |

| v3 | condition 바이트 패턴 AOBScan + stride 자동감지 | 1.16.1 / 리마스터 32비트 |

| v4 | v3 + 모든 후보 수집 + 복호화 여부 판별 | 리마스터 에뮬레이트 메모리 탐색용 |


v3/v4 패턴 예시 (trigger 8 condition[0]):

```

Deaths(player=13, qty=1, unit=0, locationNum=0x80000000)

→ AOB: 00 00 00 80 0D 00 00 00 01 00 00 00 00 00 00 0F

```


stride 자동감지: trigger 8과 trigger 9의 패턴 주소 차이 = stride


### 과거 테스트 결과


| 맵 | 결과 |

|-----|------|

| `!! 저글링_키우기_ver25.scx` | trigger 72 body 일부 복호화 확인. Lv2/playable 복원 근거로는 불충분 |


### 리마스터 제약사항


리마스터(32비트 포함)의 EUD 에뮬레이션은 SetDeaths 쓰기를 가상 1.16.1 메모리로 리다이렉트한다.

실제 트리거 구조체(`0x24030E70` 등)는 수정되지 않으므로, 리마스터에서 직접 복호화된 트리거를

덤프하려면 에뮬레이트된 가상 메모리를 찾아야 한다 (v4 스크립트가 이를 시도).


**결론**: CE 덤프는 연구/비교용으로만 남긴다. production Lv2는 정적 keycalc와

정적 offset 복원을 구현해야 한다.


### 연구 스크립트 (Tools/scripts/)


| 파일 | 역할 |

|------|------|

| `inspect_enc_trigger.py` | 암호화/덤프 비교 — 뺄셈 방향 증명 |

| `disasm_freeze_main.py` | pyd 메인 함수(0x6ACD~0x709D) 디스어셈블리 |

| `disasm_trigger_crypt.py` | 0x10F0 함수(mix2 루프) 디스어셈블리 |

| `disasm_encrypt_func.py` | 0x2590 MPQ EncryptMpqBlock 식별 |

| `disasm_wlist_gen.py` | 0x10F0 호출처 탐색 (0x1F35, 0x6DCB) |

| `disasm_pyinit.py` / `disasm_pyinit2.py` | PyInit_freezeMpq 엔트리 포인트 분석 |
