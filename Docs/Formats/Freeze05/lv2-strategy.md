# Freeze05 Lv2 Strategy

## Lv2 전략 전환: 정적 복원 → EUD VM 보존 (2026-05)


### 폐기된 접근: 정적 복원


정적 복원은 Freeze 런타임 VM이 매 프레임 수행하던 작업을 파일 생성 시점에 끝내는

방식이었다. 아래 이유로 **비현실적**이라 판단하여 폐기한다:


1. **keycalc 재현의 복잡성**: `keycalc(seedKey, fileCursor)`는 런타임에 MPQ 헤더,

   해시 테이블, 블록 테이블, 시나리오 섹터를 모두 읽어 seedKey를 강화한다.

   정적으로 재현하려면 원본 MPQ 구조를 그대로 보존하거나 완벽히 재구성해야 한다.

2. **oJumperArray 순서 복원 불가**: `initOffsets()`에서 사용하는 랜덤 값 `r`은

   `obfuscatedValueAssigner`로 난독화되어 TRIG 내 EUD 연산 체인에 분산 저장된다.

   triggerKey처럼 brute-force할 수 있는 검증 oracle이 없다.

3. **oJumper 대상 식별 불가**: 어떤 트리거의 nextptr이 oJumper 대상인지, 그 순서가

   무엇인지를 파일만으로 판별할 근거가 없다. `CallerProxy.Evaluate()`에서 빌드 시

   `tKeys[index % 4] = mix2(tKeys[keyIndex], index)`로 키를 갱신하고

   nextptr에서 `modv`를 빼는데, `tKeys` 초기값 자체가 `destKeyVal`(마커에 저장)과

   런타임 랜덤 `r`로 생성되어 `r` 없이는 역산 불가.


### 새 접근: EUD VM 보존 + unFreeze 무력화


**핵심 아이디어**: Freeze EUD 트리거(= eudplib VM)를 죽이지 않고 살려둔다.

VM이 살아있으면 `decryptOffsets`, `encryptOffsets`, `obfpatch/obfunpatch`,

keycalc, 플러그인 로직 등이 런타임에 정상 동작한다.

우리 도구가 이미 trigger body를 복호화했으므로, **`unFreeze()`의 `decryptTrigger`

루프만 무력화**하면 이중 복호화를 방지할 수 있다.


```

원래 런타임 흐름:

  게임 시작 → unFreeze() [trigger body 복호화] → 매 프레임 루프

                ↑ 우리가 이미 했으므로 이것만 막으면 됨


목표 런타임 흐름:

  게임 시작 → unFreeze() [NOP 처리됨] → 매 프레임 루프 (정상 동작)

```


#### 왜 VM을 살려야 하는가


| VM 구성요소 | 죽이면 (현재 Lv1) | 살리면 (Lv2 목표) |

|------------|-------------------|-------------------|

| `decryptOffsets/encryptOffsets` | nextptr 체인 깨짐, 트리거 실행 순서 파괴 | 매 프레임 정상 동작 |

| `obfpatch/obfunpatch` | 빌드 시 주석 처리됨, 영향 없음 | 영향 없음 |

| `keycalc` | seedKey 강화 안 됨, cryptKey 틀림 | 런타임에 정상 계산 |

| `initOffsets` | nextptr 초기 보정 안 됨 | 런타임에 정상 수행 |

| `unFreeze` decryptTrigger 루프 | 실행 안 됨 (안전) | **이중 복호화 → 데이터 파괴!** |

| gameplay EUD 트리거 | 같이 죽음 | 정상 동작 |


#### unFreeze 무력화 전략


`freeze.py`의 unFreeze()에서 trigger body를 복호화하는 부분 (라인 865~868):


```python

# 9. 런타임 복호화 루프 (EUD 코드 생성)

for player in EUDLoopRange(8):

    for ptr, epd in EUDLoopList(tbegin, tend):

        decryptTrigger(epd, triggerKey)

```


이 루프는 EUD 트리거 시퀀스로 변환되어 TRIG 섹션에 들어간다.

무력화 방법 후보:


1. **flag 기반 스킵**: 우리가 trigger body를 복호화할 때 flag에서 `0x80000000`

   비트를 제거했다. `decryptTrigger`는 `flag < 0x80000000`이면 스킵하므로,

   **이미 자동으로 무력화된다.** 다만 이것은 `decryptTrigger` 함수의 Python 소스

   동작이고, 실제 런타임 EUD 코드가 같은 체크를 하는지 확인 필요.


2. **triggerKey 변조**: `triggerKey` EUDVariable의 초기화 연산 체인을 찾아

   값을 변조하면, `decryptTrigger`가 잘못된 키로 복호화를 시도하여 데이터를

   파괴한다 → **위험, 사용 불가.**


