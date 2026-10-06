# FindEverything GUI

FindEverything 엔진으로 로컬 디스크와 공유 경로의 메타데이터를 색인하고, GUI에서 정의한 정규식 프로필로 폴더 경로를 업무 객체로 변환해 한 화면에서 조회하는 WPF 예제 앱입니다. 기존 DLL 프로필도 고급 확장 방식으로 계속 지원합니다.

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

앱에서 다음 순서로 실행합니다.

1. 기본 샘플을 사용하거나 `프로필` → `GUI 프로필 편집기`에서 새 프로필을 만듭니다.
2. `카탈로그`에서 프로필과 검색 루트를 선택합니다.
3. 색인 DB는 검색 루트 밖의 로컬 디스크 경로로 지정합니다.
4. `인덱싱`으로 색인을 갱신하거나 `불러오기`로 기존 DB만 읽습니다.
5. 정규식에 맞는 폴더가 프로필 필드별 열로 표시됩니다.
6. `결과 필터`에 값을 입력하면 모든 표시 컬럼과 폴더 경로에서 일치하는 행만 남고, 일치 부분은 노란색으로 강조됩니다.
7. 행을 더블클릭하거나 `선택 폴더 열기`를 눌러 Explorer에서 경로를 엽니다.

사용자 설정은 `%LOCALAPPDATA%\UCode\FindEverything.Gui\appsettings.user.json`에 원자적으로 저장됩니다. 환경 변수는 `FINDEVERYTHING_` 접두사를 사용합니다.

## 샘플 데이터 확인

기본 프로필이 실제 폴더를 어떻게 객체로 바꾸는지 확인할 수 있도록
`samples\SampleData\sample-projects`에 예제 폴더를 포함합니다. 소스에서 실행할
때는 이 경로를 사용하고, publish 결과에서는 EXE 옆의
`SampleData\sample-projects`를 사용하면 됩니다.

1. 프로필에서 `샘플 프로젝트 폴더`를 선택합니다.
2. 검색 루트로 바로 아래에 `Clients`가 있는 `sample-projects` 폴더를 선택합니다.
3. 인덱스 DB는 검색 루트 밖의 경로로 지정합니다.
4. `인덱싱`을 누르면 고객, 프로젝트, 연도와 선택 필드가 동적 열로 표시됩니다.

현재 규칙은 연도 뒤의 수집일, 리비전, 승인, 금액 폴더가 모두 선택 항목입니다.
따라서 깊은 예제 경로의 중간 폴더도 각각 완전한 규칙에 일치하며, 제공된 3개
leaf 예제로부터 총 10개의 행이 표시되는 것이 정상입니다. 자세한 예상 결과는
[`samples/SampleData/sample-projects/README.md`](samples/SampleData/sample-projects/README.md)를 참고하세요.

## 프로필 추가

권장 방식은 앱의 `프로필` → `GUI 프로필 편집기`입니다.

1. `새로 만들기`를 누르고 표시 이름, 프로필 ID, 경로 입력 기준을 정합니다.
2. 정규식 named group과 연결할 결과 컬럼을 추가합니다.
3. 경로 규칙을 추가하고 실제 예제 경로로 `경로 시험`을 실행합니다.
4. `검증 후 저장`을 누릅니다.

저장에 성공하면 프로필 카탈로그가 즉시 갱신되므로 앱을 재시작할 필요가 없습니다. GUI 프로필은 다음 사용자 전용 위치에 선언형 `profile.json`으로 저장됩니다.

```text
%LOCALAPPDATA%\UCode\FindEverything.Gui\Profiles\<profile-id>\profile.json
```

프로필 ID, 필드 타입, 정규식 그룹, 입력/표시 형식과 제한 시간의 정확한 허용값은 [프로필 작성 가이드](docs/profile-authoring.md)를 참고하세요. 기존 프로필의 해석 결과와 비활성화 원인은 `프로필` → `로드 결과`에서 확인할 수 있습니다.

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
