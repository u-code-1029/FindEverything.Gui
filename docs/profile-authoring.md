# 프로필 작성 가이드

프로필은 폴더 경로의 일부를 필드로 추출해 결과 표의 한 행으로 바꾸는 규칙입니다. 대부분의 프로필은 C# 프로젝트나 DLL 없이 앱의 `프로필` → `프로필 편집`에서 만드는 방식을 권장합니다. 초보자 모드는 먼저 결과 필드를 만든 뒤 실제 경로의 조각 버튼을 필드에 연결하고, 전문가 모드는 기존 방식대로 .NET 정규식과 그룹을 직접 편집합니다. DLL 프로필은 코드 기반 모델이 꼭 필요한 경우를 위한 호환·고급 기능입니다.

## 초보자 모드에서 프로필 만들기

예를 들어 다음 상대 경로에서 고객, 프로젝트 번호와 기준일을 추출한다고 가정합니다.

```text
Clients\Acme\Projects\PRJ-042\2026\0521_Release
```

1. `새로 만들기`를 누르고 표시 이름과 프로필 ID를 입력합니다.
2. 경로 입력은 보통 `검색 루트 기준 상대 경로`를 선택합니다.
3. 먼저 `고객`, `프로젝트 번호`, `기준일` 결과 값 카드를 만듭니다. 고객은 텍스트, 프로젝트 번호는 정수, 기준일은 날짜/시간으로 지정합니다.
4. 기준일 카드의 `날짜가 나뉘어 있는 방식`에서 `연도 + 월일`(`yyyy` + `MMdd`)을 고릅니다. 한 날짜가 `20260521`처럼 한 조각이면 `한 조각에서 날짜 읽기`, 연도·월·일이 각각 나뉘면 `연도 + 월 + 일`을 선택합니다.
5. 실제 경로를 붙여 넣고 `경로 분석`을 누릅니다. `/`와 `\`를 기준으로 폴더명 전체 버튼을 만들고, `PRJ-042`나 `0521_Release`처럼 `_`, `-`, 공백이 있는 폴더명은 `PRJ`·`042`, `0521`·`Release` 같은 세부 조각 버튼도 만듭니다.
6. 고객 카드에서 `경로 조각 선택`을 누른 뒤 `Acme` 버튼을 누릅니다. 프로젝트 번호 카드에서는 같은 방식으로 `042`를 고릅니다. 기준일 카드에서는 `연도 선택` 후 `2026`, `월일 선택` 후 `0521`을 차례로 고릅니다.
7. 연결하지 않은 `Clients`, `Projects`, `PRJ-`, `_Release`는 고정 텍스트로 남고, 읽기 전용 경로 규칙은 다음과 같은 형태가 됩니다.

   ```text
   Clients/{customer@customer}/Projects/PRJ-{project-number@projectNumber}/{captured-on@capturedOnYear}/{captured-on@capturedOnMonthDay}_Release
   ```

   실제 Field ID와 그룹 이름은 카드의 내부 키에 따라 달라질 수 있습니다. 토큰을 직접 입력할 필요는 없습니다.

8. 이 경로가 업무 객체의 마지막 폴더라면 기준일 카드의 `이 값까지 찾으면 하위 폴더 건너뛰기`를 선택합니다. 현재 폴더는 결과 판정을 유지하고 그 아래만 빠른 스캔에서 생략합니다.
9. `검증`을 실행하고 `이 경로로 결과 확인`에서 추출값과 변환 오류를 확인한 뒤 `검증 후 저장`을 누릅니다. 임시 저장 파일까지 같은 런타임 규칙으로 검증되면 새 프로필이 카탈로그에 즉시 나타나며 앱을 재시작할 필요가 없습니다.

초보자 모드는 필드 카드에서 시작해 버튼이나 칩으로 경로 조각을 지정합니다. 드래그 앤 드롭은 사용하지 않고, 만들어진 경로 규칙을 직접 편집할 수도 없습니다. 직접 템플릿이나 정규식을 바꾸어야 하면 전문가 모드로 전환하세요.

빠른 스캔, 인덱싱 또는 기존 DB 불러오기가 실행 중일 때는 작업이 끝난 뒤 저장해야 합니다. 한 번 저장한 프로필의 ID는 편집 화면에서 바꿀 수 없으므로 다른 ID가 필요하면 새 프로필을 만드세요.

## 경로 템플릿 문법

템플릿 자체의 구분자는 운영체제와 관계없이 `/`입니다. 각 `/`는 실제 경로의 `/` 또는 `\` 하나와 일치하므로 Windows 경로를 위해 별도 escape를 입력할 필요가 없습니다. 초보자 모드의 `경로 분석`과 조각 선택이 이 템플릿을 자동으로 만들며, 화면의 템플릿 미리보기는 읽기 전용입니다.

한국어 Windows나 일부 글꼴에서는 백슬래시 문자 `\`(U+005C)가 원화 기호처럼 보일 수 있습니다. 앱은 `/`와 이 U+005C 백슬래시를 경로 구분자로 처리하며, 실제 원화 통화 문자 `₩`(U+20A9)는 구분자로 처리하지 않습니다.

| 표기 | 의미 |
| --- | --- |
| `{customer@customer}` | 필수 필드 `customer`에 정규식 그룹 `customer`의 경로 조각을 연결합니다. |
| `{captured-on@year}` | 복합 필드 `captured-on`을 구성하는 `year` 그룹 조각입니다. 같은 필드의 `groupNames`에 선언한 모든 그룹 조각이 필요합니다. |
| `{revision@revision?}` | 선택 필드의 선택 조각입니다. `?`는 토큰 전체의 마지막 문자이며 해당 필드는 `필수`가 해제되어 있어야 합니다. |
| `{customer}` | 그룹이 하나뿐인 필드에서만 쓸 수 있는 이전 표기의 축약형입니다. 초보자 모드는 명확한 `필드@그룹` 표기를 만듭니다. |
| `/` | 실제 경로의 `/`와 `\`를 모두 일치시키는 경로 구분자입니다. |
| `{{`, `}}` | 각각 리터럴 `{`, `}`를 뜻합니다. |

중괄호 토큰과 `/` 이외의 텍스트는 정규식이 아니라 그대로 일치하는 리터럴입니다. 따라서 `.`이나 `+` 같은 문자를 escape할 필요가 없습니다. 단일 그룹 필드는 한 번만 사용할 수 있고, 복합 필드는 `groupNames`의 각 그룹을 정확히 한 번씩 모두 연결해야 합니다. 필수 필드는 연결된 모든 그룹 조각이 템플릿에 있어야 합니다.

선택 토큰은 템플릿 끝에 이어지는 경로 구간에서만 사용할 수 있고, 선택 토큰이 있는 구간에는 필드 토큰을 하나만 넣을 수 있습니다. 선택 토큰이 있는 구간은 앞의 `/`와 고정 텍스트까지 함께 생략됩니다. 예를 들어 `.../{revision@revision?}`은 마지막 폴더 전체가 선택 사항이고, `.../Rev-{revision@revision?}`은 `/Rev-3` 전체가 선택 사항입니다.

선택 구간이 여러 개면 앞에서부터 차례로 채워집니다.

```text
Clients/{customer@customer}/Projects/{project@project}/{year@year}/{revision@revision?}/{approved@approved?}
```

이 템플릿은 연도에서 끝나거나 리비전까지, 또는 리비전과 승인까지 있는 경로와 일치합니다. 리비전 없이 승인만 있는 경로와는 일치하지 않습니다. 중간 구간을 선택 사항으로 만들거나 한 선택 구간에서 둘 이상의 값을 추출해야 하면 전문가 모드의 정규식을 사용하세요.

토큰이 받아들이는 기본 문자열은 필드 타입에 따라 달라집니다.

| 값 타입 | 템플릿에서 허용하는 형태 |
| --- | --- |
| 텍스트, 날짜/시간 | 경로 구분자가 아닌 문자 한 개 이상 |
| 정수 | 선택적 `+`/`-` 부호와 숫자 |
| 소수 | 선택적 `+`/`-` 부호, 숫자, 선택적 소수점 이하 숫자 |
| 참/거짓 | `true` 또는 `false` |

날짜/시간 입력 형식과 실제 타입 변환은 아래 필드 설정을 그대로 따릅니다. 템플릿 토큰은 카드의 고급 정보에 표시되는 Field ID를 사용하고, `@` 뒤에는 해당 필드를 구성하는 정규식 그룹 이름이 들어갑니다.

날짜 프리셋의 저장 방식은 다음과 같습니다.

| 초보자 프리셋 | 그룹 구성 | 변환 방식 |
| --- | --- | --- |
| 한 조각에서 날짜 읽기 | 단일 `groupName` | 선택한 한 조각을 `parseFormat`으로 변환합니다. 예를 들어 `20260521`에는 `yyyyMMdd`를 사용할 수 있습니다. |
| 연도 + 월일 (`yyyy` + `MMdd`) | 순서가 있는 `groupNames` 2개 | 연도와 월일의 원시 문자열을 순서대로 붙인 뒤 `yyyyMMdd`로 변환합니다. |
| 연도 + 월 + 일 (`yyyy` + `MM` + `dd`) | 순서가 있는 `groupNames` 3개 | 연도, 월, 일을 순서대로 붙인 뒤 `yyyyMMdd`로 변환합니다. |

여러 그룹을 쓰는 날짜 필드는 `parseFormat`이 반드시 필요하며 두 복합 날짜 프리셋은 `yyyyMMdd`를 사용합니다. `groupNames`의 순서는 단순한 설명 순서가 아니라 실제 문자열 결합 순서입니다. 예를 들어 `groupNames: ["year", "monthDay"]`에서 `year=2026`, `monthDay=0521`이면 `20260521`을 `yyyyMMdd`로 해석합니다.

복합 필드가 선택 사항일 때 그룹이 모두 없으면 결과는 `null`입니다. 반대로 일부 그룹만 캡처되면 불완전한 값이므로 `Invalid`입니다. 필수 복합 필드는 모든 그룹이 있어야 하고, 결합한 문자열의 변환까지 성공해야 합니다.

## 전문가 모드에서 정규식 사용하기

전문가 모드는 여러 규칙, 부분 일치, 대소문자 옵션, 제한 시간, 하위 탐색 중단 조건 또는 템플릿으로 표현할 수 없는 경로 구조가 필요할 때 사용합니다. 기존 정규식 기반 선언형 프로필은 변환할 필요 없이 계속 실행되며 전문가 모드에서 같은 규칙을 편집할 수 있습니다.

위 예제 경로를 직접 표현하는 전체 일치 pattern은 다음과 같습니다. `전체 일치`를 선택하면 `^`와 `$`를 따로 붙이지 않아도 됩니다.

```regex
Clients[\\/](?<customer>[^\\/]+)[\\/]Projects[\\/]PRJ-(?<projectNumber>\d+)[\\/](?<year>\d{4})[\\/](?<monthDay>\d{4})_Release
```

1. 고객에는 `customer`, 프로젝트 번호에는 `projectNumber`를 `단일 그룹`에 입력합니다. 기준일에는 `year, monthDay`를 실제 결합 순서대로 `조합 그룹 (쉼표 순서)`에 입력하고 입력 형식을 `yyyyMMdd`로 지정합니다.
2. 경로 규칙에 pattern과 매칭 방식, 대소문자 처리, 제한 시간을 입력합니다. 빠른 스캔에서 리프 폴더 아래를 생략하려면 해당 규칙의 `탐색 중단 그룹 (쉼표 순서)`도 입력합니다.
3. 실제 예제 경로로 `경로 시험`을 실행합니다.
4. `검증`을 통과한 뒤 `검증 후 저장`을 누릅니다.

전문가 편집기의 단일 정규식 그룹과 복합 그룹 목록은 서로 대체 관계이므로 한 필드에 둘 다 입력할 수 없습니다. 복합 그룹은 왼쪽부터 이어 붙여 하나의 필드 값으로 변환하며, 복합 날짜에는 그 결과를 해석할 `parseFormat`이 필요합니다. 하위 탐색 중단 그룹은 필드 목록이 아니라 현재 규칙의 named group 목록이며, 아래의 빠른 스캔에서만 사용됩니다.

초보자 모드에서 전문가 모드로 전환하면 읽기 전용 템플릿으로 생성한 정규식을 이어서 편집할 수 있습니다. 정규식, 규칙 수, 옵션 또는 그룹 구성을 직접 바꿔 같은 템플릿으로 손실 없이 되돌릴 수 없게 되면 초보자 모드로 돌아가기 버튼이 비활성화됩니다. 단일 그룹과 의미 순서가 분명한 날짜 조합(`year + monthDay`, `year + month + day`)만 초보자 프리셋으로 복원하며, 날짜가 아닌 복합 필드나 순서가 다른 날짜 그룹은 값이 뒤바뀌지 않도록 전문가 모드에 유지합니다. `pathTemplate`이 없는 기존 프로필도 정규식은 바꾸지 않은 채 전문가 모드로 엽니다.

## 공통 입력값과 검증 규칙

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
| 정규식 그룹 | 단일 값은 `groupName` 하나, 나뉜 값은 순서가 있는 `groupNames`를 사용하며 둘을 동시에 지정할 수 없습니다. `groupNames`는 1~16개이며, 각 이름은 정규식 `[A-Za-z_][A-Za-z0-9_]*`을 만족하고 목록 안에서 중복되지 않아야 합니다. 초보자 모드는 `{fieldId@groupName}` 토큰을 만들고, 전문가 모드는 규칙의 `(?<groupName>...)` 이름과 정확히 연결합니다. |
| 필수 | 초보자 모드는 필수 필드에 필수 토큰, 필수를 해제한 필드에 `?`가 붙은 선택 토큰을 만듭니다. 실행 시 필수 그룹이 모두 없거나, 일부만 있거나, 값이 공백이거나, 결합한 값의 타입 변환에 실패하면 경로가 `Invalid`가 됩니다. 선택 필드의 모든 그룹이 없으면 `null`이지만 일부만 있으면 `Invalid`입니다. |
| 입력 형식 | `DateTime`에만 사용할 수 있는 .NET 날짜/시간 형식입니다. 예: `yyyyMMdd`. 지정하면 invariant culture의 정확한 형식으로만 변환합니다. 여러 그룹으로 구성한 `DateTime`에는 반드시 필요하며, 그룹 문자열을 선언 순서대로 붙인 결과에 적용합니다. 다른 타입에 입력하거나 형식 자체가 잘못되면 검증 오류입니다. |
| 표시 형식 | `Int32`, `Decimal`, `DateTime`에만 사용할 수 있는 유효한 .NET 표준/사용자 지정 형식입니다. 예: `N0`, `N2`, `yyyy-MM-dd`. `String`과 `Boolean`에는 비워 두어야 합니다. 표시 형식은 화면 표현만 바꾸고 저장된 값의 타입은 바꾸지 않습니다. |

지원 값 타입과 변환 규칙은 다음과 같습니다. 모든 변환은 언어·지역 설정과 무관한 invariant culture를 사용합니다.

| GUI 표시 | manifest 값 | 입력 예 | 비고 |
| --- | --- | --- | --- |
| 텍스트 | `String` | `Acme` | 캡처한 문자열을 그대로 사용합니다. |
| 정수 | `Int32` | `2026`, `-3` | 32비트 정수 범위여야 합니다. |
| 소수 | `Decimal` | `1234.50` | invariant culture 숫자 형식을 사용하므로 소수점은 `.`입니다. |
| 날짜/시간 | `DateTime` | `20261007` | 입력 형식이 있으면 `TryParseExact`, 없으면 invariant culture 날짜 파싱을 사용합니다. |
| 참/거짓 | `Boolean` | `true`, `false` | 대소문자를 구분하지 않는 Boolean 문자열이어야 합니다. |

### 전문가 모드의 경로 규칙

초보자 모드에서는 경로 템플릿으로 이 규칙을 만듭니다. 전문가 모드의 규칙은 하나 이상 필요합니다. GUI의 위·아래 순서대로 평가하며 첫 번째로 일치한 규칙이 그 경로를 소유합니다. 첫 규칙의 필드 변환이 실패하거나 정규식 시간이 초과되어도 다음 규칙으로 넘어가지 않습니다.

| 항목 | 허용값과 동작 |
| --- | --- |
| 규칙 ID | 정규식 `[A-Za-z0-9][A-Za-z0-9._-]{0,63}`을 만족하는 최대 64자 문자열입니다. 영숫자로 시작하고 이후에는 영숫자, 점, 밑줄, 하이픈을 쓸 수 있으며 대소문자를 무시해 고유해야 합니다. 비워 두면 런타임이 선언 순서에 따라 `rule-N`을 사용하지만 명시적으로 입력하는 것을 권장합니다. |
| Pattern | 공백이 아닌 유효한 .NET 정규식이어야 합니다. 필수 필드를 구성하는 모든 named group은 각 규칙에 정의되어 있어야 합니다. 선택 필드의 group은 일부 규칙에서 생략할 수 있습니다. |
| 매칭 | `Full`은 입력 전체가 일치해야 하며 런타임이 절대 시작·끝 anchor를 추가합니다. `Partial`은 입력 중 일부만 일치해도 됩니다. |
| 대소문자 무시 | 선택하면 .NET `RegexOptions.IgnoreCase`를 사용합니다. 두 경우 모두 culture-invariant 정규식으로 컴파일됩니다. |
| 제한 시간 | `1`~`10000`ms의 정수여야 합니다. 시간 초과는 해당 경로를 `Invalid`로 처리합니다. 기본값은 `100`ms입니다. |
| 하위 탐색 중단 그룹 | `stopTraversalWhenCapturedGroups`에 지정할 named group 목록으로 최대 16개입니다. 이름은 중복될 수 없고 현재 pattern에 모두 정의되어 있어야 합니다. 목록이 비어 있으면 중단하지 않으며, 지정한 모든 그룹이 공백이 아닌 원시 값을 캡처한 경우에만 현재 폴더 아래를 건너뜁니다. |

Windows 폴더 구분자 `\` 하나를 정규식으로 일치시키려면 GUI pattern에는 `\\`를 입력합니다. JSON을 직접 편집할 때는 JSON escape까지 필요하므로 같은 부분이 `\\\\`로 보입니다.

## 구조화 보기의 빠른 스캔과 탐색 중단

`구조화 보기`의 기본 동작인 `프로필로 빠르게 불러오기`는 선택한 루트를 직접 걷는 프로필 기반 빠른 스캔입니다. 폴더를 발견하는 즉시 프로필을 적용하므로 공유 SQLite 인덱스를 만들거나, 열거나, 갱신하지 않습니다. 이미 `파일 찾기`에서 만든 DB를 재사용해야 할 때만 `고급: 기존 인덱스에서 불러오기`를 선택합니다. 기존 인덱스 불러오기는 저장된 내용을 읽을 뿐이며 공유 인덱스를 갱신하는 화면은 `파일 찾기`뿐입니다.

`stopTraversalWhenCapturedGroups`는 빠른 스캔에서 업무 객체의 마지막 폴더를 알려 주는 규칙별 조건입니다.

- 규칙이 일치하고 목록의 모든 그룹이 공백이 아닌 값을 실제로 캡처하면, 일치한 현재 폴더의 매핑 결과는 그대로 유지하고 그 자식과 더 아래 폴더만 방문하지 않습니다.
- 판정 기준은 필드 변환 전의 원시 정규식 캡처입니다. 따라서 모든 중단 그룹이 캡처되었다면 날짜나 숫자 변환이 실패해 현재 폴더가 `Invalid`여도 하위 폴더는 생략합니다.
- 그룹이 하나라도 없거나 빈 값이면 계속 내려갑니다. 목록이 없거나 비어 있어도 탐색을 중단하지 않습니다.
- 정규식 제한 시간이 초과되면 현재 경로는 `Invalid`이지만 안전하게 계속 탐색합니다. 즉, 시간 초과는 하위 폴더를 잘못 놓치지 않도록 fail-open으로 동작합니다.
- 이 힌트는 `구조화 보기`의 직접 빠른 스캔에만 영향을 줍니다. `파일 찾기`의 공유 인덱스 생성, 파일 검색, 기존 인덱스 불러오기 결과에는 가지치기를 적용하지 않습니다.

초보자 모드에서는 결과 값 카드 하나를 `이 값까지 찾으면 하위 폴더 건너뛰기`로 선택하면 그 필드를 구성하는 모든 그룹을 저장합니다. 전문가 모드에서는 각 규칙마다 그룹 목록을 직접 지정할 수 있습니다. 조건에 필요한 그룹이 여러 개면 모두 캡처되어야 하므로 AND 조건으로 생각하면 됩니다.

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
      "fieldId": "project-number",
      "groupName": "projectNumber",
      "header": "프로젝트 번호",
      "order": 20,
      "required": true,
      "kind": "Int32",
      "parseFormat": null,
      "displayFormat": "N0"
    },
    {
      "fieldId": "captured-on",
      "groupNames": [
        "year",
        "monthDay"
      ],
      "header": "기준일",
      "order": 30,
      "required": true,
      "kind": "DateTime",
      "parseFormat": "yyyyMMdd",
      "displayFormat": "yyyy-MM-dd"
    }
  ],
  "candidateKind": "Directory",
  "pathInput": "Relative",
  "rules": [
    {
      "id": "default",
      "pathTemplate": "Clients/{customer@customer}/Projects/PRJ-{project-number@projectNumber}/{captured-on@year}/{captured-on@monthDay}_Release",
      "pattern": "Clients[\\\\/](?<customer>[^\\\\/]+)[\\\\/]Projects[\\\\/]PRJ-(?<projectNumber>[+-]?\\d+)[\\\\/](?<year>[^\\\\/]+)[\\\\/](?<monthDay>[^\\\\/]+)_Release",
      "matchMode": "Full",
      "ignoreCase": true,
      "timeoutMilliseconds": 100,
      "stopTraversalWhenCapturedGroups": [
        "year",
        "monthDay"
      ]
    }
  ]
}
```

