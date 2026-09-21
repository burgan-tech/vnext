namespace BBT.Workflow.Instances;

/// <summary>
/// The one spelling of the human-task selection predicate.
/// </summary>
/// <remarks>
/// <para>
/// Three places have to agree on this text: the query
/// (<see cref="EfCoreInstanceRepository.GetHumanTaskInstancesAsync"/>), the EF model's partial
/// index filter, and the migration that creates that index. PostgreSQL discharges partial-index
/// applicability by proving implication over the predicate's parse tree, and it cannot match a
/// re-spelling — a stray space or a reordered conjunct silently costs the index and turns the
/// fan-out into a sequential scan per flow schema, on a warm path, with no error anywhere.
/// Sharing the constant makes that divergence unrepresentable rather than merely tested.
/// </para>
/// <para>
/// Every term is a compile-time enum constant inlined as a literal. There is no injection surface:
/// nothing here comes from a request. Parameterizing them instead would make the implication
/// provable only under a custom plan, so an environment with Npgsql auto-preparation enabled would
/// lose the index on the warm path and only there.
/// </para>
/// <para>
/// <c>Type IN ('R','P')</c> replaces an existence probe that cast the <c>text</c>
/// <c>ExtraProperties</c> column to <c>jsonb</c> on every row of every write. <c>'P'</c> is
/// included because a SubProcess is fire-and-forget — nothing projects its state onto an ancestor,
/// so it is its own unit of work and its human states were invisible. <c>'S'</c> stays excluded:
/// its state is projected onto the root that represents it, and listing both would duplicate one
/// task.
/// </para>
/// <para>
/// <c>Status IN ('A','B') AND EffectiveStatus = 'A'</c> is the SQL spelling of the served
/// <c>Instance.GetEffectiveStatus</c> clamp. The <c>Status</c> term is not redundant: a level
/// cancelled while a correlation was open keeps a live child's <c>'A'</c> in the raw column for
/// every row written before that was repaired at the source, and <c>Fault</c> still leaves one
/// deliberately so a retry can resume. Without it the list would offer cancelled and faulted cases,
/// carrying their customer-identifying <c>humanTask</c> text, as open tasks.
/// </para>
/// </remarks>
internal static class HumanTaskQuerySql
{
    /// <summary>
    /// The predicate, without the <c>WHERE</c> keyword — the form <c>HasFilter</c> and
    /// <c>CREATE INDEX … WHERE</c> both take.
    /// </summary>
    internal const string Predicate =
        "\"Type\" IN ('R', 'P')"
        + " AND \"Status\" IN ('A', 'B')"
        + " AND \"EffectiveStatus\" = 'A'"
        + " AND \"EffectiveStateSubType\" = 6";
}
