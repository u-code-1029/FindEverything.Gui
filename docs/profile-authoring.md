# 프로필 작성 가이드

프로필은 폴더 경로에서 정규식 named group을 추출해 결과 표의 한 행으로 바꾸는 규칙입니다. 대부분의 프로필은 C# 프로젝트나 DLL 없이 앱의 `프로필` → `프로필 편집`에서 만드는 방식을 권장합니다. DLL 프로필은 코드 기반 모델이 꼭 필요한 경우를 위한 호환·고급 기능입니다.

## GUI에서 선언형 프로필 만들기

예를 들어 다음 상대 경로를 고객, 프로젝트, 연도, 리비전으로 나눈다고 가정합니다.

```text
Clients\Acme\Projects\PRJ-042\2026\Rev-3
```

1. `새로 만들기`를 누르고 표시 이름과 프로필 ID를 입력합니다.
2. 경로 입력은 보통 `검색 루트 기준 상대 경로`를 선택합니다.
3. 결과 컬럼에 `customer`, `project`, `year`, `revision` 필드를 추가하고 같은 이름의 정규식 그룹을 연결합니다.
4. 경로 규칙에 다음 pattern을 입력합니다. `전체 일치`가 선택되어 있으면 `^`와 `$`를 따로 붙이지 않아도 됩니다.

   ```regex
   Clients\\(?<customer>[^\\]+)\\Projects\\(?<project>[^\\]+)\\(?<year>\d{4})(?:\\Rev-(?<revision>\d+))?
   ```

5. 위 예제 경로를 붙여 넣고 `경로 시험`을 누릅니다. 일치한 규칙, 추출값, 변환 오류를 저장 전에 확인할 수 있습니다.
6. `검증 후 저장`을 누릅니다. 임시 저장 파일까지 같은 런타임 규칙으로 검증되면 새 프로필이 카탈로그에 즉시 나타나며 앱을 재시작할 필요가 없습니다.

인덱싱이나 기존 DB 불러오기가 실행 중일 때는 작업이 끝난 뒤 저장해야 합니다. 한 번 저장한 프로필의 ID는 편집 화면에서 바꿀 수 없으므로 다른 ID가 필요하면 새 프로필을 만드세요.

## GUI 입력값과 검증 규칙

### 기본 정보

| 항목 | 허용값과 동작 |
| --- | --- |
| 표시 이름 | 공백이 아닌 문자열이어야 합니다. |
| 프로필 ID | 정규식 `[a-z0-9][a-z0-9.-]{0,63}`을 만족해야 합니다. 소문자 영숫자로 시작하고 소문자 영숫자, 점, 하이픈만 쓸 수 있으며 최대 64자입니다. 대소문자를 무시해 다른 프로필과 중복될 수 없습니다. |
| 경로 입력 | `Relative`는 사용자가 선택한 검색 루트 기준 상대 경로, `Full`은 드라이브나 UNC 루트를 포함한 전체 경로를 규칙에 전달합니다. GUI에는 각각 `검색 루트 기준 상대 경로`, `드라이브를 포함한 전체 경로`로 표시됩니다. |
| 후보 종류 | 현재는 폴더를 뜻하는 `Directory`만 지원하며 GUI가 자동으로 지정합니다. |
| 계약·종류·버전 | GUI가 `contractVersion: 1`, `kind: Declarative`, `version: 1.0.0`을 자동으로 지정합니다. 런타임은 버전 문자열이 비어 있는지만 검사하며 SemVer 형식까지 강제하지는 않습니다. |

### 결과 컬럼

필드는 하나 이상 필요합니다. GUI의 위·아래 버튼으로 정한 순서가 결과 열 순서가 되며, 저장할 때 `order`가 10 단위로 지정됩니다.

