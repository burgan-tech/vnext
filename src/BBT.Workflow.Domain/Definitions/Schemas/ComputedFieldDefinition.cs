using System.Text.Json;

namespace BBT.Workflow.Definitions.Schemas;

/// <summary>
/// Publish-time rules for <c>x-computed</c>.
/// <list type="bullet">
/// <item><c>evaluatedBy</c> is <c>runtime</c> or <c>client</c> (<c>server</c> was the proposal's name for
/// <c>runtime</c>). A <c>client</c> declaration is the client's contract and is not inspected further.</item>
/// <item>A <c>runtime</c> field is evaluated on every read of the master schema's data and never stored, so it is
/// refused on a schema whose role is a request body or a component (<c>transition</c>, <c>function</c>,
/// <c>component</c>), outside nested <c>properties</c>, at the root, in its parent's <c>required</c> list, and next to a
/// keyword that acts on a stored value or on the field's own visibility: <c>x-indexed</c>, <c>x-filterOperators</c>,
/// <c>x-sortable</c>, <c>x-storage</c>, <c>x-roles</c>, <c>x-masking</c> or an at-rest <c>x-encryption</c>.
/// (It is computed from what the caller already sees, so it needs no visibility rule of its own.)</item>
/// <item>Shape: at least one of <c>cases</c> / <c>fallback</c>; each case and the fallback is exactly one of
/// <c>join</c> (<c>fields</c> ≥ 1, optional <c>separator</c>) or <c>arithmetic</c> (<c>op</c>, <c>fields</c> ≥ 2); a
/// case <c>when</c> is a condition with a known operator; <c>whenAbsent</c> is <c>empty</c> or <c>null</c>.</item>
/// <item>Every <c>fields</c> entry and condition <c>field</c> names a property of the same parent object (dot-separated
/// for nested members) that is not itself a runtime computed field (no chains) and carries no at-rest
/// <c>x-encryption</c> (a non-exempt caller would see the token).</item>
/// </list>
/// </summary>
public static class ComputedFieldDefinition
{
    private static readonly HashSet<string> DeclarationKeys = new(StringComparer.Ordinal)
        { "evaluatedBy", "cases", "fallback", "whenAbsent" };
    private static readonly HashSet<string> ArithmeticOps = new(StringComparer.Ordinal)
        { "add", "subtract", "multiply", "divide" };
    private static readonly HashSet<string> ConditionOperators = new(StringComparer.Ordinal)
    {
        "equals", "notEquals", "in", "notIn", "isEmpty", "isNotEmpty", "contains", "greaterThan", "lessThan",
        "greaterThanOrEquals", "lessThanOrEquals", "startsWith", "endsWith"
    };

    /// <summary>Validates every <c>x-computed</c> declaration in <paramref name="root"/>.</summary>
    /// <param name="root">The schema document (<c>attributes.schema</c>).</param>
    /// <param name="schemaType">The component's <c>attributes.type</c>.</param>
    /// <returns>One message per violation, each naming the property path; empty when valid.</returns>
    public static IReadOnlyList<string> Validate(JsonElement root, string? schemaType)
    {
        var errors = new List<string>();
        if (root.ValueKind != JsonValueKind.Object)
            return errors;

        if (root.TryGetProperty(ComputedFieldParser.Keyword, out var rootDeclaration))
            ValidateDeclaration(rootDeclaration, "(root)", parent: null, property: root, name: null, reachable: false, schemaType, errors);

        Visit(root, string.Empty, reachable: true, schemaType, errors);
        return errors;
    }

