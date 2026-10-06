# 프로필 작성 가이드

## 1. 모델 정의

프로필 모델은 public concrete class, public 기본 생성자, public setter를 가져야 합니다. 지원 타입은 `string`, `int`, `decimal`, `DateTime`, `bool` 및 nullable 값 타입입니다.

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

`fieldId`는 사용자별 열 설정을 저장하는 안정적인 ID이므로 속성명이나 regex group명을 변경하더라도 가능하면 유지합니다. 선택 필드가 값 타입이면 반드시 nullable이어야 합니다.

## 2. manifest 정의

```json
{
  "contractVersion": 1,
  "id": "sample-projects",
  "version": "1.0.0",
  "displayName": "샘플 프로젝트",
  "entryAssembly": "FindEverything.Profiles.SampleProjects.dll",
  "modelType": "FindEverything.Profiles.SampleProjects.SampleProject",
  "candidateKind": "directory",
  "pathInput": "relative",
  "rules": [
    {
      "id": "default",
      "pattern": "^(?<year>\\d{4})[\\\\/](?<projectCode>PRJ-\\d+)(?:[\\\\/]Rev(?<revision>\\d+))?$",
      "matchMode": "full",
      "ignoreCase": true,
      "timeoutMilliseconds": 100
    }
  ]
}
```

규칙은 선언 순서대로 평가합니다. 첫 번째로 일치한 규칙이 그 경로를 소유하므로 필드 변환이 실패해도 다음 규칙으로 넘어가지 않습니다.

- 필수 group이 없거나 공백이면 해당 경로는 `Invalid`입니다.
- 선택 group이 없거나 공백이면 `null`입니다.
- 정규식 timeout도 `Invalid`이며 나머지 규칙을 평가하지 않습니다.
- `full`은 입력 전체가 일치해야 하고 `partial`은 부분 일치를 허용합니다.
- 변환은 invariant culture를 사용합니다.

## 3. 배치와 진단

프로필 폴더는 `profile.json` 바로 아래의 assembly만 로드합니다. assembly의 private dependency는 같은 폴더에 둘 수 있습니다. 계약 assembly인 `FindEverything.Profile.Abstractions`는 Host의 기본 load context와 공유됩니다.

잘못된 프로필은 앱 전체를 중단시키지 않고 `Profiles` 화면에서 비활성화 이유를 보여줍니다. 중복된 profile ID가 있으면 충돌한 프로필을 모두 비활성화합니다.