3. **decryptTrigger 루프 트리거 비활성화**: unFreeze의 루프를 구성하는 EUD

   트리거들만 식별하여 비활성화. 그러나 어떤 트리거가 이 루프인지 식별이 어렵다.


#### flag 기반 자동 무력화 분석 (가장 유망)


`decryptTrigger`의 EUD 런타임 코드는 다음 체크를 수행:


```python

def decryptTrigger(epd, triggerKey):

    flag = f_dwread_epd(epd + EPD(2368 offset))

    if flag < 0x80000000:

        return  # 암호화 안 됨 → 스킵!

    # ... 복호화 수행 ...

```


우리 도구가 trigger body를 복호화한 후 flag를 복원할 때:

- **Lv1**: `(flag - 0x80000000) & 0x0F` → 하위 4비트만 남김 (0x00~0x0F)

- **Lv2**: `(flag - 0x80000000) & 0x0F` → **Lv1과 동일** (아래 "Lv2 flag 복원" 참조)


어느 쪽이든 `flag < 0x80000000`이 되므로, 런타임 `decryptTrigger`의 첫 체크에서

즉시 스킵된다. **따라서 trigger body를 미리 복호화하고 flag에서 0x80000000

비트만 제거하면, unFreeze의 decryptTrigger 루프는 자동으로 NOP이 된다.**


이것이 성립하려면:

1. ✅ `decryptTrigger`의 EUD 코드가 Python 소스와 동일한 flag 체크를 수행

2. ✅ flag 값이 게임 메모리에서 실제로 읽히는 위치에 있음 (offset 2368 = exec_flags)

3. ✅ `encryptTriggers()`가 flag 외의 방법으로 대상을 판별하지 않음 (flag 체크만 사용)

4. ✅ unFreeze의 루프가 단순히 모든 트리거를 순회하며 decryptTrigger를 호출함


포인트 4에 대해: `freeze.py`의 복호화 루프는 `EUDLoopList(tbegin, tend)`로

**모든 트리거를 순회**하며 각각에 `decryptTrigger`를 호출한다. `decryptTrigger`

내부에서 flag 체크로 비암호화 트리거를 스킵하는 구조이므로, 별도 대상 리스트는

사용하지 않는다.


**결론: flag 기반 자동 무력화가 성립한다.** (2026-05-28 Lv2 구현으로 확인 완료)


#### Lv2 flag 복원: 왜 `& 0x0F`인가 (랜덤 payload 보존 불가)


초기 설계에서는 Lv2가 `flag - 0x80000000` (랜덤 payload 보존)을 사용해야 한다고

판단했으나, 실제 구현에서 이 방식은 **exec_flags를 파괴**한다.


**문제**: `flag - 0x80000000`은 예를 들어 `0x40FD3008`을 exec_flags에 기록한다.

StarCraft의 exec_flags는 하위 4비트만 유효하므로 (0x00~0x0F), 이런 큰 값은

트리거 실행 로직을 깨뜨린다.


**해결**: Lv2도 Lv1과 동일하게 `(flag - 0x80000000) & 0x0F`로 원래 4비트

exec_flags만 추출한다. 관찰된 모든 암호화 트리거의 원래 exec_flags는 `0x08`이었다

(= "Disabled" / "Already Evaluated" 비트).


`decryptTrigger`의 flag 체크는 `flag < 0x80000000`이므로, 0x08이든 0x40FD3008이든

둘 다 스킵 조건을 만족한다. 차이는 StarCraft 트리거 엔진의 exec_flags 해석에 있다.


### Lv2 구현 필수 단계


1. [x] encrypted trigger body key를 brute-force로 복구한다.

2. [x] trigger body를 복호화한다.

3. [x] **flag 복원**: `(flag - 0x80000000) & 0x0F`로 원래 4비트 exec_flags만 추출.

   Lv1과 동일한 방식. 랜덤 payload 보존(`flag - 0x80000000`)은 exec_flags를

   파괴하므로 불가 (상세: "Lv2 flag 복원" 섹션).

4. [x] **EUD 트리거 비활성화 스킵**: Lv2 모드에서 `DisableFreezeEudTriggers()` 미호출.

   `ProcessFreezeProtection`에 `stats.Lv2Mode` 분기 구현 완료.

5. [x] **MPQ in-place 패치**: `WriteLv2Mpq()`로 원본 MPQ 바이너리 내

   scenario.chk 블록만 교체. 해시/블록 테이블 등 MPQ 외부 구조 보존.

6. [!] **인게임 검증 실패 (2026-05-28)**: chk 내용 수정 → keycalc 입력 변화 →

   런타임 cryptKey 불일치 → decryptOffsets 실패 → 트리거 실행 순서 파괴.

   MPQ 외부 구조 보존만으로는 불충분. keycalc가 chk 섹터 데이터도 읽기 때문.

   `--lv2-diag`의 keycalc 후보 diff에서 header/hash table은 유지되고
   scenario.chk raw block 일부 sector만 변경되는 것을 확인했다.

