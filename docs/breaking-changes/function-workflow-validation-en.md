# Breaking Change: Function–Workflow Validation

## Summary

When invoking a **function** whose scope is **Instance (`"scope": "I"`)** or **Flow (`"scope": "F"`)**, the workflow’s **flow definition** must declare that function in its `functions` array. If it does not, the API returns a validation error instead of executing the function.

## Error Response

If the function is not declared for the workflow:

- **Message:** `"Function '{functionKey}' is not defined for workflow '{workflowKey}'"`
- **HTTP status:** 400 (Validation)
- **Error code:** `Function:800001` (FunctionNotInWorkflow)

## Required Configuration

The **workflow (flow) definition** must reference the function in its `functions` list. Only functions listed there may be executed in the context of that workflow when their scope is `I` or `F`.

**Example (workflow/flow definition):**

```json
{
  "key": "parent-flow",
  "domain": "local-test",
  "version": "1.0.0",
  "functions": [
    {
      "key": "function-get-instance-summary",
      "domain": "local-test",
      "flow": "sys-functions",
      "version": "1.0.0"
    }
  ]
}
```

If `function-get-instance-summary` has `"scope": "I"` or `"scope": "F"` but is **missing** from the flow’s `functions` array, calls to that function for this workflow will fail with the error above.

## Scope Behaviour

- **System / global functions** (e.g. from `sys-functions` flow) that are **not** scope `I` or `F` are not subject to this check.
- For **Instance (`I`)** and **Flow (`F`)** scope, the function **must** be listed in the workflow’s `functions` array.
