# Freeze05 Trigger Crypto

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
