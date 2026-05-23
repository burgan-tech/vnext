# Rule-Based View Selection

## Overview

Rule-based view selection allows you to dynamically select different views based on runtime conditions. This feature enables platform-specific UIs, role-based views, and conditional UI rendering without modifying workflow logic.

## Use Cases

- **Platform-specific views**: Show different views for iOS, Android, and Web clients
- **Role-based views**: Display different interfaces based on user roles
- **Conditional UI**: Render views based on instance data or state
- **A/B Testing**: Serve different views based on experiment conditions

## JSON Schema

The `views` property accepts an array of view entries. Each entry can have an optional `rule` that determines when it should be selected.

```json
{
  "views": [
    {
      "rule": {
        "location": "inline",
        "code": "using System.Threading.Tasks;\nusing BBT.Workflow.Scripting;\npublic class ViewIosRule : IConditionMapping\n{\n    public async Task<bool> Handler(ScriptContext context)\n    {\n        try\n        {\n            if (context.Headers?[\"x-platform\"] == \"ios\")\n            {\n                return true;\n            }\n            return false;\n        }\n        catch (Exception ex)\n        {\n            return false;\n        }\n    }\n}",
        "encoding": "NAT"
      },
      "view": {
        "domain": "my-domain",
        "key": "ios-view",
        "version": "1.0.0",
        "flow": "sys-views"
      },
      "loadData": true,
      "extensions": ["ext1", "ext2"]
    },
    {
      "view": {
        "domain": "my-domain",
        "key": "default-view",
        "version": "1.0.0",
        "flow": "sys-views"
      },
      "loadData": true
    }
  ]
}
```

## ViewEntry Properties

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `rule` | ScriptCode | No | JavaScript expression for conditional evaluation. If omitted, acts as fallback. |
| `view` | Reference | Yes | Reference to the view component to load. |
| `loadData` | boolean | No | Whether to load instance data with the view. Default: false. |
| `extensions` | string[] | No | List of extensions to execute when this view is selected. |

### Rule (ScriptCode) Properties

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `location` | string | No | Script location. Use `"inline"` for embedded code. |
| `code` | string | Yes | C# code implementing IConditionMapping interface. |
| `encoding` | string | No | `"NAT"` for plain text, `"B64"` for Base64 encoded. |

### View (Reference) Properties

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `domain` | string | Yes | Domain where the view is registered. |
| `key` | string | Yes | Unique key of the view. |
| `version` | string | Yes | Version of the view (semver format). |
| `flow` | string | Yes | Flow type, typically `"sys-views"`. |

## Rule Evaluation

### Evaluation Order

1. Views are evaluated **in array order** (first to last)
2. The **first matching rule** wins and its view is returned
3. An entry **without a rule** acts as a fallback/default
4. Always place the default view **last** in the array

### Available ScriptContext Properties

Inside the `Handler` method, you have access to `ScriptContext` with:

| Property | Type | Description |
|----------|------|-------------|
| `context.Headers` | Dictionary | HTTP request headers |
| `context.QueryParameters` | Dictionary | URL query parameters |
| `context.Instance.Data` | dynamic | Current instance data |
| `context.Instance.Key` | string | Instance key |
| `context.State` | string | Current state key |
| `context.Transition` | string | Current transition key (if applicable) |

## Examples

### Example 1: Platform-Based View Selection

Select different views based on the `x-platform` header:

```json
{
  "key": "checkout-state",
  "stateType": 1,
  "views": [
    {
      "rule": {
        "location": "inline",
        "code": "using System.Threading.Tasks;\nusing BBT.Workflow.Scripting;\npublic class CheckoutIosRule : IConditionMapping\n{\n    public async Task<bool> Handler(ScriptContext context)\n    {\n        try\n        {\n            if (context.Headers?[\"x-platform\"] == \"ios\")\n            {\n                return true;\n            }\n            return false;\n        }\n        catch (Exception ex)\n        {\n            return false;\n        }\n    }\n}",
        "encoding": "NAT"
      },
      "view": {
        "domain": "ecommerce",
        "key": "checkout-ios",
        "version": "1.0.0",
        "flow": "sys-views"
      },
      "loadData": true
    },
    {
      "rule": {
        "location": "inline",
        "code": "using System.Threading.Tasks;\nusing BBT.Workflow.Scripting;\npublic class CheckoutAndroidRule : IConditionMapping\n{\n    public async Task<bool> Handler(ScriptContext context)\n    {\n        try\n        {\n            if (context.Headers?[\"x-platform\"] == \"android\")\n            {\n                return true;\n            }\n            return false;\n        }\n        catch (Exception ex)\n        {\n            return false;\n        }\n    }\n}",
        "encoding": "NAT"
      },
      "view": {
        "domain": "ecommerce",
        "key": "checkout-android",
        "version": "1.0.0",
        "flow": "sys-views"
      },
      "loadData": true
    },
    {
      "view": {
        "domain": "ecommerce",
        "key": "checkout-web",
        "version": "1.0.0",
        "flow": "sys-views"
      },
      "loadData": true
    }
  ]
}
```

