using System.Text.Json;

namespace BBT.Workflow.Shared.Merging;

/// <summary>
/// The ONE definition of how <c>arrayMerge: M</c> folds an incoming array into the stored one
/// (vnext-client-sdk-core#58, AB-18). Both append pipelines call in here — the live
/// <see cref="JsonCanonicalizer"/> path and the legacy <c>ObjectMerger</c> kill-switch path — so the
/// two cannot drift apart and break their byte-parity contract.
/// </summary>
/// <remarks>
/// <para>
/// Semantics: the stored order is preserved; an incoming item that MATCHES one already present
/// replaces it IN PLACE (so an edit does not reorder the list); anything unmatched is appended in
/// the order it arrived. Re-sending an identical payload is therefore idempotent.
/// </para>
/// <para>
/// Matching is by <c>id</c> when BOTH items are objects that carry one — that is what lets an
/// existing document be updated rather than duplicated. Everything else falls back to exact value
/// equality, which is the right rule for arrays of scalars (<c>tags</c>, codes) and the only rule
/// available when items have no identity. Consequence worth knowing: an array of objects WITHOUT
/// an <c>id</c> cannot express an update — an edited item does not match its predecessor and is
/// appended as a new entry.
/// </para>
/// </remarks>
public static class ArrayUnion
{
    /// <summary>The conventional identity property. Matched case-insensitively.</summary>
    public const string IdPropertyName = "id";

    /// <summary>
    /// Folds <paramref name="source"/> into <paramref name="target"/> per the rules above.
    /// </summary>
    public static List<JsonElement> Union(JsonElement target, JsonElement source)
    {
        var result = new List<JsonElement>();
        foreach (var item in target.EnumerateArray())
        {
            result.Add(item);
        }

        foreach (var incoming in source.EnumerateArray())
        {
            var matchIndex = IndexOfMatch(result, incoming);
            if (matchIndex >= 0)
            {
                result[matchIndex] = incoming; // the incoming item wins for that identity
            }
            else
            {
                result.Add(incoming);
            }
        }

        return result;
    }

    private static int IndexOfMatch(List<JsonElement> existing, JsonElement incoming)
    {
        for (var i = 0; i < existing.Count; i++)
        {
            if (IsSameItem(existing[i], incoming))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Two items are the same when both are objects carrying an <c>id</c> and those ids are equal;
    /// otherwise when their raw JSON is identical.
    /// </summary>
    public static bool IsSameItem(JsonElement a, JsonElement b)
    {
        if (TryGetId(a, out var idA) && TryGetId(b, out var idB))
        {
            return string.Equals(idA, idB, StringComparison.Ordinal);
        }

        return string.Equals(a.GetRawText(), b.GetRawText(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads the item's identity as a string. A JSON string id is compared by its VALUE and a
    /// number by its text, so <c>"7"</c> and <c>7</c> are deliberately different identities — the
    /// authored data decides, the runtime does not coerce.
    /// </summary>
    private static bool TryGetId(JsonElement element, out string id)
    {
        id = string.Empty;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, IdPropertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = property.Value;
            // An id that is itself an object/array is not an identity we can rely on.
            if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Null)
            {
                return false;
            }

            id = value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();
            return true;
        }

        return false;
    }
}