| 항목 | 허용값과 동작 |
| --- | --- |
| 표시 이름 | 결과 표의 열 제목입니다. 비워 두면 런타임은 Field ID를 사용하지만, 알아보기 쉬운 이름을 입력하는 편이 좋습니다. |
| Field ID | 정규식 `[a-z0-9][a-z0-9._-]{0,63}`을 만족해야 합니다. 소문자 영숫자로 시작하고 소문자 영숫자, 점, 밑줄, 하이픈만 쓸 수 있으며 최대 64자입니다. 프로필 안에서 대소문자를 무시해 고유해야 합니다. 저장된 열 설정의 안정적인 키이므로 가능하면 변경하지 마세요. |
| 정규식 그룹 | 정규식 `[A-Za-z_][A-Za-z0-9_]*`을 만족해야 합니다. 문자 또는 밑줄로 시작하고 이후에는 영숫자와 밑줄만 쓸 수 있습니다. 규칙의 `(?<groupName>...)` 이름과 정확히 연결됩니다. |
| 필수 | 선택하면 그룹이 규칙에 없거나, 일치하지 않거나, 값이 공백이거나, 타입 변환에 실패한 경로가 `Invalid`가 됩니다. 선택하지 않은 필드의 그룹이 없거나 비어 있으면 값은 `null`입니다. |
| 입력 형식 | `DateTime`에만 사용할 수 있는 .NET 날짜/시간 형식입니다. 예: `yyyyMMdd`. 지정하면 invariant culture의 정확한 형식으로만 변환합니다. 다른 타입에 입력하거나 형식 자체가 잘못되면 검증 오류입니다. |
| 표시 형식 | `Int32`, `Decimal`, `DateTime`에만 사용할 수 있는 유효한 .NET 표준/사용자 지정 형식입니다. 예: `N0`, `N2`, `yyyy-MM-dd`. `String`과 `Boolean`에는 비워 두어야 합니다. 표시 형식은 화면 표현만 바꾸고 저장된 값의 타입은 바꾸지 않습니다. |

지원 값 타입과 변환 규칙은 다음과 같습니다. 모든 변환은 언어·지역 설정과 무관한 invariant culture를 사용합니다.

| GUI 표시 | manifest 값 | 입력 예 | 비고 |
| --- | --- | --- | --- |
| 텍스트 | `String` | `Acme` | 캡처한 문자열을 그대로 사용합니다. |
| 정수 | `Int32` | `2026`, `-3` | 32비트 정수 범위여야 합니다. |
| 소수 | `Decimal` | `1234.50` | invariant culture 숫자 형식을 사용하므로 소수점은 `.`입니다. |
| 날짜/시간 | `DateTime` | `20261007` | 입력 형식이 있으면 `TryParseExact`, 없으면 invariant culture 날짜 파싱을 사용합니다. |
| 참/거짓 | `Boolean` | `true`, `false` | 대소문자를 구분하지 않는 Boolean 문자열이어야 합니다. |

### 경로 규칙

규칙은 하나 이상 필요합니다. GUI의 위·아래 순서대로 평가하며 첫 번째로 일치한 규칙이 그 경로를 소유합니다. 첫 규칙의 필드 변환이 실패하거나 정규식 시간이 초과되어도 다음 규칙으로 넘어가지 않습니다.

| 항목 | 허용값과 동작 |
| --- | --- |
| 규칙 ID | 정규식 `[A-Za-z0-9][A-Za-z0-9._-]{0,63}`을 만족하는 최대 64자 문자열입니다. 영숫자로 시작하고 이후에는 영숫자, 점, 밑줄, 하이픈을 쓸 수 있으며 대소문자를 무시해 고유해야 합니다. 비워 두면 런타임이 선언 순서에 따라 `rule-N`을 사용하지만 명시적으로 입력하는 것을 권장합니다. |
| Pattern | 공백이 아닌 유효한 .NET 정규식이어야 합니다. 필수 필드의 named group은 모든 규칙에 정의되어 있어야 합니다. 선택 필드의 group은 일부 규칙에서 생략할 수 있습니다. |
| 매칭 | `Full`은 입력 전체가 일치해야 하며 런타임이 절대 시작·끝 anchor를 추가합니다. `Partial`은 입력 중 일부만 일치해도 됩니다. |
| 대소문자 무시 | 선택하면 .NET `RegexOptions.IgnoreCase`를 사용합니다. 두 경우 모두 culture-invariant 정규식으로 컴파일됩니다. |
| 제한 시간 | `1`~`10000`ms의 정수여야 합니다. 시간 초과는 해당 경로를 `Invalid`로 처리합니다. 기본값은 `100`ms입니다. |