### Example 2: Role-Based View Selection

Show different views based on user role from instance data:

```json
{
  "key": "approval-state",
  "stateType": 2,
  "views": [
    {
      "rule": {
        "location": "inline",
        "code": "using System.Threading.Tasks;\nusing BBT.Workflow.Scripting;\npublic class ApprovalAdminRule : IConditionMapping\n{\n    public async Task<bool> Handler(ScriptContext context)\n    {\n        try\n        {\n            if (context.Instance.Data.userRole == \"admin\")\n            {\n                return true;\n            }\n            return false;\n        }\n        catch (Exception ex)\n        {\n            return false;\n        }\n    }\n}",
        "encoding": "NAT"
      },
      "view": {
        "domain": "hr",
        "key": "approval-admin-view",
        "version": "1.0.0",
        "flow": "sys-views"
      },
      "loadData": true,
      "extensions": ["adminActions"]
    },
    {
      "rule": {
        "location": "inline",
        "code": "using System.Threading.Tasks;\nusing BBT.Workflow.Scripting;\npublic class ApprovalManagerRule : IConditionMapping\n{\n    public async Task<bool> Handler(ScriptContext context)\n    {\n        try\n        {\n            if (context.Instance.Data.userRole == \"manager\")\n            {\n                return true;\n            }\n            return false;\n        }\n        catch (Exception ex)\n        {\n            return false;\n        }\n    }\n}",
        "encoding": "NAT"
      },
      "view": {
        "domain": "hr",
        "key": "approval-manager-view",
        "version": "1.0.0",
        "flow": "sys-views"
      },
      "loadData": true
    },
    {
      "view": {
        "domain": "hr",
        "key": "approval-default-view",
        "version": "1.0.0",
        "flow": "sys-views"
      },
      "loadData": true
    }
  ]
}
```

### Example 3: Data-Driven View Selection

Select view based on instance data values:

```json
{
  "key": "payment-state",
  "stateType": 2,
  "views": [
    {
      "rule": {
        "location": "inline",
        "code": "using System.Threading.Tasks;\nusing BBT.Workflow.Scripting;\npublic class HighValuePaymentRule : IConditionMapping\n{\n    public async Task<bool> Handler(ScriptContext context)\n    {\n        try\n        {\n            decimal amount = context.Instance.Data.amount;\n            if (amount > 10000)\n            {\n                return true;\n            }\n            return false;\n        }\n        catch (Exception ex)\n        {\n            return false;\n        }\n    }\n}",
        "encoding": "NAT"
      },
      "view": {
        "domain": "finance",
        "key": "high-value-payment-view",
        "version": "1.0.0",
        "flow": "sys-views"
      },
      "loadData": true,
      "extensions": ["riskAssessment", "managerApproval"]
    },
    {
      "view": {
        "domain": "finance",
        "key": "standard-payment-view",
        "version": "1.0.0",
        "flow": "sys-views"
      },
      "loadData": true
    }
  ]
}
```

### Example 4: Query Parameter Based Selection

Select view based on URL query parameters:

```json
{
  "key": "dashboard-state",
  "stateType": 1,
  "views": [
    {
      "rule": {
        "location": "inline",
        "code": "using System.Threading.Tasks;\nusing BBT.Workflow.Scripting;\npublic class DashboardCompactRule : IConditionMapping\n{\n    public async Task<bool> Handler(ScriptContext context)\n    {\n        try\n        {\n            if (context.QueryParameters?[\"mode\"] == \"compact\")\n            {\n                return true;\n            }\n            return false;\n        }\n        catch (Exception ex)\n        {\n            return false;\n        }\n    }\n}",
        "encoding": "NAT"
      },
      "view": {
        "domain": "portal",
        "key": "dashboard-compact",
        "version": "1.0.0",
        "flow": "sys-views"
      },
      "loadData": true
    },
    {
      "rule": {
        "location": "inline",
        "code": "using System.Threading.Tasks;\nusing BBT.Workflow.Scripting;\npublic class DashboardDetailedRule : IConditionMapping\n{\n    public async Task<bool> Handler(ScriptContext context)\n    {\n        try\n        {\n            if (context.QueryParameters?[\"mode\"] == \"detailed\")\n            {\n                return true;\n            }\n            return false;\n        }\n        catch (Exception ex)\n        {\n            return false;\n        }\n    }\n}",
        "encoding": "NAT"
      },
      "view": {
        "domain": "portal",
        "key": "dashboard-detailed",
        "version": "1.0.0",
        "flow": "sys-views"
      },
      "loadData": true
    },
    {
      "view": {
        "domain": "portal",
        "key": "dashboard-default",
        "version": "1.0.0",
        "flow": "sys-views"
      },
      "loadData": true
    }
  ]
}
```

