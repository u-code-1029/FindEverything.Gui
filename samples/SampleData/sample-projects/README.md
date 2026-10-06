# 샘플 프로젝트 폴더 데이터

이 폴더는 `샘플 프로젝트 폴더` 프로필을 바로 시험하기 위한 검색 루트입니다.
앱에서 검색 루트로 **이 `sample-projects` 폴더 자체**를 선택하고, 인덱스 DB는
이 폴더 밖에 지정한 뒤 `인덱싱`을 누르세요.

```text
sample-projects/
└─ Clients/
   ├─ Contoso/Projects/Apollo/2026/
   ├─ Fabrikam/Projects/Aurora/2025/20250317/Rev-3/Approved-true/Amount-125000.50/
   └─ Northwind/Projects/Legacy/2024/Rev-12/Approved-false/Amount-2500.75/
```

각 leaf의 `sample-item.txt`는 빈 폴더 구조를 Git과 배포 결과에 유지하기 위한
표시 파일입니다. 프로필 후보는 폴더만 조회하므로 목록에는 나타나지 않습니다.

## 가장 깊은 행의 예상 값

| 고객 | 프로젝트 | 연도 | 수집일 | 리비전 | 승인 | 금액 |
|---|---|---:|---|---:|---|---:|
| Contoso | Apollo | 2026 | — | — | — | — |
| Fabrikam | Aurora | 2025 | 2025-03-17 | 3 | True | 125,000.50 |
| Northwind | Legacy | 2024 | — | 12 | False | 2,500.75 |

## 왜 3개가 아니라 10개 행인가요?

현재 정규식에서 연도 뒤의 수집일, 리비전, 승인, 금액은 모두 선택 그룹입니다.
그래서 아래처럼 각 중간 폴더도 그 자체로 유효한 항목입니다.

- `Contoso/Apollo`: 연도 단계 1행
- `Fabrikam/Aurora`: 연도 → 수집일 → 리비전 → 승인 → 금액 단계 5행
- `Northwind/Legacy`: 연도 → 리비전 → 승인 → 금액 단계 4행

합계 10행이며, 각 단계에서 아직 경로에 없는 값은 `—`로 표시됩니다. 상위의
`Clients`, 고객명, `Projects`, 프로젝트명 폴더는 규칙에 맞지 않으므로 상태의
`규칙 외` 후보 수에 포함되는 것도 정상입니다. 현재 트리를 처음 인덱싱하면
상태 요약은 `후보 20 · 일치 10 · 규칙 외 10 · 변환 오류 0`으로 나옵니다.