    private static void Visit(JsonElement node, string path, bool reachable, string? schemaType, List<string> errors)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in node.EnumerateArray())
                Visit(child, path, reachable: false, schemaType, errors);
            return;
        }

        if (node.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in node.EnumerateObject())
        {
            if (property.Name.StartsWith("x-", StringComparison.Ordinal) ||
                property.Name is "default" or "examples" or "enum" or "const")
                continue;

            if (property.Name == "properties" && property.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var child in property.Value.EnumerateObject())
                {
                    var childPath = path.Length == 0 ? child.Name : $"{path}.{child.Name}";
                    if (child.Value.ValueKind == JsonValueKind.Object &&
                        child.Value.TryGetProperty(ComputedFieldParser.Keyword, out var declaration))
                        ValidateDeclaration(declaration, childPath, node, child.Value, child.Name, reachable, schemaType, errors);

                    Visit(child.Value, childPath, reachable, schemaType, errors);
                }
            }
            else if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                // items, $defs, combinators, conditionals …: the read path never resolves a computed field there.
                if (property.Value.ValueKind == JsonValueKind.Object &&
                    property.Name is "$defs" or "definitions" or "patternProperties" or "dependentSchemas")
                {
                    foreach (var child in property.Value.EnumerateObject())
                        VisitUnreachable(child.Value, path, schemaType, errors);
                }
                else
                {
                    VisitUnreachable(property.Value, path, schemaType, errors);
                }
            }
        }
    }

    private static void VisitUnreachable(JsonElement node, string path, string? schemaType, List<string> errors)
    {
        if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty(ComputedFieldParser.Keyword, out var declaration))
            ValidateDeclaration(declaration, path.Length == 0 ? "(root)" : path, null, node, null, reachable: false, schemaType, errors);

        Visit(node, path, reachable: false, schemaType, errors);
    }

    private static void ValidateDeclaration(
        JsonElement declaration,
        string where,
        JsonElement? parent,
        JsonElement property,
        string? name,
        bool reachable,
        string? schemaType,
        List<string> errors)
    {
        void Error(string message) => errors.Add($"Field '{where}': {message}");

        if (declaration.ValueKind != JsonValueKind.Object)
        {
            Error("x-computed must be an object.");
            return;
        }

        if (declaration.TryGetProperty("evaluatedBy", out var by))
        {
            var value = by.ValueKind == JsonValueKind.String ? by.GetString() : null;
            if (value == "server")
            {
                Error("x-computed.evaluatedBy 'server' has been renamed; use 'runtime'.");
                return;
            }

            if (value is not (ComputedFieldParser.RuntimeEvaluator or ComputedFieldParser.ClientEvaluator))
            {
                Error("x-computed.evaluatedBy must be 'runtime' or 'client'.");
                return;
            }
        }

        if (!ComputedFieldParser.IsRuntimeEvaluated(declaration))
            return;

        if (SchemaRoleTypes.IsNonRecordRole(schemaType))
            Error($"x-computed evaluatedBy 'runtime' is evaluated on the master schema's data; a '{schemaType}' schema is a request or component contract — use evaluatedBy 'client'.");

        if (!reachable || parent is null || name is null)
        {
            Error("x-computed evaluatedBy 'runtime' is only supported on a property reachable through nested 'properties'; it is not evaluated at the root, under items, $defs, combinators or conditional schemas.");
            return;
        }

        foreach (var key in declaration.EnumerateObject())
        {
            if (!DeclarationKeys.Contains(key.Name))
                Error($"x-computed does not support '{key.Name}'. Allowed: evaluatedBy, cases, fallback, whenAbsent.");
        }

        ValidateCompanions(property, parent.Value, name, Error);

        var hasCases = declaration.TryGetProperty("cases", out var cases);
        var hasFallback = declaration.TryGetProperty("fallback", out var fallback);
        if (!hasCases && !hasFallback)
            Error("x-computed needs 'cases' or a 'fallback'.");

        if (hasCases)
        {
            if (cases.ValueKind != JsonValueKind.Array || cases.GetArrayLength() == 0)
            {
                Error("x-computed.cases must be a non-empty array.");
            }
            else
            {
                var index = 0;
                foreach (var candidate in cases.EnumerateArray())
                {
                    var label = $"x-computed.cases[{index++}]";
                    if (candidate.ValueKind != JsonValueKind.Object)
                    {
                        Error($"{label} must be an object.");
                        continue;
                    }

                    foreach (var key in candidate.EnumerateObject())
                    {
                        if (key.Name is not ("when" or "join" or "arithmetic"))
                            Error($"{label} does not support '{key.Name}'. Allowed: when, join, arithmetic.");
                    }

                    if (candidate.TryGetProperty("when", out var when))
                        ValidateCondition(when, $"{label}.when", parent.Value, Error);

                    ValidateAction(candidate, label, parent.Value, Error);
                }
            }
        }

        if (hasFallback)
        {
            if (fallback.ValueKind != JsonValueKind.Object)
            {
                Error("x-computed.fallback must be an object.");
            }
            else
            {
                foreach (var key in fallback.EnumerateObject())
                {
                    if (key.Name is not ("join" or "arithmetic"))
                        Error($"x-computed.fallback does not support '{key.Name}'. Allowed: join, arithmetic.");
                }

                ValidateAction(fallback, "x-computed.fallback", parent.Value, Error);
            }
        }

        if (declaration.TryGetProperty("whenAbsent", out var whenAbsent) &&
            (whenAbsent.ValueKind != JsonValueKind.String || whenAbsent.GetString() is not ("empty" or "null")))
            Error("x-computed.whenAbsent must be 'empty' or 'null'.");
    }

    private static void ValidateCompanions(JsonElement property, JsonElement parent, string name, Action<string> error)
    {
        if (property.TryGetProperty("x-indexed", out var indexed) && indexed.ValueKind == JsonValueKind.True)
            error("x-computed cannot be combined with x-indexed; the index is built from stored data and a computed field is never stored.");
        if (property.TryGetProperty(FilterOperatorDefinition.Keyword, out var ops) &&
            ops.ValueKind == JsonValueKind.Array && ops.GetArrayLength() > 0)
            error("x-computed cannot be combined with x-filterOperators; list filters run on stored data and a computed field is never stored.");
        if (property.TryGetProperty("x-sortable", out var sortable) && sortable.ValueKind == JsonValueKind.True)
            error("x-computed cannot be combined with x-sortable; sorting runs on stored data and a computed field is never stored.");
        if (property.TryGetProperty(FileStorageSchemaParser.Keyword, out _))
            error("x-computed cannot be combined with x-storage.");
        foreach (var keyword in new[] { "x-roles", SchemaRolesParser.MaskingKey })
        {
            if (property.TryGetProperty(keyword, out _))
                error($"x-computed cannot be combined with {keyword}; the value is computed from what the caller already sees, so it takes no visibility rule of its own.");
        }
        if (property.TryGetProperty(SchemaRolesParser.EncryptionKey, out var encryption) &&
            FieldMaskingDefinition.IsActiveEncryption(encryption))
            error("x-computed cannot be combined with an at-rest x-encryption; a computed field is never stored.");

        if (parent.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array &&
            required.EnumerateArray().Any(r => r.ValueKind == JsonValueKind.String && r.GetString() == name))
            error("a field with x-computed evaluatedBy 'runtime' is never stored and cannot be 'required'.");
    }

    private static void ValidateAction(JsonElement action, string label, JsonElement parent, Action<string> error)
    {
        var hasJoin = action.TryGetProperty("join", out var join);
        var hasArithmetic = action.TryGetProperty("arithmetic", out var arithmetic);
        if (hasJoin == hasArithmetic)
        {
            error($"{label} must declare exactly one of 'join' or 'arithmetic'.");
            return;
        }

        if (hasJoin)
        {
            if (join.ValueKind != JsonValueKind.Object)
            {
                error($"{label}.join must be an object.");
                return;
            }

            foreach (var key in join.EnumerateObject())
            {
                if (key.Name is not ("fields" or "separator"))
                    error($"{label}.join does not support '{key.Name}'. Allowed: fields, separator.");
            }

            if (join.TryGetProperty("separator", out var separator) && separator.ValueKind != JsonValueKind.String)
                error($"{label}.join.separator must be a string.");

            ValidateFields(join, $"{label}.join", minimum: 1, parent, error);
            return;
        }

        if (arithmetic.ValueKind != JsonValueKind.Object)
        {
            error($"{label}.arithmetic must be an object.");
            return;
        }

        foreach (var key in arithmetic.EnumerateObject())
        {
            if (key.Name is not ("op" or "fields"))
                error($"{label}.arithmetic does not support '{key.Name}'. Allowed: op, fields.");
        }

        if (!arithmetic.TryGetProperty("op", out var op) || op.ValueKind != JsonValueKind.String ||
            !ArithmeticOps.Contains(op.GetString()!))
            error($"{label}.arithmetic.op must be 'add', 'subtract', 'multiply' or 'divide'.");

        ValidateFields(arithmetic, $"{label}.arithmetic", minimum: 2, parent, error);
    }

    private static void ValidateFields(JsonElement action, string label, int minimum, JsonElement parent, Action<string> error)
    {
        if (!action.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array ||
            fields.GetArrayLength() < minimum)
        {
            error($"{label}.fields must be an array of at least {minimum} field name(s).");
            return;
        }

        foreach (var field in fields.EnumerateArray())
        {
            if (field.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(field.GetString()))
            {
                error($"{label}.fields entries must be non-empty strings.");
                continue;
            }

            ValidateReference(field.GetString()!, label, parent, error);
        }
    }

    private static void ValidateCondition(JsonElement condition, string label, JsonElement parent, Action<string> error)
    {
        if (condition.ValueKind != JsonValueKind.Object)
        {
            error($"{label} must be a condition object.");
            return;
        }

        foreach (var combinator in new[] { "allOf", "anyOf" })
        {
            if (!condition.TryGetProperty(combinator, out var list))
                continue;
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
            {
                error($"{label}.{combinator} must be a non-empty array of conditions.");
                continue;
            }

            foreach (var child in list.EnumerateArray())
                ValidateCondition(child, $"{label}.{combinator}", parent, error);
            return;
        }

        if (condition.TryGetProperty("not", out var not))
        {
            ValidateCondition(not, $"{label}.not", parent, error);
            return;
        }

        if (!condition.TryGetProperty("field", out var field) || field.ValueKind != JsonValueKind.String ||
            string.IsNullOrEmpty(field.GetString()))
        {
            error($"{label} needs a 'field' and an 'operator' (or allOf / anyOf / not).");
            return;
        }

        if (!condition.TryGetProperty("operator", out var op) || op.ValueKind != JsonValueKind.String ||
            !ConditionOperators.Contains(op.GetString()!))
            error($"{label}.operator must be one of: {string.Join(", ", ConditionOperators)}.");

        ValidateReference(field.GetString()!, label, parent, error);
    }

    private static void ValidateReference(string reference, string label, JsonElement parent, Action<string> error)
    {
        JsonElement? current = parent;
        foreach (var segment in reference.Split('.'))
        {
            if (current is not { ValueKind: JsonValueKind.Object } node ||
                !node.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object ||
                !properties.TryGetProperty(segment, out var next))
            {
                error($"{label} references '{reference}', which is not a property of the same object.");
                return;
            }

            current = next;
        }

        if (current is not { ValueKind: JsonValueKind.Object } target)
            return;

        if (target.TryGetProperty(ComputedFieldParser.Keyword, out var declaration) &&
            ComputedFieldParser.IsRuntimeEvaluated(declaration))
            error($"{label} references '{reference}', which is itself a runtime computed field; computed fields do not chain.");

        if (target.TryGetProperty(SchemaRolesParser.EncryptionKey, out var encryption) &&
            FieldMaskingDefinition.IsActiveEncryption(encryption))
            error($"{label} references '{reference}', which is encrypted at rest; a caller who is not exempt would see the token in the computed value.");
    }
}
