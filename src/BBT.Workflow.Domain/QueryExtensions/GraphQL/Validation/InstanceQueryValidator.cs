using System.Text.Json;
using System.Text.RegularExpressions;
using BBT.Workflow.Security;

namespace BBT.Workflow.Definitions.GraphQL.Validation;

/// <summary>
/// Boundary validator for instance-query parameters. Rejects any filter, sort, groupBy or
/// aggregation the runtime cannot execute exactly as authored.
/// </summary>
/// <remarks>
/// <para>
/// This exists because filter parsing used to fail <em>open</em>: an unsupported operator was
/// dropped by the JSON converter, a malformed filter was swallowed by a repository fallback, and a
/// truncated filter was never parsed at all. In each case the query ran unfiltered and answered
/// HTTP 200 — a caller asking to narrow a result set silently got everything.
/// </para>
/// <para>
/// Run this before building the query. It is pure and allocation-light on the happy path: a valid
/// request costs one parse per supplied parameter and allocates no error list.
/// </para>
/// </remarks>
public static class InstanceQueryValidator
{
    /// <summary>Field-path segment rule, mirroring <c>GraphQLJsonFilterService.IsSafeJsonPath</c>.</summary>
    private static readonly Regex SafePathSegment = new(
        "^[a-zA-Z0-9_]+$",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    private const string AttributesPrefix = "attributes.";

    /// <summary>Top-level properties recognized on a <see cref="GraphQLFilterRequest"/> envelope.</summary>
    private static readonly HashSet<string> EnvelopeProperties =
        new(StringComparer.OrdinalIgnoreCase) { "filter", "groupBy", "aggregations", "orderBy" };

    /// <summary>Matches the leniency of <see cref="GraphQLFilterParser"/>'s serializer options.</summary>
    private static readonly JsonDocumentOptions EnvelopeDocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>
    /// Validates every supplied parameter and returns all reasons for rejection.
    /// </summary>
    /// <param name="request">Parameters to validate. Blank parameters are skipped.</param>
    /// <returns>
    /// <see cref="FilterValidationResult.Valid"/> when the request can be executed as authored;
    /// otherwise a failing result carrying up to <see cref="FilterValidationResult.MaxErrors"/> errors.
    /// </returns>
    public static FilterValidationResult Validate(InstanceQueryValidationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new List<FilterValidationError>();

        ValidateFilterParameter(request, errors);
        ValidateSortParameter(request.Sort, errors);
        ValidateGroupByParameter(request.GroupBy, errors);
        ValidateAggregationsParameter(request.Aggregations, errors);

        return errors.Count == 0 ? FilterValidationResult.Valid : FilterValidationResult.Invalid(errors);
    }

    #region Filter

    private static void ValidateFilterParameter(InstanceQueryValidationRequest request, List<FilterValidationError> errors)
    {
        var filter = request.Filter;
        if (string.IsNullOrWhiteSpace(filter))
            return;

        try
        {
            InputValidator.ValidateFilters(filter);
        }
        catch (ArgumentException ex)
        {
            Add(errors, "filter.tooLong", ex.Message, "filter");
            return;
        }

        var format = FilterFormatDetector.DetectFormat(filter);
        switch (format)
        {
            case FilterFormat.Legacy:
                // DetectFormat's regex already whitelists the legacy operator set, so a legacy
                // filter is executable on the plain list path. The aggregation path is different:
                // it feeds the filter straight into the GraphQL parser without converting it.
                if (!string.IsNullOrWhiteSpace(request.GroupBy) || !string.IsNullOrWhiteSpace(request.Aggregations))
                {
                    Add(errors, "filter.legacyNotAggregatable",
                        "The legacy 'field=operator:value' filter format cannot be combined with groupBy or " +
                        "aggregations. Express the filter as GraphQL-style JSON instead.",
                        "filter");
                }

                return;

            case FilterFormat.Empty:
                // Non-blank yet unrecognized: the classic truncated filter (missing the closing
                // brace). The runtime would apply no filter at all and return every row.
                Add(errors, "filter.unrecognizedFormat",
                    "Filter is not valid JSON and does not match the legacy 'field=operator:value' format. " +
                    "Check for a missing or unbalanced brace.",
                    "filter");
                return;
        }

        if (TryValidateAsEnvelope(request, filter, errors))
            return;

        GraphQLFilterNode? node;
        try
        {
            node = GraphQLFilterParser.ParseFilter(filter);
        }
        catch (ArgumentException ex)
        {
            Add(errors, "filter.invalidJson", ex.Message, "filter");
            return;
        }

        ValidateFilterNode(node, "filter", errors);
    }

    /// <summary>
    /// Handles the <c>{"filter":…,"groupBy":…,"aggregations":…}</c> envelope shape.
    /// </summary>
    /// <returns>True when the string was handled as an envelope and needs no further validation.</returns>
    private static bool TryValidateAsEnvelope(
        InstanceQueryValidationRequest request,
        string filter,
        List<FilterValidationError> errors)
    {
        var looksLikeEnvelope = LooksLikeEnvelope(filter);

        GraphQLFilterRequest? envelope;
        try
        {
            envelope = GraphQLFilterParser.ParseRequestEnvelope(filter);
        }
        catch (ArgumentException ex)
        {
            if (!looksLikeEnvelope)
                return false; // Not an envelope; let bare-node parsing produce the precise error.

            Add(errors, "filter.invalidJson", ex.Message, "filter");
            return true;
        }

        // Mirror GraphQLFilterParser.TryParseRequest so the validator and the runtime agree on
        // which strings are envelopes.
        var isEnvelope = envelope != null &&
                         (envelope.GroupBy != null ||
                          envelope.Aggregations != null ||
                          (envelope.Filter != null && looksLikeEnvelope));

        if (!isEnvelope)
            return false;

        ValidateEnvelopeProperties(filter, errors);

        // The app service unwraps the envelope only when no separate groupBy parameter was given,
        // so supplying both silently discards the envelope's grouping and aggregations.
        if (!string.IsNullOrWhiteSpace(request.GroupBy) || !string.IsNullOrWhiteSpace(request.Aggregations))
        {
            Add(errors, "filter.ambiguousEnvelope",
                "The filter carries groupBy/aggregations and they were also supplied as separate query " +
                "parameters. Provide them in one place only.",
                "filter");
        }

        ValidateFilterNode(envelope!.Filter, "filter.filter", errors);
        ValidateGroupByRequest(envelope.GroupBy, "filter.groupBy", errors);
        ValidateAggregationRequest(envelope.Aggregations, "filter.aggregations", errors);
        ValidateOrderByRequest(envelope.OrderBy, "filter.orderBy", errors);
        return true;
    }

    /// <summary>
    /// Rejects unrecognized top-level properties on an envelope.
    /// </summary>
    /// <remarks>
    /// <see cref="GraphQLFilterRequest"/> cannot use <c>JsonUnmappedMemberHandling.Disallow</c> —
    /// <see cref="GraphQLFilterParser.TryParseRequest"/> deserializes arbitrary filter strings as an
    /// envelope to sniff the format, so unknown members must be tolerated there. That tolerance is
    /// what lets a misspelled <c>filter</c> key (<c>{"fitler":…,"groupBy":…}</c>) drop the caller's
    /// conditions and aggregate over every instance. Checking the raw top-level keys closes it
    /// without touching format detection.
    /// </remarks>
    private static void ValidateEnvelopeProperties(string filter, List<FilterValidationError> errors)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(filter, EnvelopeDocumentOptions);
        }
        catch (JsonException)
        {
            return; // Already parsed successfully as an envelope; nothing further to check.
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return;

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (EnvelopeProperties.Contains(property.Name))
                    continue;

                Add(errors, "filter.unknownProperty",
                    $"'{property.Name}' is not a recognized filter envelope property. Expected 'filter', " +
                    "'groupBy', 'aggregations' or 'orderBy'.",
                    $"filter.{property.Name}");
            }
        }
    }

    private static bool LooksLikeEnvelope(string filter)
    {
        var lowered = filter.ToLowerInvariant();
        return lowered.Contains("\"groupby\"", StringComparison.Ordinal) ||
               lowered.Contains("\"aggregations\"", StringComparison.Ordinal);
    }

    private static void ValidateFilterNode(GraphQLFilterNode? node, string path, List<FilterValidationError> errors)
    {
        if (node == null)
            return;

        if (IsFull(errors))
            return;

        if (node.UnrecognizedProperties is { Count: > 0 })
        {
            foreach (var property in node.UnrecognizedProperties)
            {
                Add(errors, "filter.unknownProperty",
                    $"'{property}' is not a recognized filter property. Expected 'and', 'or', 'not', 'attributes', or a field name mapped to an operator object.",
                    $"{path}.{property}");
            }
        }

        // An authored-but-empty logical operator collapses NodeType to Empty, so check the raw
        // collections before switching on it.
        if (node.And is { Count: 0 })
            Add(errors, "filter.emptyLogicalOperator", "The 'and' operator requires at least one condition.", $"{path}.and");

        if (node.Or is { Count: 0 })
            Add(errors, "filter.emptyLogicalOperator", "The 'or' operator requires at least one condition.", $"{path}.or");

        switch (node.NodeType)
        {
            case FilterNodeType.And:
                foreach (var child in node.And!)
                    ValidateFilterNode(child, $"{path}.and", errors);
                break;

            case FilterNodeType.Or:
                foreach (var child in node.Or!)
                    ValidateFilterNode(child, $"{path}.or", errors);
                break;

            case FilterNodeType.Not:
                ValidateFilterNode(node.Not, $"{path}.not", errors);
                break;

            case FilterNodeType.Condition:
                foreach (var (fieldName, condition) in node.Attributes!)
                    ValidateFieldCondition(fieldName, condition, path, errors);
                break;

            default:
                // An Empty node — `{}` or `{"attributes":{}}` — is the "no filters selected" idiom
                // and legitimately means no restriction. GraphQLJsonFilterService's own guard says
                // the same thing, so rejecting it here would make the two contradict each other.
                // Constructs that were authored but carry nothing (`{"and":[]}`, a field with no
                // operator, an unknown property) are reported above and below instead.
                break;
        }
    }

    private static void ValidateFieldCondition(
        string fieldName,
        FieldCondition condition,
        string path,
        List<FilterValidationError> errors)
    {
        if (IsFull(errors))
            return;

        if (string.IsNullOrWhiteSpace(fieldName))
        {
            Add(errors, "filter.emptyFieldName", "Filter field name cannot be empty.", path);
            return;
        }

        var fieldPath = $"{path}.{fieldName}";
        var reportedUnknownOperator = false;

        if (condition.UnrecognizedOperators is { Count: > 0 })
        {
            foreach (var unrecognized in condition.UnrecognizedOperators)
            {
                reportedUnknownOperator = true;

                if (string.IsNullOrEmpty(unrecognized.Name))
                {
                    Add(errors, "filter.emptyOperatorName",
                        $"Field '{fieldName}' has an operator with an empty name.",
                        fieldPath);
                    continue;
                }

                var suggestion = FilterOperators.Suggest(unrecognized.Name);
                var message = suggestion != null
                    ? $"Operator '{unrecognized.Name}' is not supported on field '{fieldName}'. Did you mean '{suggestion}'?"
                    : $"Operator '{unrecognized.Name}' is not supported on field '{fieldName}'. Supported operators: {FilterOperators.SupportedList}.";

                Add(errors, "filter.unknownOperator", message, $"{fieldPath}.{unrecognized.Name}");
            }
        }

        if (reportedUnknownOperator)
            return;

        var hasOperator = condition.GetOperators().Any();
        var hasNested = condition.NestedConditions is { Count: > 0 };

        if (!hasOperator && !hasNested)
        {
            Add(errors, "filter.noOperator",
                $"Field '{fieldName}' must specify at least one operator. Supported operators: {FilterOperators.SupportedList}.",
                fieldPath);
        }
    }

    #endregion

    #region Sort

    private static void ValidateSortParameter(string? sort, List<FilterValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(sort))
            return;

        OrderByRequest? parsed;
        try
        {
            parsed = GraphQLFilterParser.ParseOrderBy(sort);
        }
        catch (ArgumentException ex)
        {
            Add(errors, "sort.invalidJson", ex.Message, "sort");
            return;
        }

        if (parsed == null)
        {
            Add(errors, "sort.emptyField",
                "Sort was supplied but names no field. Provide {\"field\":\"…\"} or {\"fields\":[{\"field\":\"…\"}]}.",
                "sort");
            return;
        }

        ValidateOrderByRequest(parsed, "sort", errors);
    }

    private static void ValidateOrderByRequest(OrderByRequest? orderBy, string path, List<FilterValidationError> errors)
    {
        if (orderBy == null || IsFull(errors))
            return;

        if (orderBy.Fields is { Count: > 0 })
        {
            for (var i = 0; i < orderBy.Fields.Count; i++)
            {
                var entry = orderBy.Fields[i];
                var entryPath = $"{path}.fields[{i}]";

                if (string.IsNullOrWhiteSpace(entry.Field))
                {
                    Add(errors, "sort.emptyField", $"Sort entry at index {i} names no field.", entryPath);
                    continue;
                }

                ValidateSortDirection(entry.Direction, entryPath, errors);
                ValidateSortField(entry.Field!, entryPath, errors);
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(orderBy.Field))
        {
            Add(errors, "sort.emptyField", "Sort names no field.", path);
            return;
        }

        ValidateSortDirection(orderBy.Direction, path, errors);
        ValidateSortField(orderBy.Field!, path, errors);
    }

    private static void ValidateSortDirection(string? direction, string path, List<FilterValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(direction))
            return; // Absent means ascending.

        var trimmed = direction.Trim();
        if (trimmed.Equals("asc", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("desc", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Add(errors, "sort.invalidDirection",
            $"Sort direction '{direction}' is not valid. Use 'asc' or 'desc'.",
            $"{path}.direction");
    }

    private static void ValidateSortField(string field, string path, List<FilterValidationError> errors)
    {
        var trimmed = field.Trim();

        if (trimmed.StartsWith(AttributesPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var jsonPath = trimmed[AttributesPrefix.Length..].Trim();
            if (jsonPath.Length == 0 || !IsSafePath(jsonPath))
            {
                Add(errors, "sort.unsafePath",
                    $"Sort field '{field}' is not a valid attributes path. Each segment must contain only letters, digits or underscores.",
                    $"{path}.field");
            }

            return;
        }

        if (!InstanceFieldDiscriminator.IsInstanceColumn(trimmed))
        {
            Add(errors, "sort.unknownField",
                $"Sort field '{field}' is neither an instance column nor an 'attributes.' path. " +
                $"Instance columns: {string.Join(", ", InstanceFieldDiscriminator.GetSupportedColumns())}.",
                $"{path}.field");
        }
    }

    private static bool IsSafePath(string path)
    {
        try
        {
            return path.Split('.').All(segment => SafePathSegment.IsMatch(segment.Trim()));
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    #endregion

    #region GroupBy and aggregations

    private static void ValidateGroupByParameter(string? groupBy, List<FilterValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(groupBy))
            return;

        GroupByRequest? parsed;
        try
        {
            parsed = GraphQLFilterParser.ParseGroupBy(groupBy);
        }
        catch (ArgumentException ex)
        {
            Add(errors, "groupBy.invalidJson", ex.Message, "groupBy");
            return;
        }

        if (parsed == null)
        {
            Add(errors, "groupBy.noFields", "GroupBy was supplied but names no field.", "groupBy");
            return;
        }

        ValidateGroupByRequest(parsed, "groupBy", errors);
    }

    private static void ValidateGroupByRequest(GroupByRequest? groupBy, string path, List<FilterValidationError> errors)
    {
        if (groupBy == null || IsFull(errors))
            return;

        var fields = groupBy.GetFields();
        if (fields.Count == 0)
        {
            Add(errors, "groupBy.noFields", "GroupBy requires at least one field.", path);
        }
        else
        {
            foreach (var field in fields)
                ValidateAggregatableField(field, "groupBy.invalidField", $"{path}.fields", errors);
        }

        ValidateAggregationRequest(groupBy.Aggregations, $"{path}.aggregations", errors);
    }

    private static void ValidateAggregationsParameter(string? aggregations, List<FilterValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(aggregations))
            return;

        AggregationRequest? parsed;
        try
        {
            parsed = GraphQLFilterParser.ParseAggregations(aggregations);
        }
        catch (ArgumentException ex)
        {
            Add(errors, "aggregations.invalidJson", ex.Message, "aggregations");
            return;
        }

        if (parsed == null)
        {
            Add(errors, "aggregations.empty", "Aggregations were supplied but request no function.", "aggregations");
            return;
        }

        ValidateAggregationRequest(parsed, "aggregations", errors, requireAny: true);
    }

    private static void ValidateAggregationRequest(
        AggregationRequest? aggregations,
        string path,
        List<FilterValidationError> errors,
        bool requireAny = false)
    {
        if (aggregations == null || IsFull(errors))
            return;

        if (!aggregations.HasAggregations)
        {
            if (requireAny)
            {
                Add(errors, "aggregations.empty",
                    "Aggregations request no function. Supported functions: count, sum, avg, min, max.",
                    path);
            }

            return;
        }

        ValidateAggregationCount(aggregations.Count, path, errors);
        ValidateAggregatableField(aggregations.Sum, "aggregations.invalidField", $"{path}.sum", errors);
        ValidateAggregatableField(aggregations.Avg, "aggregations.invalidField", $"{path}.avg", errors);
        ValidateAggregatableField(aggregations.Min, "aggregations.invalidField", $"{path}.min", errors);
        ValidateAggregatableField(aggregations.Max, "aggregations.invalidField", $"{path}.max", errors);
    }

    /// <summary>
    /// <c>count</c> is either a boolean (COUNT(*)) or a field name (COUNT(field)). Anything else —
    /// a number, an array, an object — produces no SQL and silently drops the aggregation.
    /// </summary>
    private static void ValidateAggregationCount(object? count, string path, List<FilterValidationError> errors)
    {
        if (count == null)
            return;

        var countPath = $"{path}.count";

        if (count is JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.True:
                case JsonValueKind.False:
                    return;
                case JsonValueKind.String:
                    ValidateAggregatableField(element.GetString(), "aggregations.invalidCount", countPath, errors);
                    return;
                default:
                    Add(errors, "aggregations.invalidCount",
                        $"Aggregation 'count' must be true, false, or a field name; got a JSON {element.ValueKind.ToString().ToLowerInvariant()}.",
                        countPath);
                    return;
            }
        }

        if (count is bool)
            return;

        if (count is string countField)
        {
            ValidateAggregatableField(countField, "aggregations.invalidCount", countPath, errors);
            return;
        }

        Add(errors, "aggregations.invalidCount",
            "Aggregation 'count' must be true, false, or a field name.",
            countPath);
    }

    private static void ValidateAggregatableField(
        string? field,
        string errorCode,
        string path,
        List<FilterValidationError> errors)
    {
        if (field == null)
            return;

        try
        {
            InputValidator.ValidateFieldName(field);
        }
        catch (ArgumentException ex)
        {
            Add(errors, errorCode, ex.Message, path);
        }
    }

    #endregion

    private static bool IsFull(List<FilterValidationError> errors) => errors.Count >= FilterValidationResult.MaxErrors;

    private static void Add(List<FilterValidationError> errors, string code, string message, string? target)
    {
        if (IsFull(errors))
            return;

        errors.Add(new FilterValidationError(code, message, target));
    }
}
