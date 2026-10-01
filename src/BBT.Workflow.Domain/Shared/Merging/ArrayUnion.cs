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
/// existing document be updated rather than duplicated. Everything else falls back to whole-value
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
    /// otherwise when the items themselves are equal.
    /// </summary>
    /// <remarks>
    /// Equality on both arms is <see cref="JsonElement.DeepEquals(JsonElement, JsonElement)"/>, never
    /// <c>GetRawText()</c>. Raw text is a FORMATTING comparison, and the two sides are never formatted
    /// the same way: the stored side has been through PostgreSQL <c>jsonb</c>, which re-emits
    /// <c>{"n": "a"}</c> with spaces, while the incoming side is the caller's compact body. Raw text
    /// therefore never matched an id-less object, and a transition that was retried — the normal,
    /// expected thing for a client to do — appended another copy on EVERY resend, growing the array
    /// without bound. Found by running it against a real database; the unit tests could not see it,
    /// because they build the stored side from compact JSON too.
    /// <para>
    /// <see cref="JsonElement.DeepEquals(JsonElement, JsonElement)"/> is structural: insensitive to
    /// whitespace and property order, so the round-trip is invisible; <c>7</c> and <c>"7"</c> stay
    /// DIFFERENT identities, because the kinds differ and the runtime must not coerce across types;
    /// and <c>1</c> and <c>1.0</c> are the SAME identity, which is what an author means by them.
    /// </para>
    /// </remarks>
    public static bool IsSameItem(JsonElement a, JsonElement b)
    {
        if (TryGetId(a, out var idA) && TryGetId(b, out var idB))
        {
            return JsonElement.DeepEquals(idA, idB);
        }

        return JsonElement.DeepEquals(a, b);
    }

    /// <summary>
    /// Reads the item's identity value. The property name is matched case-insensitively; an
    /// <c>id</c> that is itself an object, an array or <c>null</c> is not an identity we can rely
    /// on, and those items fall back to whole-value equality.
    /// </summary>
    private static bool TryGetId(JsonElement element, out JsonElement id)
    {
        id = default;
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

            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Null)
            {
                return false;
            }

            id = property.Value;
            return true;
        }

        return false;
    }
}