7. [ ] **keycalc 정적 구현**: keycalc.pyc를 C#으로 포팅하여 cryptKeyVal 독립 계산.

8. [ ] **keycalc 보상 또는 완전 정적 복원**: 아래 "Lv2 Phase 2 계획" 참조.

9. [x] **static keycalc 후보 모델 진단 추가**: `keycalc_disasm_full.txt` 기반으로
   원본/패치 MPQ의 후보 seed/cryptKeyVal 변화를 출력한다. 두 테스트맵 모두
   Lv2 패치 후 후보 cryptKeyVal이 변경됨을 확인했다.


### Lv2의 핵심 리스크: MPQ 구조 보존


keycalc가 읽는 MPQ 데이터:


```

keycalc가 피딩하는 데이터 (keycalc.pyc 디스어셈블리 기준):

  1. MPQ 헤더 8개 dword (mpqHeaderEPD를 8회 읽음)

  2. 해시 테이블 전체 (hashTableOffsetDiv4부터 mpqHashTableSize 엔트리)

  3. 블록 테이블 일부 (initialBlockIndex ~ mpqBlockTableSize)

  4. chk 섹터 데이터 (chkSector_ ~ chkSectorNum, stride 3)

  5. 블록 테이블 전체 × SAMPLEN/4회 순회 (seedKey[i] += seedKey[i] + sample)

  6. 64회 seed key self-mixing

  7. 블록 테이블 끝 4개 dword로 최종 마무리

```


새 MPQ에 repack하면 해시 테이블, 블록 테이블, 섹터 배치가 모두 달라진다.

따라서 **원본 MPQ를 최대한 그대로 보존하는 경로가 필요**하다.


가능한 접근:

- **원본 MPQ 바이너리 내에서 scenario.chk만 in-place 교체**: MPQ 구조 자체는

  건드리지 않고, scenario.chk의 내용만 (trigger body 복호화 + flag 수정) 반영.

  freezeMpq가 적용한 해시/블록 테이블 암호화도 그대로 유지.

- **freezeMpq 역연산**: MPQ 해시/블록 테이블 암호화를 풀고, 원본 구조로 복원한 뒤

  scenario.chk만 교체. keycalc가 읽는 것은 **암호화된** MPQ인지

  **복호화된** MPQ인지 확인 필요.


keycalc는 **런타임에** MPQ를 읽는다. 런타임에는 freezeMpq가 적용한 암호화가

걸려 있는 상태이다. 따라서 keycalc가 기대하는 것은 **freezeMpq 적용 후의

MPQ 구조**이다. 결국 freezeMpq 암호화를 건드리면 안 된다.


**최종 결론**: Lv2 출력은 원본 MPQ 파일을 그대로 두고, 내부 scenario.chk만

trigger body 복호화 + flag 수정을 적용해야 한다. MPQ 재패키징은 하지 않는다.
다만 이것만으로는 충분하지 않다. keycalc가 scenario.chk sector payload 자체도 읽으므로,
in-place 패치가 바꾼 sector를 기준으로 keycalc 보상 또는 정적 offset 복원이 추가로 필요하다.

static 후보 모델 기준으로도 원본과 Lv2 패치 후 cryptKeyVal이 달라진다. 즉 실패 원인은
“MPQ 구조가 움직였다”보다 “바뀐 scenario sector payload가 keycalc seed 강화 결과를 바꾼다”에 가깝다.


`obfjump.pyc` 디스어셈블리 기준 offset 암호화 핵심:


```python

# decryptOffsets()

cryptKey2 = cryptKey

for jumperEPD in oJumperArray:

    v = f_dwread_epd(jumperEPD)

    v ^= cryptKey2

    cryptKey += v

    jumperEPD.dest = EPD(v.dest)

    cryptKey2 += 0x46B7A62C


# encryptOffsets()

cryptKeyInv = -cryptKey

cryptKey2 = cryptKey

for jumperEPD in oJumperArray:

    v = f_dwread_epd(jumperEPD)

    cryptKeyInv += v

    x = v ^ cryptKey2

    SetMemoryC(jumperEPD.dest, SetTo, EPD(x.dest))

    cryptKey2 += 0x46B7A62C

```


빌드 시 `CallerProxy.Evaluate()`는 `oJumper` 순서별로 `index % 4`의 `tKeys`를

`mix2(tKeys[keyIndex], index)`로 갱신하고, nextptr에 `modv`를 뺀 값을 기록한다.

런타임 `initOffsets()`는 seedKey와 oJumper index 기반 key를 각 jumper nextptr 위치에

더해 초기 보정을 수행한다. 따라서 Lv2 정적 복원은 단순 trigger body 복호화만으로는

충분하지 않고, oJumperArray와 그 대상 nextptr 위치까지 찾아야 한다.
