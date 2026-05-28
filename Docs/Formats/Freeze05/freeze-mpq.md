# Freeze05 freezeMpq.pyd Analysis

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