### Example 5: Complex Rule with Multiple Conditions

Combine multiple conditions in a single rule:

```json
{
  "key": "onboarding-state",
  "stateType": 1,
  "views": [
    {
      "rule": {
        "location": "inline",
        "code": "using System.Threading.Tasks;\nusing BBT.Workflow.Scripting;\npublic class IosFirstTimeUserRule : IConditionMapping\n{\n    public async Task<bool> Handler(ScriptContext context)\n    {\n        try\n        {\n            bool isIos = context.Headers?[\"x-platform\"] == \"ios\";\n            bool isFirstTime = context.Instance.Data.isFirstTimeUser == true;\n            return isIos && isFirstTime;\n        }\n        catch (Exception ex)\n        {\n            return false;\n        }\n    }\n}",
        "encoding": "NAT"
      },
      "view": {
        "domain": "onboarding",
        "key": "ios-first-time-view",
        "version": "1.0.0",
        "flow": "sys-views"
      },
      "loadData": true
    },
    {
      "rule": {
        "location": "inline",
        "code": "using System.Threading.Tasks;\nusing BBT.Workflow.Scripting;\npublic class FirstTimeUserRule : IConditionMapping\n{\n    public async Task<bool> Handler(ScriptContext context)\n    {\n        try\n        {\n            if (context.Instance.Data.isFirstTimeUser == true)\n            {\n                return true;\n            }\n            return false;\n        }\n        catch (Exception ex)\n        {\n            return false;\n        }\n    }\n}",
        "encoding": "NAT"
      },
      "view": {
        "domain": "onboarding",
        "key": "first-time-user-view",
        "version": "1.0.0",
        "flow": "sys-views"
      },
      "loadData": true
    },
    {
      "view": {
        "domain": "onboarding",
        "key": "returning-user-view",
        "version": "1.0.0",
        "flow": "sys-views"
      },
      "loadData": true
    }
  ]
}
```

## Transition Views

The same `views` array format can be used in transitions:

```json
{
  "key": "submit-transition",
  "target": "review-state",
  "triggerType": 0,
  "views": [
    {
      "rule": {
        "location": "inline",
        "code": "using System.Threading.Tasks;\nusing BBT.Workflow.Scripting;\npublic class SubmitMobileRule : IConditionMapping\n{\n    public async Task<bool> Handler(ScriptContext context)\n    {\n        try\n        {\n            string platform = context.Headers?[\"x-platform\"];\n            if (platform == \"ios\" || platform == \"android\")\n            {\n                return true;\n            }\n            return false;\n        }\n        catch (Exception ex)\n        {\n            return false;\n        }\n    }\n}",
        "encoding": "NAT"
      },
      "view": {
        "domain": "forms",
        "key": "submit-mobile-view",
        "version": "1.0.0",
        "flow": "sys-views"
      },
      "loadData": true
    },
    {
      "view": {
        "domain": "forms",
        "key": "submit-desktop-view",
        "version": "1.0.0",
        "flow": "sys-views"
      },
      "loadData": true
    }
  ]
}
```

## Best Practices

1. **Always include a default view**: Place an entry without a `rule` at the end of the array as a fallback.

2. **Order rules from specific to general**: More specific rules should come before general ones.

3. **Keep rules simple**: Complex logic should be handled in extensions or backend services.

4. **Use meaningful view keys**: Name views descriptively (e.g., `checkout-ios`, `approval-admin-view`).

5. **Test all paths**: Ensure each rule path is tested with appropriate conditions.

## Error Handling

- If no rule matches and no default view exists, an error is returned
- Failed rule evaluation is logged and the next rule is evaluated
- Invalid rule syntax causes the rule to fail (continues to next)

## Related Documentation

- [Scripting Engine](./scripting-engine.md) - JavaScript expression syntax
- [Instance Filtering](./instance-filtering.md) - Query parameter usage
