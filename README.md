# FindEverything GUI

FindEverything 엔진으로 로컬 디스크와 공유 경로의 메타데이터를 색인하고, GUI에서 정의한 경로 템플릿이나 정규식 프로필로 폴더 경로를 업무 객체로 변환해 한 화면에서 조회하는 WPF 예제 앱입니다. 기존 DLL 프로필도 고급 확장 방식으로 계속 지원합니다.

## 구성

- `.NET 10`, WPF, Generic Host, Microsoft DI, `IOptions`
- LePO.CO `WPF-UI` 기반 Fluent shell
- `external/FindEverything`에 고정한 엔진 Git submodule
- 코드나 DLL 없이 GUI에서 작성하는 선언형 프로필
- 필요할 때 Host와 분리해 배포할 수 있는 런타임 프로필 DLL
- 정규식 named group에서 선언형 필드 또는 attribute 기반 POCO로 변환되는 동적 표

의존성 방향은 다음과 같습니다.

```text
Desktop ──> Application <── Infrastructure.FindEverything ──> Engine submodule
   │              │
   └──────> Profile.Runtime ──> declarative profile.json
                    └────────> Profile.Abstractions <── optional profile DLL
```

`Desktop`만 WPF와 WPF-UI를 참조합니다. 선언형 프로필은 코드 참조가 전혀 없고, DLL 프로필 프로젝트는 `Profile.Abstractions`만 참조하며 Host DI에 자신의 타입을 등록하지 않습니다.

## 빌드 및 실행

Windows 10/11과 .NET SDK 10.0.401 이상이 필요합니다.

처음 받을 때는 엔진 submodule까지 함께 clone합니다.

```powershell
git clone --recurse-submodules https://github.com/u-code-1029/FindEverything.Gui.git
cd .\FindEverything.Gui
```

이미 submodule 없이 clone했다면 다음 명령으로 엔진을 받습니다. 이후 복원, 빌드, 테스트 및 실행 순서는 아래와 같습니다.

```powershell
git submodule update --init --recursive
dotnet restore .\FindEverything.Gui.slnx --locked-mode
dotnet build .\FindEverything.Gui.slnx -c Release --no-restore
dotnet test .\FindEverything.Gui.slnx -c Release --no-build
dotnet run --project .\src\FindEverything.Desktop -c Release
```

배포용 self-contained 패키지는 별도의 RID 복원 후 생성합니다. 일반 복원은 저장소의 portable lock file을 검증하고, 이 RID 복원은 각 프로젝트의 `obj` 아래에 publish 전용 lock file을 만들므로 소스 트리와 고정된 엔진 submodule을 변경하지 않습니다.

```powershell
dotnet restore .\src\FindEverything.Desktop\FindEverything.Desktop.csproj -r win-x64 -p:NuGetLockFilePath=obj\packages.win-x64.lock.json
dotnet publish .\src\FindEverything.Desktop\FindEverything.Desktop.csproj -c Release -r win-x64 --self-contained true --no-restore -o .\artifacts\win-x64
```

앱에는 서로 독립적인 두 탐색 흐름이 있습니다.

1. `파일 찾기`에서 검색할 폴더나 드라이브를 선택하고 `인덱스 새로 고침`을 누릅니다. 이 화면만 공유 SQLite 인덱스를 만들고 갱신합니다.
2. 상단 검색창에 여러 단어를 입력하면 파일명 또는 전체 경로에 모든 단어가 있는 항목만 남고, 일치 부분은 노란색으로 강조됩니다.
3. 종류, 크기, 생성일, 수정일 필터와 각 결과 열의 정렬을 사용합니다. 행을 더블클릭하면 항목이 열리고, 우클릭 메뉴에서 Explorer 위치 열기와 경로 복사를 할 수 있습니다.
4. 경로를 업무 데이터로 해석하려면 `구조화 보기`에서 프로필과 검색 위치를 선택한 뒤 기본 동작인 `프로필로 빠르게 불러오기`를 실행합니다. 폴더를 직접 훑으면서 즉시 규칙을 적용하며 공유 SQLite 인덱스를 열거나 변경하지 않습니다.
5. 빠른 불러오기를 시작하면 하단 `탐색 로그` 패널이 열립니다. 제목 표시줄 오른쪽의 패널 버튼으로 언제든 접거나 다시 열 수 있습니다. 각 방문 폴더의 원래 경로(`full`)와 프로필에 전달된 절대/UNC 경로(`input`), `MATCH`, `NO MATCH`, `INVALID`, 적용 규칙, 변환 값, `PRUNE` 여부를 확인할 수 있으며 패널을 접어도 현재 로그는 유지됩니다.
6. 이미 만들어 둔 인덱스로 구조화 결과를 다시 읽고 싶을 때만 `고급: 기존 인덱스에서 불러오기`를 사용합니다. 이 동작도 인덱스를 갱신하지 않습니다.
7. 구조화 결과의 검색창은 표시된 모든 컬럼과 폴더 경로를 함께 검색하며, 공백으로 나눈 모든 단어가 일치해야 합니다.

