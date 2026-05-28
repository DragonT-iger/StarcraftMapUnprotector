# Freeze05 armoha Module Extraction

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