`groupName`은 단일 캡처용이고 `groupNames`는 여러 캡처를 순서대로 합칠 때 사용합니다. 위 기준일은 `year`의 `2026`과 `monthDay`의 `0521`을 `20260521`로 합친 뒤 필수 `parseFormat`인 `yyyyMMdd`로 변환합니다. 두 속성을 한 필드에 함께 둘 수 없으며, `groupNames`에는 중복 없이 최대 16개를 지정할 수 있습니다.

`pathTemplate`은 초보자 모드로 다시 열기 위한 선택적 작성 정보입니다. `{fieldId@groupName}`이 특정 필드의 특정 구성 그룹을 가리킵니다. 앱은 이 템플릿을 다시 컴파일한 결과가 저장된 `pattern` 및 초보자 모드의 고정 옵션과 정확히 같을 때만 초보자 모드를 사용합니다. 기존 정규식 프로필처럼 `pathTemplate`이 없거나 둘이 일치하지 않으면 `pattern`을 바꾸지 않고 전문가 모드로 엽니다.

`stopTraversalWhenCapturedGroups`에는 현재 규칙에 정의된 그룹을 최대 16개까지 넣을 수 있습니다. 위 예제에서는 `year`와 `monthDay`가 모두 캡처된 현재 폴더를 판정한 뒤 그 하위 탐색을 멈춥니다. 자세한 판정 순서는 위의 빠른 스캔 절을 참고하세요.