기본 인덱스 DB는 사용자 로컬 데이터 폴더에 생성됩니다. 엔진 안전 규칙상 DB는 검색 루트 밖의 로컬 디스크에 있어야 합니다. 검색 루트와 겹치면 `파일 찾기`의 `인덱스 위치`에서 다른 위치를 지정하세요. `구조화 보기`의 빠른 스캔에는 DB가 필요하지 않으며, 고급 기존 인덱스 불러오기에서만 DB 위치를 사용합니다.

프로필 정규식과 경로 템플릿에는 검색 위치에 대한 상대 경로가 아니라 절대 경로만 전달됩니다. 로컬 디스크는 드라이브 문자가 포함된 경로를 사용하고, 매핑된 네트워크 드라이브는 규칙 적용 전에 실제 UNC 경로로 확장합니다. 예를 들어 `Z:`가 `\\192.168.10.20\share`에 연결되어 있으면 프로필에는 IP가 보존된 `\\192.168.10.20\share\...`가 입력됩니다. 검색 위치는 방문할 범위만 제한하므로 같은 폴더를 더 상위 또는 더 하위 루트에서 스캔해도 프로필의 입력값은 바뀌지 않습니다.

사용자 설정은 `%LOCALAPPDATA%\UCode\FindEverything.Gui\appsettings.user.json`에 원자적으로 저장됩니다. 환경 변수는 `FINDEVERYTHING_` 접두사를 사용합니다.

## 샘플 데이터 확인

기본 프로필이 실제 폴더를 어떻게 객체로 바꾸는지 확인할 수 있도록
`samples\SampleData\sample-projects`에 예제 폴더를 포함합니다. 소스에서 실행할
때는 이 경로를 사용하고, publish 결과에서는 EXE 옆의
`SampleData\sample-projects`를 사용하면 됩니다.

1. `구조화 보기`에서 바로 아래에 `Clients`가 있는 `sample-projects` 폴더를 검색 위치로 선택합니다.
2. `샘플 프로젝트 폴더` 프로필을 선택합니다.
3. `프로필로 빠르게 불러오기`를 누르면 고객, 프로젝트, 연도와 선택 필드가 동적 열로 표시됩니다. 샘플 확인을 위해 파일 인덱스를 먼저 만들 필요는 없습니다.

