# Freeze05 Research Log

## 다음 단계


1. [x] ~~Cheat Engine 스크립트 작성~~

2. [x] ~~테스트 맵 런타임 덤프~~ (`!! 저글링_키우기_ver25.scx` trigger 72 복호화)

3. [x] ~~도구 통합~~ (`--apply-dump` CLI 옵션)

4. [x] ~~armoha wlist 공식 확보~~ — euddraft 0.10.2.5 `library.zip` → `freeze/trigcrypt.pyc` 디스어셈블 → phu54321 원본과 100% 동일 확인

5. [x] ~~type-byte targeted partial decrypt 설계~~: wlist가 건드리는 dword 중 condition/action type byte만 빠르게 검사

6. [x] ~~파일 단독 triggerKey 직접 탐색 구현~~: allocation 없는 병렬 2^32 탐색 + 빠른 reject + 전체 trigger 검증

7. [x] ~~전체 encrypted TRIG 복구~~: 검증된 `triggerKey`로 모든 encrypted trigger 복호화

8. [x] ~~런타임 덤프 fallback 유지~~: Lv2 전략에서 제외. `--apply-dump`는 레거시 진단용으로만 유지

9. [x] ~~Lv2 전략 전환~~: 정적 keycalc/offset 복원 → EUD VM 보존 + unFreeze 무력화로 전환

10. [x] ~~flag 기반 자동 무력화 검증~~: `DecryptAllFreezeTriggers(trigData, recoveredKey, false)`로 flag에서 0x80000000 제거. 런타임 decryptTrigger의 `flag < 0x80000000` 체크에서 자동 스킵 확인.

11. [x] ~~Lv2 모드 MPQ in-place 패치 구현~~: `BuildStaticLv2Chk()` → `WriteLv2Mpq()`로 원본 MPQ 내 scenario.chk 블록만 교체. MPQ 구조 100% 보존.

12. [!] **Lv2 게임 실행 검증 실패**: 맵은 로딩되지만 트리거가 전혀 동작하지 않는 증상 확인.
    `lv2-strategy.md`의 "인게임 검증 실패" 기록이 최신 상태이다.


### Lv2 테스트맵 결과 (2026-05-28)


| 맵 | 전체 트리거 | 암호화 트리거 | triggerKey | 브루트포스 | 상태 |

|-----|-----------|-------------|-----------|-----------|------|

| 간단한 강화기.scx | 140 | 14 | 0x4D1E5744 | ~5초 | 복호화 성공, Lv2 인게임 트리거 무반응 |

| [EUD] 망할 마린2 2.5.scx | 71 | 0 | N/A | N/A | 암호화 트리거 없음 (EUD-only) |

| 적은자원으로 살아남기EUD [4.3V].scx | 243 | 39 | 0xCA27FC03 | ~30초 (65%) | 복호화 성공, Lv2 인게임 트리거 무반응 |

### Lv2 인게임 실패 증상

최신 관찰: Lv2 출력 맵은 게임에서 로딩되지만, 트리거가 전혀 동작하지 않는 상태에 가깝다.
이는 MPQ/CHK가 로딩 단계에서 깨지는 문제라기보다, 런타임 트리거 체인 또는 EUD VM 내부 키 계산이
깨진 증상으로 본다.

현재 가장 유력한 원인:

1. scenario.chk 내용 변경으로 keycalc 입력 스트림이 원본과 달라진다.
2. 런타임 cryptKey가 원본 빌드 시점과 달라진다.
3. `decryptOffsets()`가 잘못된 key로 nextptr 체인을 복호화한다.
4. 결과적으로 일반 트리거 실행 순서가 복원되지 않아, 맵은 로딩되지만 트리거가 실행되지 않는다.

### Lv2 keycalc 입력 후보 diff (2026-05-28)

`--lv2-diag`에 원본 MPQ와 메모리상 Lv2 패치 MPQ의 keycalc 후보 입력 영역 비교를 추가했다.
현재 진단은 MPQ header, raw hash table, raw scenario.chk block을 비교한다. 보호 MPQ에서
복구된 block table 위치가 raw 파일 span으로 신뢰하기 어려운 경우는 스킵한다.

관찰 결과:

| 맵 | header | hash table | scenario raw block | scenario sector 변화 |
|----|--------|------------|--------------------|----------------------|
| 간단한 강화기.scx | unchanged | unchanged | changed, firstDiff +0xE0 | 16 / 509 sectors |
| 적은자원으로 살아남기EUD [4.3V].scx | unchanged | unchanged | changed, firstDiff +0x118 | 44 / 3080 sectors |

결론: Lv2 in-place 패치는 MPQ header/hash table을 유지하지만, trigger body 복호화가 들어간
scenario.chk compressed/encrypted payload 일부 sector를 바꾼다. 따라서 현재 실패 원인은
MPQ 외부 구조 변경이 아니라, keycalc가 읽는 scenario.chk sector payload 변화로 보는 것이 맞다.

### Static keycalc 후보 모델 결과 (2026-05-28)

`keycalc_disasm_full.txt`의 바이트코드 흐름을 바탕으로 진단용 static keycalc 후보 모델을 추가했다.
이 모델은 header 8 dword, hash table dword, scenario sector sample, block table walk 후보를
`feedSample()` 형태로 seedKey에 섞고 최종 `ComputeCryptKeyVal()`을 계산한다.

주의: 보호 MPQ의 런타임 EPD 포인터와 raw 파일 offset 대응은 아직 완전 확정이 아니므로,
이 값은 oracle이 아니라 “원본/패치 입력 변화가 keycalc 결과를 흔드는지” 보는 진단값이다.

| 맵 | 원본 후보 cryptKeyVal | Lv2 패치 후보 cryptKeyVal | 결과 |
|----|------------------------|----------------------------|------|
| 간단한 강화기.scx | 0x94AF4F5C | 0x4B00ED71 | changed |
| 적은자원으로 살아남기EUD [4.3V].scx | 0xA1D0CA2F | 0xEAB71865 | changed |

후보 모델에서도 Lv2 패치 후 keycalc 결과가 달라진다. 따라서 다음 단계는
런타임 seedKey 초기 할당을 보상하거나, keycalc 의존성을 제거할 수 있도록 offset 복원을
정적으로 처리하는 쪽이다.


### GetFreezeCryptFlag `& 0x7FFFF000` 마스크 분석


`GetFreezeCryptFlag(encryptedFlag)`는 wlist 생성에 쓰이는 flag 값을 추출한다:


```csharp

return unchecked(encryptedFlag - 0x80000000u) & 0x7FFFF000u;

```


**Python 소스와의 불일치**: `trigcrypt.pyc` 디스어셈블리에서 `encryptTrigger`의

flag 계산은 `flag = flag + 0x80000000 + (r & 0x7FFFF000)`이고,

`decryptTrigger`에서는 `flag -= 0x80000000` 후 마스크 없이 그대로 `mix2(key, flag)`에

전달한다. 즉 Python 소스상으로는 마스크가 불필요해 보인다.


**그러나 실증적으로 마스크가 필수**:

- 마스크 제거 시: 2^32 전체 탐색에서 1045 fast-pass, 0 full-pass → **실패**

- 마스크 유지 시: 344 fast-pass, 1 full-pass → **성공** (key 0xCA27FC03 검증)


**원인 분석**: 암호화 시 `flag = original_exec_flags + 0x80000000 + (r & 0x7FFFF000)`

이므로, `flag - 0x80000000 = original_exec_flags + (r & 0x7FFFF000)`. 여기서

original_exec_flags는 하위 4비트 (관찰값: 항상 0x08). 마스크 `& 0x7FFFF000`은

이 하위 비트를 제거하여 `(r & 0x7FFFF000)` 부분만 남긴다. Python 런타임에서는

exec_flags가 mix2 입력에 포함되어도 EUD 연산의 32비트 overflow 특성으로 동일 결과가

나올 수 있지만, C# 브루트포스에서는 정확한 flag 값이 필요하므로 마스크가 필수.


**결론**: `& 0x7FFFF000` 마스크는 C# 구현에서 반드시 유지해야 한다.


### exec_flags 관찰 결과


테스트맵에서 관찰된 모든 암호화 트리거의 원래 exec_flags(하위 4비트)는 `0x08`이었다.


`0x08` = bit 3 = "Disabled" 또는 "Already Evaluated" 플래그.

이는 빌드 시점에 `encryptTrigger`가 호출되기 전 트리거의 초기 상태가 exec_flags=8임을 의미한다.
