# Council Selection Rules

## Core Roles

Every non-trivial council includes Solution Architect, Pragmatic Engineer, and Test & Evidence Agent.

## Conditional Roles

| Trigger | Role |
|---|---|
| Authentication, authorization, PII or secrets | Security Guardian |
| Data model, migration, consistency or transaction | Data Architect |
| Latency, throughput, allocation, cache or concurrency | Performance Engineer |
| Deployment, recovery or production operations | Reliability Engineer |
| Cross-service or High/Critical risk | Devil's Advocate |

## Size

- Low: 3 core roles
- Medium: 3–5 roles
- High: 4–7 roles
- Critical: all applicable roles plus required human expert

Before adding a role, record which blind spot it closes. Do not select every role by default.