샘플 프로필은 전체 경로를 입력받되 `/`와 `\`를 모두 허용하고 `Clients` 이하의 경로 접미사를 부분 일치시키므로, 샘플 폴더가 어느 절대 경로에 있어도 같은 결과를 만듭니다.

현재 규칙은 연도 뒤의 수집일, 리비전, 승인, 금액 폴더가 모두 선택 항목입니다.
따라서 깊은 예제 경로의 중간 폴더도 각각 완전한 규칙에 일치하며, 제공된 3개
leaf 예제로부터 총 10개의 행이 표시되는 것이 정상입니다. 자세한 예상 결과는
[`samples/SampleData/sample-projects/README.md`](samples/SampleData/sample-projects/README.md)를 참고하세요.

## 프로필 추가

권장 방식은 앱의 `프로필` → `프로필 편집`입니다.

처음 만드는 경우에는 초보자 모드가 가장 간단합니다.

1. `새로 만들기`를 누르고 표시 이름과 프로필 ID를 정합니다. 프로필 입력은 항상 실제 전체 경로입니다.
2. 먼저 결과 값 카드를 추가하고 이름, 값 종류, 필수 여부를 정합니다. 날짜는 `한 조각`, `연도 + 월일`, `연도 + 월 + 일` 중 실제 폴더 구조에 맞는 방식을 고릅니다.
3. 실제 예제 경로를 붙여 넣고 `경로 분석`을 누릅니다. 경로는 `/`와 `\`로 폴더 버튼이 되고, 각 폴더명은 `_`, `-`, 공백을 기준으로 더 작은 조각 버튼도 제공합니다.
4. 결과 값 카드에서 `경로 조각 선택`을 누른 다음 해당 폴더 또는 세부 조각 버튼을 누릅니다. 날짜가 나뉘어 있으면 연도, 월일 또는 월·일 버튼을 차례로 연결합니다.
5. 필요하면 완성 값을 `이 값까지 찾으면 하위 폴더 건너뛰기`로 지정하고, `검증`과 `이 경로로 결과 확인`을 거쳐 `검증 후 저장`을 누릅니다.

초보자 모드는 필드에서 시작해 버튼으로 조각을 연결하는 방식입니다. 드래그하거나 만들어진 경로 규칙을 직접 편집하지 않으며, 규칙 미리보기는 읽기 전용입니다.

예를 들어 다음 전체 경로 템플릿은 마지막 리비전 폴더가 없어도 일치합니다.

```text
C:/Archive/Clients/{customer@customer}/Projects/{project@project}/{year@year}/{revision@revision?}
```

템플릿에서는 `/`를 구분자로 쓰며 실제 경로의 `/`와 `\`를 모두 일치시킵니다. `{fieldId@groupName}`은 필드와 실제 정규식 캡처를 연결하고, 선택 토큰은 `{revision@revision?}`처럼 끝에 이어지는 경로 구간에 사용합니다. 그 구간에는 필드 토큰을 하나만 넣을 수 있습니다. 리터럴 중괄호는 `{{`와 `}}`로 씁니다. 더 복잡한 규칙이나 여러 규칙의 우선순위가 필요하면 전문가 모드에서 필드의 단일·복합 그룹, 정규식과 탐색 중단 그룹을 직접 작성할 수 있으며, 기존 정규식 프로필도 계속 사용할 수 있습니다.

저장에 성공하면 프로필 카탈로그가 즉시 갱신되므로 앱을 재시작할 필요가 없습니다. GUI 프로필은 다음 사용자 전용 위치에 선언형 `profile.json`으로 저장됩니다.

```text
%LOCALAPPDATA%\UCode\FindEverything.Gui\Profiles\<profile-id>\profile.json
```

토큰 문법, 선택 구간 제한, 프로필 ID, 필드 타입, 정규식 그룹, 입력/표시 형식과 제한 시간의 정확한 허용값은 [프로필 작성 가이드](docs/profile-authoring.md)를 참고하세요. 기존 프로필의 해석 결과와 비활성화 원인은 `프로필` → `로드 상태`에서 확인할 수 있습니다.

코드 기반 변환이 꼭 필요하면 기존 DLL 프로필도 사용할 수 있습니다. 예시는 `samples/FindEverything.Profiles.SampleProjects`에 있으며, 배포 폴더 구조는 다음과 같습니다.

```text
Profiles/<profile-id>/
  profile.json
  <profile assembly>.dll
  <private dependencies>.dll
```

DLL 프로젝트는 `FindEverything.Profile.Abstractions`만 참조하고 public POCO 속성에 `CaptureFieldAttribute`를 붙입니다. DLL 플러그인은 보안 sandbox가 아니므로 신뢰할 수 있는 파일만 배치해야 합니다.

GUI 밖에서 DLL이나 manifest를 직접 바꾼 경우에는 앱을 재시작해야 합니다. GUI의 `검증 후 저장`은 저장할 파일을 같은 런타임 규칙으로 다시 검증한 뒤 현재 카탈로그에 즉시 적용합니다.

## 현재 POC 한계

- 검색 결과는 메모리에 모으므로 프로필 후보 폴더 약 10만 개 미만을 목표로 합니다.
- offset 페이지 전체를 읽는 동안 이 프로세스의 scan/search는 직렬화합니다.
- SMB 호출이 운영체제에서 반환될 때까지 취소가 늦어질 수 있습니다.
- Windows 7은 .NET 10 런타임 지원 범위가 아니므로 이 POC에서 지원하지 않습니다.
