# Form URL-Encoded Payloads

The public Orchestration API accepts `application/x-www-form-urlencoded` in addition to
`application/json` on these endpoint groups:

- Workflow instance start
- Workflow instance transition
- Domain function invocation
- Instance function invocation

The form body is converted to a JSON object before the existing payload-mode and workflow
pipelines run. Execution-service endpoints, multipart forms, and file uploads are not included.

## Standard payload

A standard payload contains the vNext envelope fields `key`, `stage`, `tags`, and `attributes`.
Use bracket paths for nested attributes:

```bash
curl -X POST \
  -H 'Content-Type: application/x-www-form-urlencoded' \
  --data-urlencode 'key=50044086191232280074134' \
  --data-urlencode 'stage=Initial' \
  --data-urlencode 'tags[]=retail' \
  --data-urlencode 'attributes[customer][name]=Ali' \
  --data-urlencode 'attributes[customer][age]=30' \
  'https://host/api/v1/acme/workflows/onboarding/instances/start'
```

This produces the same payload shape as:

```json
{
  "key": "50044086191232280074134",
  "stage": "Initial",
  "tags": ["retail"],
  "attributes": {
    "customer": {
      "name": "Ali",
      "age": 30
    }
  }
}
```

The envelope fields `key`, `stage`, and each `tags` element always remain strings, including
numeric-looking values. Their names follow the same case-insensitive model-binding behavior as
JSON when `x-vnext-payload-mode: standard` is supplied.

## Raw payload

When the form has no top-level `attributes` field, it is treated as a raw payload and the whole
object is passed through the existing raw-payload normalization:

```text
amount=100&approved=true&currency=TRY
```

Equivalent JSON:

```json
{
  "amount": 100,
  "approved": true,
  "currency": "TRY"
}
```

## Scalar types

Raw leaves and values below `attributes` use JSON-scalar rules:

| Form value | JSON value |
|---|---|
| `30` | number `30` |
| `1.25` | number `1.25` |
| `true` / `false` | boolean |
| `null` | null |
| `"00123"` | string `"00123"` |
| `Ali` | string `"Ali"` |

Quote a numeric-, boolean-, or null-looking value as a JSON string when its string type must be
preserved. URL-encode the quotes when constructing the request manually; for example,
`code=%2200123%22` represents `code="00123"`.

JSON objects and arrays are not accepted inside one scalar form value. Use bracket paths instead.

## Objects and arrays

Nested object:

```text
attributes[customer][ownerUserId]="2321321"
```

Scalar arrays can use trailing brackets or a repeated key:

```text
tags[]=a&tags[]=b
tags=a&tags=b
```

Because `tags` is a known standard-envelope array, a single `tags=a` field is normalized to
`"tags": ["a"]` as well.

Arrays of objects require contiguous numeric indices. The fields may arrive in any order:

```text
attributes[items][1][name]=B&attributes[items][0][name]=A
```

Equivalent JSON:

```json
{
  "attributes": {
    "items": [
      { "name": "A" },
      { "name": "B" }
    ]
  }
}
```

## Payload-mode override

The existing `x-vnext-payload-mode` header has the same precedence for form and JSON bodies:

- `x-vnext-payload-mode: standard` forces standard envelope handling.
- `x-vnext-payload-mode: raw` forces raw handling.
- Without the header, a top-level lowercase `attributes` path selects standard mode; otherwise the
  payload is raw.

Use the standard override when sending a standard envelope without an `attributes` field, such as
an input containing only `key`.

## Rejected forms

Invalid or ambiguous shapes return HTTP 400 with a model-binding error. Rejected examples include:

- Unclosed or malformed brackets: `attributes[name=value`
- Unindexed arrays of objects: `items[][name]=A`
- Negative indices: `items[-1]=A`
- Sparse indices: `items[1]=B` without `items[0]`
- Array indices above the supported maximum (1024): `items[1025]=A`
- Scalar/container collisions: `value=x&value[name]=y`
- JSON object or array embedded as a scalar value

Use numeric indices for arrays of objects and ensure every index from zero through the highest
index is present.
