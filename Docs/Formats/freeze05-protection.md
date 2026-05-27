# Freeze05 Protection — Reverse Engineering Notes

## 개요

Freeze05는 eudplib/euddraft 기반의 스타크래프트 맵 보호 기법이다.

원본 소스: [phu54321/euddraft/freeze/](https://github.com/phu54321/euddraft/tree/master/freeze)
실제 배포: [armoha/euddraft](https://github.com/armoha/euddraft) — freezeMpq.pyd (컴파일된 C++ 확장)

### Freeze가 하는 일

#### 빌드 시 (맵 파일에 적용)

| 단계 | 함수 | 역할 |
|------|------|------|
| 1 | `encryptTrigger()` | 트리거 body(condition/action)를 수학적으로 변조 → 에디터에서 읽을 수 없음 |
| 2 | `encryptOffsets()` | 트리거 체인의 nextptr 등 구조적 포인터 암호화 → 트리거 간 연결 구조 파괴 |
| 3 | `obfpatch()` | 트리거 구조에 난독화 변형 적용 |
| 4 | `applyFreezeMpqModification()` | MPQ 해시/블록 테이블 암호화 → 일반 MPQ 추출 도구로 내부 파일 추출 불가 |

#### 런타임 (게임 실행 중 EUD VM이 수행)

```
게임 시작 시 1회:
  unFreeze()              → 암호화된 트리거 body 복호화

매 프레임 반복:
  decryptOffsets()        → 오프셋 복호화 (트리거 체인 복원)
  obfpatch()              → 난독화 패치 적용
  RunTrigTrigger()        → 실제 게임 트리거 실행
  obfunpatch()            → 난독화 패치 해제
  encryptOffsets()        → 오프셋 재암호화
```

매 프레임 encrypt/decrypt를 반복하는 이유: 메모리 덤프 도구(Cheat Engine 등)로
복호화된 상태를 캡처하기 어렵게 만들기 위함. 한 프레임만 복호화하고 트리거 실행 후 즉시 재암호화.

#### 보호 해제의 한계

현재 도구는 EUD 트리거를 전부 비활성화한다. 이렇게 하면:

| 항목 | 상태 |
|------|------|
| 에디터에서 열기/편집 | ✅ 가능 (트리거 body 복호화됨) |
| 게임 정상 실행 | ❌ 불가능 |

게임이 안 되는 이유:

1. **EUD VM 전체가 죽음**: freeze EUD 트리거 = eudplib VM의 일부. VM을 죽이면
   `decryptOffsets`, `obfpatch/obfunpatch`, `encryptOffsets`, 플러그인 로직 전부 사라짐
2. **오프셋 미복구**: `decryptOffsets()`가 매 프레임 트리거 체인 nextptr을 복구하는데,
   이것도 안 되니 트리거 실행 순서 자체가 깨짐

게임 실행까지 가려면 EUD VM을 통째로 살려두되 `unFreeze()`만 무력화해야 한다.
(우리 도구가 이미 트리거 body를 복호화했으므로 `unFreeze()`가 또 실행되면 이중 복호화로 데이터 파괴)
그러나 EUD 트리거들 사이에서 unFreeze 부분만 정확히 식별하여 분리하는 것은 매우 어렵다.

## 보호 레벨 정의

| 레벨 | 의미 | 상태 | 비고 |
|------|------|------|------|
| Lv0 | 에디터에서 열기만 가능 | ✅ 가능 | ScmDraft로 열기 |
| Lv1 | 보호 트리거 제거, 편집 가능 | ✅ 구현 완료 | EUD 비활성화 + 트리거 복호화 |
| Lv2 | Lv1 + 게임 정상 실행 | 🚧 정적 복원 진단 구현 중 | triggerKey 복구 완료, keycalc/offset 복원 남음 |
| Lv3 | 완전 복원 (원본과 동일) | ❌ 미착수 | Freeze 보호 흔적 완전 제거 |

**현재 상태**: Lv1까지 완료. 에디터에서 트리거를 읽고 편집할 수 있으나 게임은 실행 불가.
파일 단독 `triggerKey` 복구와 encrypted trigger body 복호화는 구현 완료되었다.
Lv2의 남은 핵심 문제는 `keycalc(seedKey, fileCursor)`의 정적 재현과
`oJumperArray` 기반 nextptr/offset 복원이다.

## Lv2 정적 복원 방향 (2026-05)

실패한 in-place MPQ 패치 실험 결과, MPQ 구조만 보존하는 접근은 폐기한다.
Lv2는 Freeze 런타임 VM이 매 프레임 수행하던 작업을 파일 생성 시점에 끝내는
정적 복원기로 구현한다.

필수 단계:

1. [x] encrypted trigger body key를 brute-force로 복구한다.
2. [x] trigger body를 복호화한다.
3. [ ] `keycalc(seedKey, fileCursor)`를 원본 MPQ 구조 기준으로 정적으로 재현한다.
4. [ ] `initOffsets/decryptOffsets/encryptOffsets`의 oJumper 체인을 찾아 nextptr을 복원한다.
5. [ ] Freeze runtime 트리거만 비활성화하고 실제 gameplay/EUD 트리거는 유지한다.
6. [ ] 최종 파일은 일반 `WriteStandardMpq()` 경로로 새 MPQ에 repack한다.

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

## 파일 내 구조

### Freeze 마커

MPQ 파일 끝 부분에 48바이트 구조 존재:

```
[seedKey: 16 bytes (uint32 × 4)]
[marker:  "freeze05 protect" (16 bytes ASCII)]
[destKey: 16 bytes (uint32 × 4)]
```

`DetectFreezeProtection()`이 이 마커를 검색한다.

### 키 계보

```
                 ┌─── seedKey[4] ──── (마커에 저장)
                 │
 빌드 시 생성 ──┤── destKey[4] ──── (마커에 저장)
                 │
                 ├── fileCursorVal ─ (keyfile에 저장)
                 │
                 └── triggerKeyVal ─ (랜덤, 난독화된 EUD 트리거 체인으로 저장)
                                      │
                                      ▼
                  cryptKeyVal = ComputeCryptKeyVal(seedKey)
                                      │
                                      ▼
              triggerKey = mix2(triggerKeyVal, cryptKeyVal)
                    ↑
                    └── 이 값으로 트리거를 암호화/복호화
```

### triggerKeyVal 저장 방식

`triggerKeyVal`은 `random.randint(0, 0xFFFFFFFF)`로 생성되는 랜덤 32비트 값이다.
파일 내에 평문으로 저장되지 않지만, TRIG 섹션에 **난독화된 형태로 존재**한다.

`obfuscatedValueAssigner(triggerKey, triggerKeyVal)` (freeze/utils.py)이 값을
32~96개의 산술 연산 체인으로 분해하여 EUD 트리거 액션(SetDeaths)들에 분산 저장한다.

```python
def obfuscatedValueAssigner(v, vInsert):
    desiredOperationCount = random.randint(32, 96)
    t = random.randint(0, 0xFFFFFFFF)

    # 초기: vInsert를 (vInsert + t)와 (-t)로 분리
    operations = [L('+', v, vInsert + t, -t)]

    while len(operations) < desiredOperationCount:
        # 기존 연산의 상수를 골라서 더 작은 조각으로 분해
        # 분해 방식은 랜덤 선택: +, ^, -, &, | 중 택1
        # 중간값에 T2(random) 변환 적용
        targetValue = ...
        t1 = T2(random.randint(0, 0xFFFFFFFF))
        t2 = random.randint(0, 0xFFFFFFFF)

        optype = random.randint(0, 4)
        if optype == 0:   # 덧셈 분해
            operation = ['+', srcVariable, t1, targetValue - t1]
        elif optype == 1: # XOR 분해
            operation = ['^', srcVariable, t1, targetValue ^ t1]
        elif optype == 2: # 뺄셈 분해
            operation = ['-', srcVariable, targetValue + t1, t1]
        elif optype == 3: # AND 분해
            a = t1 & t2;  b = ~t1 & t2
            operation = ['&', srcVariable, a | targetValue, b | targetValue]
        elif optype == 4: # OR 분해
            a = t1 | t2;  b = ~t1 | t2
            operation = ['|', srcVariable, a & targetValue, b & targetValue]

    return operations
```

각 operation은 EUD 트리거의 SetDeaths 액션으로 변환된다.
게임 실행 시 이 액션들이 순서대로 실행되면 최종적으로 `triggerKeyVal`이 복원된다.

**정적 triggerKeyVal 추출의 어려움:**
- 32~96개의 연산을 역추적해야 함
- 중간값에 T2() 변환이 적용되어 있음
- 연산 순서가 `assignerMerge`로 다른 변수의 연산과 인터리빙됨
- 어떤 트리거/액션이 triggerKey 초기화인지 식별하는 것 자체가 어려움
- `encryptOffsets`가 nextptr을 암호화하므로 실제 트리거 실행 순서도 불명확함
- EUDVariable 주소를 식별해야 하며, seedKey/fileCursor/triggerKey 할당이 섞여 있음

**수정된 결론:** `triggerKeyVal`은 맵 파일 내 EUD 연산 체인에 존재하지만,
이를 직접 추출하려면 assigner 체인, nextptr 암호화, EUDVariable 주소를 함께
해석해야 한다. 부분 VM 시뮬레이션만으로 안정적으로 뽑기는 어렵고, 사실상
Freeze/eudplib VM의 큰 부분을 구현해야 한다.

이 문제는 `triggerKey` 복구의 blocker가 아니다. 현재 도구는 VM에서
`triggerKeyVal`을 직접 뽑지 않고, encrypted trigger body에서 최종
`triggerKey = mix2(triggerKeyVal, cryptKeyVal)`를 직접 탐색한다. 이후
`unmix2(triggerKey, cryptKeyVal)`로 진단용 `triggerKeyVal`도 계산할 수 있다.

런타임 덤프는 Lv2 복원 전략에서 제외한다. Freeze는 매 프레임
`decryptOffsets → RunTrigTrigger → encryptOffsets`를 반복하므로, 외부 메모리 덤프가
완전히 복호화된 안정 상태를 보장하지 못한다. 현재 주 복구 전략은
`파일 단독 triggerKey 직접 탐색 + type-byte targeted partial decrypt 검증`이며
이 경로는 구현 완료되었다.

### 파일 단독 triggerKey 복구 전략

정적 wlist 복구는 해당 트리거 하나를 복호화하는 데에는 충분하지만,
모든 암호화 트리거를 안정적으로 복구하려면 공통 `triggerKey`가 필요하다.
`triggerKey`는 최종적으로 32비트 값 하나이므로, VM에서 `triggerKeyVal`을
찾는 대신 파일의 encrypted trigger를 anchor로 삼아 직접 탐색한다.

복구 전략:

1. encrypted trigger 하나를 anchor로 선택하고 `flag - 0x80000000` 값을 읽는다.
2. 후보 key마다 wlist 16개를 생성한다.
3. wlist가 건드리는 128개 dword 중 condition/action type byte가 포함된 dword만 부분 복호화한다.
4. type byte가 유효 범위(condition type ≤ 23, action type ≤ 63)를 벗어나면 즉시 기각한다.
5. 빠른 검증을 통과한 소수 후보만 전체 trigger body 복호화 + 구조 검증을 수행한다.
6. 유일한 key가 검증되면 그 `triggerKey`로 모든 encrypted trigger를 복호화한다.

이 방식은 여전히 `2^32` key space를 훑지만, 후보마다 2400바이트 전체를 복호화하지
않는다. 대부분의 오답 key는 몇 개의 type byte 검사에서 탈락하므로,
기존 full-decrypt brute force보다 훨씬 현실적이다.

## 암호화 알고리즘 (armoha 바이너리 검증 완료 ✅)

소스: euddraft 0.10.2.5 릴리스 → `library.zip` → `freeze/crypt.pyc`, `freeze/trigcrypt.pyc` 디스어셈블리.

### T2 함수 (비선형 변환)

```python
def T2(x):
    xsq = x * x           # x²
    x4 = xsq * xsq        # x⁴  (주의: x⁶이 아님!)
    return x * (xsq * (x4 + 1) + 1) + 0x8ADA4053
```

다항식: `f(x) = x⁷ + x³ + x + 0x8ADA4053 (mod 2³²)`

**상수 검증**: phu54321 Python 소스, armoha 컴파일 pyd, armoha cx_Freeze pyc 모두 동일한 상수 사용 확인.

### Mix2 함수

```python
def mix2(x, y):
    return T2(x) + y + 0x10F874F3
```

### 컴파일러 최적화

실제 freezeMpq.pyd에서는 T2와 mix2가 인라인되어 하나의 상수로 합쳐짐:

```
T2_CONST + MIX_CONST = 0x8ADA4053 + 0x10F874F3 = 0x9BD2B546
```

pyd 바이너리에서 `0x9BD2B546`이 69회 출현 (LEA/ADD 명령어에서 사용, 0x1175~0x1B71 구간에 집중).
`0x8ADA4053`이나 `0x10F874F3`은 개별적으로 존재하지 않음.

### 검증 결과

```
C# FreezeT2(0)               = 0x8ADA4053 ✅
C# FreezeMix2(0, 0)          = 0x9BD2B546 ✅
C# ComputeCryptKeyVal([0]*4)  = 0xA31836DE ✅
Python 출력과 100% 일치
```

### ComputeCryptKeyVal

```python
def ComputeCryptKeyVal(seedKey):
    v = 0
    v = mix2(v, seedKey[0])
    v = mix2(v, seedKey[1])
    v = mix2(v, seedKey[2])
    v = mix2(v, seedKey[3])
    v = mix2(v, 0)
    return v
```

## 트리거 암호화 상세

### 트리거 구조 (2400 bytes)

```
[conditions: 16 × 20 bytes = 320 bytes]  (offset 0~319)
[actions:    64 × 32 bytes = 2048 bytes]  (offset 320~2367)
[exec_flags: 4 bytes]                     (offset 2368~2371)
[players:    28 bytes]                     (offset 2372~2399)
```

### 암호화 과정 (encryptTrigger) — armoha 바이너리에서 확인 ✅

euddraft 0.10.2.5의 `freeze/trigcrypt.pyc` 바이트코드 디스어셈블리 결과.
phu54321 원본과 100% 동일한 알고리즘 확인.

```python
def encryptTrigger(bTrigger_, key):
    bTrigger = bytearray(bTrigger_)
    r = random.randint(0, 0xFFFFFFFF)

    # 1. 플래그 체크 — 이미 암호화된 트리거는 스킵
    flag = b2i4(bTrigger, 2368)
    if flag >= 16:
        return bTrigger_  # 스킵!

    # 2. 새 플래그 생성
    flag = flag + 0x80000000 + (r & 0x7FFFF000)
    bTrigger[2368:2372] = i2b4(flag)

    # 3. 키 스트림 생성
    flag -= 0x80000000
    r = mix2(key, flag)
    r = mix2(r, key)

    wlist = []
    for i in range(tabCount):           # tabCount = 16
        wlist.append(r % 74)            # stride = 74
        r = mix2(r, key + i)            # advance: key + i

    # 4. 역순으로 뺄셈 (암호화)
    for i in range(tabCount - 1, -1, -1):  # 15→0 역순!
        w = wlist[i]
        adddw = mix2(w, i)
        for j in range(8):              # stride 간격으로 8개 dword 수정
            dw = b2i4(bTrigger, w * 4)
            bseti4(bTrigger, w * 4, dw - adddw)  # 뺄셈!
            w += 74

    return bTrigger
```

### 복호화 과정 (decryptTrigger)

```python
def decryptTrigger(trigger, key):
    flag = read_u32(trigger, 2368)
    if flag < 0x80000000:
        return  # 암호화 안 됨

    flag -= 0x80000000
    r = mix2(key, flag)
    r = mix2(r, key)

    wlist = []
    for i in range(16):
        wlist.append(r % 74)
        r = mix2(r, key + i)

    # 정순으로 덧셈 (복호화)
    for i in range(16):                 # 0→15 정순!
        w = wlist[i]
        adddw = mix2(w, i)
        for j in range(8):
            dw = read_u32(trigger, w * 4)
            write_u32(trigger, w * 4, dw + adddw)  # 덧셈!
            w += stride
```

### wlist 공식 검증 결과

| 요소 | phu54321 원본 | armoha 0.10.2.5 | C# 구현 | 일치 |
|------|--------------|-----------------|---------|------|
| T2 상수 | `0x8ADA4053` | `0x8ADA4053` | `FreezeT2Const` | ✅ |
| Mix 상수 | `0x10F874F3` | `0x10F874F3` | `FreezeMixConst` | ✅ |
| stride | `2368 // 32 = 74` | `74` | `FreezeStride` | ✅ |
| tabCount | `16` | `16` | `FreezeTabCount` | ✅ |
| wlist init | `mix2(key,flag) → mix2(r,key)` | 동일 | 동일 | ✅ |
| wlist advance | `mix2(r, key + i)` | `mix2(r, key + i)` | `FreezeMix2(r, key + i)` | ✅ |
| encrypt 방향 | `dw - adddw` (역순) | `dw - adddw` (역순) | N/A (복호화만) | ✅ |
| decrypt 방향 | `dw + adddw` (정순) | `dw + adddw` (정순) | `dw += adddw` (정순) | ✅ |

**결론**: armoha가 phu54321의 트리거 암호화 알고리즘을 그대로 유지하고 있으며,
우리 C# 구현(`TryDecryptFreezeTrigger`)이 정확하다. **key만 알면 100% 복호화 가능.**

### encryptTriggers (빌드 시 호출)

`freeze/trigcrypt.pyc`에서 확인. `unFreeze()` (freeze.py)에서 두 번 호출된다:

```python
# freeze.py line 95, 101:
encryptTriggers(mix2(triggerKeyVal, cryptKeyVal))
```

```python
def encryptTriggers(cryptKey):
    chkt = GetChkTokenized()
    trigSection = chkt.getsection("TRIG")
    p = 0.05  # 5% 확률

    for i in range(0, len(trigSection), 2400):
        bTrigger = trigSection[i:i+2400]
        if not GetInlineCodePlayerList(bTrigger):
            if random.random() < p:
                # ... encryptedCount 기록 ...
                bTrigger = encryptTrigger(bTrigger, cryptKey)
        bSet.append(bTrigger)
```

### 핵심 특성

- **수정 범위**: 트리거 바디의 2368 바이트 중 약 120개 dword만 수정 (전체의 ~20%)
- **수정 안 됨**: 실행 플래그(2368~2371), 플레이어 바이트(2372~2399)
- **이중 암호화 불가**: `flag >= 16` 체크로 이미 암호화된 트리거는 재암호화 스킵
- **5% 확률**: `encryptTriggers()`에서 각 트리거는 5% 확률로 선택됨
- **두 번 호출**: `encryptTriggers(key)`가 두 번 호출되나, 첫 번째에서 암호화된 것은 두 번째에서 스킵
- **인라인 코드 트리거 보호**: `GetInlineCodePlayerList(bTrigger)`가 true인 트리거는 암호화 스킵 (EUD VM 자체 트리거)

### 플래그 구조

```
flag (암호화 후):
  bit 31:     항상 1 (0x80000000)
  bits 12~30: 랜덤 (0x7FFFF000 마스크)
  bits 0~3:   원래 실행 플래그 값

예: flag = 0xC0FD3008
  → 원래 실행 플래그: 0x8
  → 랜덤 부분: 0x40FD3000
```

## EUD 보호 트리거 식별

### IsFreezeEudTrigger 판별 기준

Freeze 보호 전용 트리거는 다음 조건을 모두 만족:

1. SetDeaths(type 45) 액션만 포함 (빈 슬롯 제외)
2. 최소 하나의 EUD 주소 SetDeaths 존재 (player > 27)
3. 게임플레이 액션 없음 (DisplayText, CreateUnit 등)

### 비활성화 방식 (Lv1 — 구현 완료)

트리거를 **제거하지 않고** in-place로 비활성화:

```csharp
// 플레이어 실행 바이트 클리어 (offset 2372~2399)
for (int i = 0; i < 28; i++)
    trigData[offset + 2372 + i] = 0;

// 실행 플래그 클리어 (offset 2368~2371)
trigData[offset + 2368..2371] = 0;
```

**제거하면 안 되는 이유**: eudplib VM은 트리거의 절대 메모리 주소를 하드코딩.
트리거를 제거하면 후속 트리거의 주소가 밀려서 VM이 깨진다.

### obfpatch 상태

phu54321 소스에서 `obfpatch.py`의 모든 `issuePatcher` 호출이 **주석 처리**되어 있음.
→ ObfuscatedJump은 Freeze 코드 내부에서만 사용되고, 메인 eudplib VM에는 삽입되지 않음.
→ Freeze EUD 트리거만 비활성화하면 VM 자체는 정상 작동.

## freezeMpq.pyd 분석

### 파일 정보

| 속성 | 값 |
|------|-----|
| 위치 | `Tools/reference/euddraft/lib/freezeMpq.pyd` |
| 크기 | 219,136 bytes (219KB) |
| 아키텍처 | x86-64 (PE32+) |
| 익스포트 | `PyInit_freezeMpq`, `applyFreezeMpqModification` |
| 종류 | pybind11 C++ Python 확장 모듈 |

### 역할 분리 (확정 ✅)

디스어셈블리를 통해 pyd가 트리거 암호화를 **수행하지 않음**을 확정했다:

| 증거 | 결과 |
|------|------|
| stride 74 (0x4A) 상수 | .text 섹션에 **없음** |
| trigger body size 2368 (0x940) | .text 섹션에 **없음** |
| trigger size 2400 (0x960) | .text 섹션에 **없음** |
| 32비트 0x80000000 플래그 체크 | **없음** (64비트 0x8000000000000000만 존재 — 메모리 할당 체크용) |

```
freezeMpq.pyd      → MPQ 구조 수정만 (해시/블록 테이블 암호화, freeze 마커 삽입)
freeze Python 모듈 → 트리거 암호화/복호화, wlist 생성, nextptr 암호화 등
```

### 실제 트리거 암호화 위치: freeze Python 모듈

`applyeuddraft.py`에서 확인된 빌드 흐름:
```python
from freeze import decryptOffsets, encryptOffsets, obfpatch, obfunpatch, unFreeze
```

```
빌드 순서:
1. SaveMap()       ← freeze 모듈이 트리거 암호화 수행 (encryptTrigger/encryptOffsets)
2. freezeMpq.applyFreezeMpqModification()  ← pyd가 MPQ 해시/블록 테이블만 수정
```

**freeze 모듈의 특성**:
- armoha의 **비공개** Python 코드 (GitHub에 미공개)
- euddraft 바이너리 배포판에만 포함 (cx_Freeze로 빌드, `lib/library.zip` 내 `.pyc`)
- eudplib의 EUD 코드 생성 레이어와 통합
- **이 모듈이 실제 encryptTrigger / wlist 생성 / 트리거 body 수정을 담당**
- ~~phu54321 원본 소스는 초기 설계이며, armoha 버전은 wlist 생성 알고리즘이 다르다~~
- **확인 완료**: armoha 0.10.2.5의 wlist 생성 알고리즘은 phu54321 원본과 **100% 동일**

### pyd 내부 상수 확인

```
0x9BD2B546 (T2+MIX combined):  69회 출현 ✅ (0x1175~0x1B71 범위 단일 함수 그룹에 집중)
0x8ADA4053 (T2 단독):          미발견 (인라인 최적화)
0x10F874F3 (MIX 단독):         미발견 (인라인 최적화)
"freeze05 protect":            .rdata 0x29F50에 존재, .text에서 참조됨
```

### pyd 내부 함수 구조 (디스어셈블리)

T2+mix2 인라인 (offset 0x1153):
```nasm
mov  eax, edi           ; eax = x
imul eax, edi           ; eax = x² (xsq)
mov  ecx, eax           ; ecx = xsq
imul ecx, eax           ; ecx = x⁴ (xsq²)
inc  ecx                ; ecx = x⁴ + 1
imul ecx, eax           ; ecx = xsq * (x⁴ + 1)
; ... (다음 T2 계산 인터리빙) ...
inc  ecx                ; ecx = xsq*(x⁴+1) + 1
imul ecx, edi           ; ecx = x * (xsq*(x⁴+1) + 1) = T2_inner
add  ecx, 0x9BD2B546    ; ecx += T2_CONST + MIX_CONST
add  edx, ecx           ; result = y + T2_inner + combined_const
```

다항식 확인: **`x*(x²*(x⁴+1)+1)` — Python 소스와 동일** ✅

### pyd 내 keycalc 함수 (offset 0x1810)

```nasm
; 내부 루프: 512회 반복 (cmp r9d, 0x200)
; 외부 루프: 4회 반복 (mov edi, 4)
lea  ecx, [rcx + rcx*2]        ; ecx *= 3
div  ebx                        ; r % divisor
add  ecx, [rsi + rdx*4]        ; ecx += data[remainder]
; ... T2+mix2 ... (r = mix2(r, i))
```

의사 코드:
```python
for k in range(4):      # 4개 state 값
    ecx = state[k]
    for i in range(512): # 512회 반복
        ecx = ecx * 3 + data[r % divisor]
        r = mix2(r, i)   # 주의: key+i가 아닌 i만 사용!
    state[k] = ecx
```

**참고**: 이 함수는 seedKey → cryptKeyVal **key derivation** 전용이다.
`r = mix2(r, i)` 패턴이 발견되었으나 이것은 wlist 생성과는 별개이다.
0x10F0 함수의 두 호출처 (0x1F35=key derivation, 0x6DCB=main freeze function) 모두
MPQ 수준 작업에 사용되며, 트리거 암호화/wlist 생성은 Python freeze 모듈에서 수행된다.

## 구현 파일

### FreezeDecryptor.cs (신규)

`StarcraftMapUnprotector` partial class에 추가:

| 함수 | 역할 |
|------|------|
| `FreezeT2(x)` | T2 비선형 변환 |
| `FreezeMix2(x, y)` | Mix2 함수 |
| `ComputeCryptKeyVal(seedKey)` | seedKey → cryptKeyVal 계산 |
| `TryDecryptFreezeTrigger(...)` | 키 기반 단일 트리거 복호화 |
| `ValidateDecryptedTrigger(...)` | 복호화 결과 유효성 검증 |
| `DecryptAllFreezeTriggers(...)` | 키로 모든 트리거 복호화 |
| `ApplyRuntimeDump(...)` | 레거시 진단용 CE 메모리 덤프 적용 |
| `DisableFreezeEudTriggers(...)` | 보호 EUD 트리거 in-place 비활성화 |
| `ProcessFreezeProtection(...)` | 전체 Freeze 처리 진입점 |

### 처리 순서 (BuildNormalizedChk 내)

```
1. MergeRepeated("TRIG")
2. TrimRecordSection("TRIG")
3. RemoveFakeUnitRecords
4. ProcessFreezeProtection ←── Freeze 처리
   ├── 키 기반 복호화 (키를 알고 있는 경우)
   ├── --apply-dump 런타임 덤프 적용 (레거시 진단용)
   └── EUD 트리거 비활성화
5. RemoveFakeTriggerRecords  (Freeze 맵은 early return)
6. RepairLocations
7. NormalizeStringTable      (Freeze 맵은 skip)
8. NormalizeTriggerRecords   (Freeze 맵은 skip)
```

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

### freezeMpq.pyd 함수 맵 (역공학 결과)

| 오프셋 | 역할 | 비고 |
|--------|------|------|
| 0x10F0 | mix2 체인 (키 상태 생성) | 호출처 2곳: 0x1F35(key derivation), 0x6DCB(main) |
| 0x1150-0x1B71 | 언롤링된 T2/mix2 계산 루프 | 0x9BD2B546 69회 출현 구간 |
| 0x1810 | keycalc (4×512 key derivation) | `r = mix2(r, i)` 패턴, wlist 생성 아님 |
| 0x2390 | MPQ CryptTable 생성 (LCG: ecx*125+3, 256회) | StormLib 호환 |
| 0x2590 | MPQ EncryptMpqBlock (0xEEEEEEEE 시드) | StormLib 호환 |
| 0x2610 | MPQ HashString | StormLib 호환 |
| 0x6ACD-0x709D | 메인 applyFreezeMpqModification | MPQ 수정만, 트리거 암호화 없음 |
| 0x1E5A0 | PyInit_freezeMpq 엔트리 포인트 | pybind11 |

## armoha freeze 모듈 추출 기록

### 추출 경로

```
euddraft 0.10.2.5 릴리스 (GitHub armoha/euddraft)
  → euddraft0.10.2.5.zip
  → lib/library.zip (cx_Freeze 8.3.0 패키징)
  → freeze/*.pyc (Python 3.13.0 바이트코드)
  → xdis 6.1.8로 디스어셈블리
```

### freeze 패키지 구조

| 파일 | 크기 | 역할 |
|------|------|------|
| `__init__.py` | 346B | 패키지 초기화 |
| `crypt.pyc` | 3041B | T2, mix2, unmix2 수학 함수 (빌드 시 + EUD 런타임 양쪽) |
| `freeze.pyc` | 5867B | **unFreeze() 메인 진입점** — 키 생성, 암호화 호출, 런타임 복호화 |
| `keycalc.pyc` | 6430B | seedKey에 MPQ 구조 데이터를 피딩하여 키 강화 |
| `trigcrypt.pyc` | 4599B | **encryptTrigger/decryptTrigger** — wlist 생성 및 트리거 암호화/복호화 |
| `trigutils.pyc` | 12542B | EUD 트리거 유틸리티 (SetDeaths 래퍼, ObfuscatedAdd 등) |
| `utils.pyc` | 5067B | **obfuscatedValueAssigner** — 값 난독화 체인 생성 |
| `obfjump.pyc` | 8979B | ObfuscatedJump, cryptKey, encryptOffsets, initOffsets |
| `obfpatch.pyc` | 1289B | obfpatch/obfunpatch (모든 issuePatcher 호출 주석 처리됨) |
| `pdefault.pyc` | 4189B | 기본 설정 |
| `mpqh.pyc` | 558B | getMapHandleEPD |

### 키 생성 전체 흐름 (freeze.py unFreeze())

```python
# 1. 9개 랜덤 키 생성
keys = [random.randint(0, 0xFFFFFFFF) for _ in range(9)]
seedKeyVal    = keys[0:4]   # → freeze 마커에 저장 (접근 가능)
destKeyVal    = keys[4:8]   # → freeze 마커에 저장 (접근 가능)
fileCursorVal = keys[8]     # → "(keyfile)"에 저장 (접근 가능)

# 2. triggerKeyVal — 별도 랜덤 생성
triggerKeyVal = random.randint(0, 0xFFFFFFFF)  # 난독화 저장 (접근 어려움)

# 3. EUD 변수 생성 + 난독화 할당
seedKey = [EUDVariable() for _ in seedKeyVal]
triggerKey = EUDVariable()
fileCursor = EUDVariable()

assigner = []
for i, key in enumerate(seedKeyVal):
    assignerMerge(assigner, obfuscatedValueAssigner(seedKey[i], key))
assignerMerge(assigner, obfuscatedValueAssigner(fileCursor, fileCursorVal))
assignerMerge(assigner, obfuscatedValueAssigner(triggerKey, triggerKeyVal))
writeAssigner(assigner)  # → 전부 SetDeaths 트리거 액션으로 출력

# 4. cryptKeyVal 계산 (빌드 시, 순수 Python)
cryptKeyVal = 0
cryptKeyVal = mix2(cryptKeyVal, seedKeyVal[0])
cryptKeyVal = mix2(cryptKeyVal, seedKeyVal[1])
cryptKeyVal = mix2(cryptKeyVal, seedKeyVal[2])
cryptKeyVal = mix2(cryptKeyVal, seedKeyVal[3])
cryptKeyVal = mix2(cryptKeyVal, 0)

# 5. 런타임 cryptKey 초기화 (EUD 코드 생성)
tempKey1 = mix(cryptKey, seedKey[0])      # EUD 연산
tempKey2 = mix(tempKey1, seedKey[1])
tempKey3 = mix(tempKey2, seedKey[2])
tempKey4 = mix(tempKey3, seedKey[3])
mix(tempKey4, 0, ret=cryptKey)            # cryptKey = ComputeCryptKeyVal(seedKey)

# 6. seedKey 강화 (런타임)
keycalc(seedKey, fileCursor)              # MPQ 구조 데이터로 seedKey 강화

# 7. 트리거 암호화 (빌드 시, 순수 Python)
encryptTriggers(mix2(triggerKeyVal, cryptKeyVal))   # 1차
encryptTriggers(mix2(triggerKeyVal, cryptKeyVal))   # 2차 (이미 암호화된 것은 스킵)

# 8. 런타임 triggerKey 설정 (EUD 코드 생성)
triggerKey = mix(triggerKey, cryptKey)     # triggerKey = mix(triggerKeyVal, cryptKeyVal)

# 9. 런타임 복호화 루프 (EUD 코드 생성)
for player in EUDLoopRange(8):
    for ptr, epd in EUDLoopList(tbegin, tend):
        decryptTrigger(epd, triggerKey)
```

## 다음 단계

1. [x] ~~Cheat Engine 스크립트 작성~~
2. [x] ~~테스트 맵 런타임 덤프~~ (`!! 저글링_키우기_ver25.scx` trigger 72 복호화)
3. [x] ~~도구 통합~~ (`--apply-dump` CLI 옵션)
4. [x] ~~armoha wlist 공식 확보~~ — euddraft 0.10.2.5 `library.zip` → `freeze/trigcrypt.pyc` 디스어셈블 → phu54321 원본과 100% 동일 확인
5. [x] ~~type-byte targeted partial decrypt 설계~~: wlist가 건드리는 dword 중 condition/action type byte만 빠르게 검사
6. [x] ~~파일 단독 triggerKey 직접 탐색 구현~~: allocation 없는 병렬 2^32 탐색 + 빠른 reject + 전체 trigger 검증
7. [x] ~~전체 encrypted TRIG 복구~~: 검증된 `triggerKey`로 모든 encrypted trigger 복호화
8. [x] ~~런타임 덤프 fallback 유지~~: Lv2 전략에서 제외. `--apply-dump`는 레거시 진단용으로만 유지
9. [ ] **Lv2 keycalc 정적 포팅**: 원본 MPQ header/hash/block/scenario sector sample을 seedKey에 피딩
10. [ ] **Lv2 oJumperArray/nextptr 복원**: `decryptOffsets()` 대상 추출 및 static patch
