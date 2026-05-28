# Freeze05 Keys and File Structure

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
