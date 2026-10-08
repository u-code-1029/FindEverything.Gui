# Sample project folder data

This folder is a ready-to-use search root for the `Sample project folders` profile.
In the app, choose **this `sample-projects` folder itself** as the search root, place
the index database outside this folder, and then start indexing.

```text
sample-projects/
└─ Clients/
   ├─ Contoso/Projects/Apollo/2026/
   ├─ Fabrikam/Projects/Aurora/2025/20250317/Rev-3/Approved-true/Amount-125000.50/
   └─ Northwind/Projects/Legacy/2024/Rev-12/Approved-false/Amount-2500.75/
```

Each leaf's `sample-item.txt` is a placeholder that preserves the otherwise empty
folder structure in Git and published builds. The profile evaluates folders only,
so these files do not appear in the structured results.

## Expected values for the deepest rows

| Client | Project | Year | Collected on | Revision | Approved | Amount |
|---|---|---:|---|---:|---|---:|
| Contoso | Apollo | 2026 | — | — | — | — |
| Fabrikam | Aurora | 2025 | 2025-03-17 | 3 | True | 125,000.50 |
| Northwind | Legacy | 2024 | — | 12 | False | 2,500.75 |

## Why are there 10 rows instead of 3?

The current regular expression makes the collection date, revision, approval, and
amount groups after the year optional. Each intermediate folder is therefore also
a valid item:

- `Contoso/Apollo`: 1 row at the year level
- `Fabrikam/Aurora`: 5 rows across year → collection date → revision → approval → amount
- `Northwind/Legacy`: 4 rows across year → revision → approval → amount

That produces 10 rows in total. Values not yet present in a path are shown as `—`.
The parent `Clients`, client-name, `Projects`, and project-name folders do not match
the rule, so they correctly count as outside-rule candidates. After this tree is
indexed for the first time, the status summary is `Candidates 20 · Matches 10 ·
Outside rules 10 · Conversion errors 0`.
