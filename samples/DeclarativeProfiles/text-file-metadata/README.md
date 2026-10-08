# 텍스트 파일 메타데이터 프로필 예제

`profile.json`은 일치한 프로젝트 폴더 바로 아래의 `description.txt`를 읽어
`description` 문자열 필드로 추가합니다. 앱의 `프로필 폴더 열기`에서 연 사용자
프로필 폴더에 이 디렉터리를 복사한 뒤 앱을 다시 시작하거나, GUI에서 같은 값을
정의해 사용할 수 있습니다.

시험 데이터는 `samples/SampleData/text-file-metadata`에 있습니다. 검색 루트로 그
폴더를 선택하면 `Apollo`와 `Aurora` 두 항목이 매핑됩니다. `Apollo`에는 파일이
있으므로 설명이 표시되고, `Aurora`는 선택 필드라서 설명이 `null`로 표시됩니다.
