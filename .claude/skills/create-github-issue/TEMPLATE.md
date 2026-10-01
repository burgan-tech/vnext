# Issue template and examples — create-github-issue

Loaded when drafting the issue (Step 2).

## Title

Imperative English, max 72 chars, format `[type] <concise description>`, e.g.
`[bug] Fix null reference in payment callback handler`.

## Body template

Fill in every section; remove sections that truly do not apply.

```markdown
## Description
<!-- What is the problem or goal? Why does it matter? -->

## Steps to Reproduce (bugs only)
<!-- Numbered steps. Remove this section for non-bugs. -->
1.
2.

## Expected Behavior
<!-- What should happen? -->

## Actual Behavior (bugs only)
<!-- What actually happens? Remove for non-bugs. -->

## Technical Context
<!-- Relevant files, classes, methods, or patterns identified during scope analysis.
     If "projeyi tara" was used, populate from scan results. Otherwise, add what is known. -->
-

## Acceptance Criteria
<!-- Definition of done — bullet list of verifiable conditions -->
- [ ]

## Notes
<!-- Risks, dependencies, related issues, or anything else worth knowing. Remove if not applicable. -->
```

## Title examples

| User scope | Generated title |
|-----------|----------------|
| "Login sayfası hata veriyor, boş email kabul ediyor" | `[bug] Fix login form accepting empty email address` |
| "Kullanıcı profil sayfasına avatar yükleme ekle" | `[enhancement] Add avatar upload to user profile page` |
| "RuleEvaluator servisini temizle, tekrar eden kodlar var" | `[refactor] Remove duplicated logic in RuleEvaluator service` |

## Minimal body example (no scan)

```markdown
## Description
The login form currently accepts empty email addresses and submits, causing a 500 error on the server side.

## Steps to Reproduce
1. Navigate to /login
2. Leave the email field blank, enter any password
3. Click Submit

## Expected Behavior
Client-side validation rejects the form and shows an error message.

## Actual Behavior
The form submits and the server returns a 500 Internal Server Error.

## Technical Context
- `src/components/LoginForm.jsx` — form submit handler lacks email presence check

## Acceptance Criteria
- [ ] Empty email field shows inline validation error
- [ ] Form does not submit until email is valid
- [ ] Server-side validation also rejects empty email (defense in depth)
```
