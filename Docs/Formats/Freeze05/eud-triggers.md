# Freeze05 EUD Protection Triggers

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