선언형 프로필에는 `entryAssembly`와 `modelType`을 사용할 수 없으며 `fields`가 반드시 하나 이상 있어야 합니다. `profile.json`은 1 MiB 이하여야 합니다. JSON 속성명은 대소문자를 구분하지 않고 주석과 trailing comma를 허용하지만, 위와 같은 GUI 출력 형식을 유지하는 편이 이식성과 가독성에 좋습니다.

## 매핑 결과와 로드 진단

개별 폴더 경로의 매핑 결과는 다음 셋 중 하나입니다.

- `Success`: 규칙이 일치했고 모든 캡처값이 성공적으로 변환되었습니다.
- `NoMatch`: 어떤 규칙에도 일치하지 않았습니다. 카탈로그 화면에서는 규칙 외 후보로 집계됩니다.
- `Invalid`: 규칙은 일치했지만 필수값 누락, 복합 필드의 일부 캡처 누락, 타입 변환 실패 또는 정규식 시간 초과가 발생했습니다.

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
      "pattern": "(?<year>\\d{4})[\\\\/](?<projectCode>PRJ-\\d+)(?:[\\\\/]Rev(?<revision>\\d+))?(?:[\\\\/](?<dateYear>\\d{4})[\\\\/](?<monthDay>\\d{4}))?",
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

    [CaptureField(
        "captured-on",
        "dateYear",
        "monthDay",
        Header = "촬영일",
        Order = 40,
        ParseFormat = "yyyyMMdd")]
    public DateTime? CapturedOn { get; set; }
}
```

`CaptureField(fieldId, groupName)`의 기존 단일 그룹 생성자는 그대로 사용할 수 있습니다. 여러 그룹을 넘기면 선언한 순서대로 캡처 값을 이어 붙인 뒤 속성 타입으로 변환합니다. 위 예에서는 `dateYear=2026`, `monthDay=0521`이 `20260521`이 되고 `ParseFormat="yyyyMMdd"`에 따라 하나의 `DateTime` 값이 됩니다. 복합 `DateTime` 필드에는 `ParseFormat`을 반드시 지정하세요.

`Assembly` 프로필은 `fields`를 manifest에 둘 수 없습니다. 필드 정보는 모두 `CaptureFieldAttribute`에서 읽습니다. 프로필 폴더에는 `profile.json`, entry assembly, private dependency를 함께 둡니다. `entryAssembly`는 프로필 디렉터리 안의 `.dll`이어야 하며 계약 assembly인 `FindEverything.Profile.Abstractions`는 Host의 기본 load context와 공유됩니다.

```text
Profiles/<profile-id>/
  profile.json
  <profile assembly>.dll
  <private dependencies>.dll
```

DLL 플러그인은 보안 sandbox가 아닙니다. 출처를 신뢰할 수 있는 assembly만 배치하고, GUI 밖에서 파일을 변경한 뒤에는 앱을 재시작하세요.
