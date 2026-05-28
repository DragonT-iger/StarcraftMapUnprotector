# Freeze05 Implementation Notes

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


### FreezeStaticRestorer.cs (Lv2 전용)


| 함수 | 역할 |

|------|------|

| `BuildStaticLv2Chk(...)` | Lv2 진입점: TRIG에서 triggerKey 브루트포스 → body 복호화 → CHK 재조립 |

| `ReplaceTrigSection(...)` | CHK 내 TRIG 섹션을 복호화된 데이터로 교체 |

| `RunLv2Diagnostics(...)` | Lv2 진단 출력 (trigger 분석, MPQ 구조, key 정보) |

| `PrintLv2KeycalcInputDiff(...)` | 원본 MPQ와 메모리상 Lv2 패치 MPQ의 keycalc 후보 입력 영역 비교 |

| `ComputeStaticKeycalcCandidate(...)` | `keycalc.pyc` 디스어셈블리 기반 static 후보 모델로 seed/cryptKeyVal 변화 진단 |


### FreezeKeyRecovery.cs


| 함수 | 역할 |

|------|------|

| `TryRecoverFreezeKeyByFastBruteforce(...)` | 병렬 2^32 triggerKey 탐색 (fast-pass + full-pass) |

| `FastValidateKeyAgainstAnchor(...)` | type byte 빠른 검증 (condition ≤ 23, action ≤ 63) |

| `ValidateKeyAgainstTrigger(...)` | 전체 trigger body 복호화 + 구조 검증 |

| `BuildFreezeAddSums(...)` | wlist에서 adddw 배열 생성 |


### Lv2 처리 흐름


```

Program.Main

  → BuildStaticLv2Chk(input, inputBytes, chk, stats)

    → TryGetFirstChkSection("TRIG")

    → CountEncryptedFreezeTriggers

    → TryRecoverFreezeKeyByFastBruteforce (병렬 2^32 key 탐색)

    → DecryptAllFreezeTriggers(trigData, recoveredKey, preserveLv2FlagPayload: false)

      → 각 encrypted trigger: TryDecryptFreezeTrigger + flag 복원 (& 0x0F)

    → ReplaceTrigSection(chk, trigData)

  → WriteLv2Mpq(input, outputPath, newChk, inputBytes, stats)

    → LocateScenarioTablesForPatch (MPQ 블록 테이블에서 scenario.chk 위치)

    → scenario.chk 블록만 in-place 교체 (MPQ 구조 보존)

```

### Lv2 진단 흐름

`--lv2-diag`는 출력 파일을 쓰지 않고 다음을 확인한다:

1. Freeze marker의 seed/dest key 출력
2. TRIG 내 암호화 trigger 수와 EUD SetDeaths 후보 요약
3. triggerKey 브루트포스 및 `triggerKeyVal` 역산
4. 메모리상 Lv2 패치 MPQ 생성
5. 원본 MPQ와 Lv2 패치 MPQ의 keycalc 후보 입력 영역 fingerprint/firstDiff 출력

현재 keycalc 후보 diff는 MPQ header, raw hash table, raw scenario.chk block을 비교한다.
복구된 block table 위치가 raw 파일 span으로 신뢰하기 어려운 보호 MPQ에서는 block table raw 비교를 스킵한다.

추가로 static keycalc 후보 모델을 실행해 원본 MPQ와 Lv2 패치 MPQ의 후보 seed/cryptKeyVal을 출력한다.
이 모델은 런타임 EPD 포인터 매핑이 완전히 확정되기 전까지 진단용으로만 사용한다.


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
