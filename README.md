# StarcraftMapUnprotector

StarCraft 맵 파일의 보호 구조를 정리하고, 가능한 경우 `scenario.chk`를 복구해 다시 열 수 있는 형태의 MPQ 맵 파일로 저장하는 도구입니다.

## 다운로드

Windows용 실행 파일은 GitHub Releases에서 받을 수 있습니다.

- [v1.0.0 다운로드](https://github.com/DragonT-iger/StarcraftMapUnprotector/releases/tag/v1.0.0)
- 다운로드 파일: `StarcraftMapUnprotector-v1.0.0-win.zip`

## 사용 방법

1. zip 파일의 압축을 풉니다.
2. 언프로텍트할 원본 맵 파일을 `Maps\Originals` 폴더에 넣습니다.
   - 지원 파일: `.scx`, `.scm`
3. `StarcraftMapUnprotector.exe`를 실행합니다.
4. 변환된 파일은 `Maps\Outputs` 폴더에 생성됩니다.

원본 파일은 덮어쓰지 않습니다. 중요한 맵은 그래도 별도로 백업해 둔 뒤 사용하는 것을 권장합니다.

## 고급 사용

파일을 직접 지정하거나 자동화 스크립트에서 사용할 때는 명령줄 인수를 활용할 수 있습니다.

```powershell
StarcraftMapUnprotector.exe input.scx output.scx
StarcraftMapUnprotector.exe input.scx output.scx --no-pause
```

`--no-pause`를 붙이면 작업 완료 후 Enter 입력 없이 바로 종료됩니다.

## 사용 시 변질될 수 있는 정보

이 프로그램은 보호 해제와 정상 실행을 위해 맵 내부 데이터를 정리/복구합니다. 따라서 결과 파일은 원본과 완전히 동일한 내부 구조를 보존하지 않을 수 있습니다.

- 보호용 SMLP 섹션 및 가짜/중복 섹션 정보
- 중복된 CHK 섹션 중 선택되지 않은 섹션 데이터
- 가짜 UNIT 레코드, 가짜 TRIG 레코드
- 트리거 주석 및 일부 트리거 문자열 참조
- 잘못된 위치(Location) 번호나 문자열 번호 참조
- 문자열 테이블(`STR `)의 순서, 빈 문자열, 사용되지 않는 문자열 정보
- 지형 관련 섹션(`MTXM`, `TILE`, `ISOM`, `MASK`)의 후보 선택 또는 복구 결과
- 플레이어/세력/소유권 등 일부 기본값이 비정상일 때 보정된 값
- MPQ 내부 테이블, 파일 순서, 압축 방식, 해시/블록 테이블 구조
- 원본 보호 방식이나 보호 툴이 남긴 메타데이터

맵 플레이에 필요한 정보 복구를 우선하므로, 원본 보호 구조나 편집기 표시 정보가 일부 달라질 수 있습니다.

## 기여 방법

기여는 이슈와 Pull Request로 받습니다.

### 이슈 등록

버그나 개선 아이디어가 있다면 GitHub Issues에 남겨주세요. 가능하면 아래 정보를 함께 적어주면 원인을 찾는 데 도움이 됩니다.

- 사용한 버전 또는 커밋
- 실행한 명령어
- 기대한 결과와 실제 결과
- 콘솔에 출력된 오류 메시지
- 문제가 발생한 맵의 종류와 확장자

GitHub Issues 외에도 아래 연락처로 개선사항을 보내주셔도 됩니다.

- 디스코드: `dtdt__`
- 이메일: `dydyqja12345@naver.com`

맵 파일에 저작권이나 개인 정보가 들어 있을 수 있으므로, 공개 이슈에 업로드하기 전에 공유해도 되는 파일인지 먼저 확인해 주세요.

### 빌드 참고

직접 빌드할 때는 `TkMPQLib.dll`이 32비트 전용이라는 점에 주의해 주세요. 실행 파일도 x86으로 빌드해야 합니다.

```powershell
C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe /nologo /target:exe /platform:x86 /out:StarcraftMapUnprotector.exe /reference:TkMPQLib.dll StarcraftMapUnprotector.cs
StarcraftMapUnprotector.exe --help --no-pause
```