Windows 폴더 구분자 `\` 하나를 정규식으로 일치시키려면 GUI pattern에는 `\\`를 입력합니다. JSON을 직접 편집할 때는 JSON escape까지 필요하므로 같은 부분이 `\\\\`로 보입니다.

## 저장 위치와 즉시 적용

GUI로 만든 프로필은 사용자별 폴더에 저장됩니다.

```text
%LOCALAPPDATA%\UCode\FindEverything.Gui\Profiles\<profile-id>\profile.json
```

`프로필 폴더 열기` 버튼으로 이 위치를 바로 열 수 있습니다. 앱은 먼저 임시 파일을 만들고 그 파일을 다시 읽어 검증한 다음 `profile.json`을 원자적으로 교체합니다. 검증된 프로필만 실행 중인 카탈로그에 반영하므로 다른 DLL 프로필은 다시 로드하거나 제거하지 않습니다. 여러 앱 창의 동시 저장은 프로필별 잠금으로 막고, 저장 시 확인된 외부 파일 변경은 덮어쓰지 않은 채 다시 열도록 안내합니다.

앱과 함께 배포된 프로필이나 DLL 프로필은 GUI 편집 목록에 나타나지 않습니다. 이들은 `로드 결과`에서 읽기 전용으로 확인할 수 있습니다. 파일을 GUI 밖에서 직접 수정했다면 자동 감시하지 않으므로 앱을 재시작해야 합니다.

## 선언형 profile.json 예제

GUI가 저장하는 선언형 프로필은 다음 구조입니다. 보통 직접 작성할 필요는 없지만 버전 관리나 문제 진단에 사용할 수 있습니다.

```json
{
  "contractVersion": 1,
  "kind": "Declarative",
  "id": "project-folders",
  "version": "1.0.0",
  "displayName": "프로젝트 폴더",
  "fields": [
    {
      "fieldId": "customer",
      "groupName": "customer",
      "header": "고객",
      "order": 10,
      "required": true,
      "kind": "String",
      "parseFormat": null,
      "displayFormat": null
    },
    {
      "fieldId": "project",
      "groupName": "project",
      "header": "프로젝트",
      "order": 20,
      "required": true,
      "kind": "String",
      "parseFormat": null,
      "displayFormat": null
    },
    {
      "fieldId": "year",
      "groupName": "year",
      "header": "연도",
      "order": 30,
      "required": true,
      "kind": "Int32",
      "parseFormat": null,
      "displayFormat": "0000"
    },
    {
      "fieldId": "revision",
      "groupName": "revision",
      "header": "리비전",
      "order": 40,
      "required": false,
      "kind": "Int32",
      "parseFormat": null,
      "displayFormat": "N0"
    }
  ],
  "candidateKind": "Directory",
  "pathInput": "Relative",
  "rules": [
    {
      "id": "default",
      "pattern": "Clients\\\\(?<customer>[^\\\\]+)\\\\Projects\\\\(?<project>[^\\\\]+)\\\\(?<year>\\d{4})(?:\\\\Rev-(?<revision>\\d+))?",
      "matchMode": "Full",
      "ignoreCase": true,
      "timeoutMilliseconds": 100
    }
  ]
}
```

선언형 프로필에는 `entryAssembly`와 `modelType`을 사용할 수 없으며 `fields`가 반드시 하나 이상 있어야 합니다. `profile.json`은 1 MiB 이하여야 합니다. JSON 속성명은 대소문자를 구분하지 않고 주석과 trailing comma를 허용하지만, 위와 같은 GUI 출력 형식을 유지하는 편이 이식성과 가독성에 좋습니다.

## 매핑 결과와 로드 진단

개별 폴더 경로의 매핑 결과는 다음 셋 중 하나입니다.

- `Success`: 규칙이 일치했고 모든 캡처값이 성공적으로 변환되었습니다.
- `NoMatch`: 어떤 규칙에도 일치하지 않았습니다. 카탈로그 화면에서는 규칙 외 후보로 집계됩니다.
- `Invalid`: 규칙은 일치했지만 필수값 누락, 타입 변환 실패 또는 정규식 시간 초과가 발생했습니다.

`프로필` → `로드 상태`의 프로필 상태는 다음 두 값만 가집니다.

- `사용 가능` (`Loaded`): 검증과 컴파일을 통과해 카탈로그에서 선택할 수 있습니다.
- `사용 불가` (`Disabled`): manifest, DLL 또는 규칙에 문제가 있어 선택 대상에서 제외되었습니다. 같은 프로필 ID가 앱 번들 및 사용자 폴더를 포함해 둘 이상 발견되면 충돌한 항목을 모두 비활성화합니다.

진단 심각도는 `Information`, `Warning`, `Error`이며 화면에는 코드, 메시지, 상세 정보가 함께 표시됩니다. 잘못된 프로필 하나가 앱 전체의 시작이나 다른 정상 프로필의 로드를 중단시키지는 않습니다.

## 고급: DLL 프로필

기존 DLL 프로필은 계속 호환됩니다. manifest에서 `kind`를 생략하면 이전 형식과의 호환을 위해 `Assembly`로 해석되지만 새 파일에는 명시하는 것을 권장합니다.

```json
{
  "contractVersion": 1,
  "kind": "Assembly",
  "id": "sample-projects",
  "version": "1.0.0",
  "displayName": "샘플 프로젝트",
  "entryAssembly": "FindEverything.Profiles.SampleProjects.dll",
  "modelType": "FindEverything.Profiles.SampleProjects.SampleProject",
  "candidateKind": "Directory",
  "pathInput": "Relative",
  "rules": [
    {
      "id": "default",
      "pattern": "(?<year>\\d{4})[\\\\/](?<projectCode>PRJ-\\d+)(?:[\\\\/]Rev(?<revision>\\d+))?",
      "matchMode": "Full",
      "ignoreCase": true,
      "timeoutMilliseconds": 100
    }
  ]
}
```

모델은 public concrete class이고 public 매개 변수 없는 생성자가 있어야 합니다. 캡처할 속성은 public setter를 가져야 하며 인덱서는 사용할 수 없습니다. 지원 타입은 `string`, `int`, `decimal`, `DateTime`, `bool` 및 이 값 타입들의 nullable 형식입니다. 선택 필드가 값 타입이면 반드시 nullable이어야 합니다.

```csharp
using FindEverything.Profile.Abstractions;

public sealed class SampleProject
{
    [CaptureField("year", "year", Header = "연도", Order = 10, Required = true)]
    public int Year { get; set; }

    [CaptureField("project-code", "projectCode", Header = "프로젝트", Order = 20, Required = true)]
    public string ProjectCode { get; set; } = string.Empty;

    [CaptureField("revision", "revision", Header = "리비전", Order = 30)]
    public int? Revision { get; set; }
}
```

`Assembly` 프로필은 `fields`를 manifest에 둘 수 없습니다. 필드 정보는 모두 `CaptureFieldAttribute`에서 읽습니다. 프로필 폴더에는 `profile.json`, entry assembly, private dependency를 함께 둡니다. `entryAssembly`는 프로필 디렉터리 안의 `.dll`이어야 하며 계약 assembly인 `FindEverything.Profile.Abstractions`는 Host의 기본 load context와 공유됩니다.

```text
Profiles/<profile-id>/
  profile.json
  <profile assembly>.dll
  <private dependencies>.dll
```

DLL 플러그인은 보안 sandbox가 아닙니다. 출처를 신뢰할 수 있는 assembly만 배치하고, GUI 밖에서 파일을 변경한 뒤에는 앱을 재시작하세요.
