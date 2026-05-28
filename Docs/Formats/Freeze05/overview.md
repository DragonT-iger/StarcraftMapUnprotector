# Freeze05 Overview

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


#### 보호 해제의 한계와 Lv2 접근


현재 Lv1 도구는 EUD 트리거를 전부 비활성화한다. 이렇게 하면:


| 항목 | Lv1 (현재) | Lv2 (목표) |

|------|-----------|-----------|

| 에디터에서 열기/편집 | ✅ 가능 | ✅ 가능 |

| 게임 정상 실행 | ❌ 불가능 | ✅ 목표 |


Lv1에서 게임이 안 되는 이유:


1. **EUD VM 전체가 죽음**: freeze EUD 트리거 = eudplib VM의 일부. VM을 죽이면

   `decryptOffsets`, `obfpatch/obfunpatch`, `encryptOffsets`, 플러그인 로직 전부 사라짐

2. **오프셋 미복구**: `decryptOffsets()`가 매 프레임 트리거 체인 nextptr을 복구하는데,

   이것도 안 되니 트리거 실행 순서 자체가 깨짐


**Lv2 접근**: EUD VM을 살려두고, trigger body를 미리 복호화한 뒤 flag에서

`0x80000000` 비트만 제거한다. 런타임 `decryptTrigger`는 `flag < 0x80000000`

체크에서 스킵하므로 이중 복호화가 발생하지 않는다. unFreeze()를 EUD 트리거

수준에서 식별할 필요 없이, trigger flag 조작만으로 자동 무력화가 가능하다.

단, MPQ 구조를 원본 그대로 보존해야 keycalc가 올바른 cryptKey를 계산할 수 있다.

(상세: "Lv2 전략 전환" 섹션 참조)


## 보호 레벨 정의


| 레벨 | 의미 | 상태 | 비고 |

|------|------|------|------|

| Lv0 | 에디터에서 열기만 가능 | ✅ 가능 | ScmDraft로 열기 |

| Lv1 | 보호 트리거 제거, 편집 가능 | ✅ 구현 완료 | EUD 비활성화 + 트리거 복호화 |

| Lv2 | Lv1 + 게임 정상 실행 | 🚧 trigger body 복호화 완료, keycalc 의존성 미해결 | chk 수정 → keycalc 입력 변화 → cryptKey/offset 깨짐 |

| Lv3 | 완전 복원 (원본과 동일) | ❌ 미착수 | Freeze 보호 흔적 완전 제거 |


**현재 상태**: trigger body 복호화 + MPQ in-place 패치는 구현되었으나, chk 수정이

keycalc 입력을 변경하여 런타임 cryptKey가 틀어지는 문제가 확인됨 (2026-05-28).

keycalc 정적 구현 또는 보상 메커니즘이 필요하다.
