using Microsoft.Extensions.Configuration;

namespace BBT.Workflow.DbMigrator;

/// <summary>What the DbMigrator process has been asked to do.</summary>
public enum MigratorCommandKind
{
    /// <summary>Default and previous-only behavior: migrate every schema forward to the assembly's latest.</summary>
    Migrate,

    /// <summary>Converge schemas to an explicit migration target (rollback, or catch-up to exactly that point).</summary>
    Downgrade,

    /// <summary>Read-only: print each schema's applied head, pending and unknown migrations, then exit.</summary>
    Status
}

/// <summary>
/// The parsed invocation of the DbMigrator. Sources, in precedence order: command-line arguments,
/// then the <c>DbMigrator:*</c> configuration keys — so deployments that cannot pass container args
/// (the current compose/helm templates pass none) can drive the same commands through environment
/// variables (<c>DbMigrator__Command=downgrade</c>, <c>DbMigrator__Target=…</c>).
/// With no arguments and no configured command the migrator behaves exactly as before this command
/// existed: forward migration of everything, at startup.
/// </summary>
public sealed record MigratorCommand
{
    public MigratorCommandKind Kind { get; init; } = MigratorCommandKind.Migrate;

    /// <summary>Target migration (id or name) for the workflow chain — every workflow schema converges to it.</summary>
    public string? Target { get; init; }

    /// <summary>Target migration for the messaging chain (<c>sys_queues</c>), which has its own migration ids.</summary>
    public string? MessagingTarget { get; init; }

    /// <summary>Explicit schema subset; empty = all system schemas plus every domain schema discovered from sys_flows.</summary>
    public IReadOnlyList<string> Schemas { get; init; } = [];

    /// <summary>When set, nothing is applied: the per-schema SQL is written into this directory instead (dry run).</summary>
    public string? ScriptDirectory { get; init; }

    /// <summary>
    /// Required whenever the revert range contains table/column/schema drops — the operator's
    /// explicit acknowledgment that the data those drops destroy will not come back.
    /// </summary>
    public bool AcceptDataLoss { get; init; }

    /// <summary>
    /// Required whenever the revert range removes or reshapes tables/columns THIS build's runtime
    /// model maps — the operator's declaration that the runtime is being rolled back to an older
    /// version right after this command. Without it the command refuses, because the same-version
    /// runtime would start failing (42703/42P01) on every read/write of the affected entities.
    /// </summary>
    public bool ForRuntimeRollback { get; init; }

    public const string Usage =
        """
        Usage:
          (no arguments)      migrate every schema forward to this build's latest (unchanged default)
          status [--schema S]...
          downgrade [--target <migrationIdOrName>] [--messaging-target <migrationIdOrName>]
                    [--schema <name>]... [--script <directory>] [--accept-data-loss]
                    [--for-runtime-rollback]

        Configuration fallbacks (used when the argument is absent):
          DbMigrator:Command, DbMigrator:Target, DbMigrator:MessagingTarget,
          DbMigrator:Schemas (comma-separated), DbMigrator:ScriptDirectory,
          DbMigrator:AcceptDataLoss, DbMigrator:ForRuntimeRollback
        """;

    /// <summary>Parses argv with configuration fallbacks. Throws <see cref="ArgumentException"/> with a message + usage on invalid input.</summary>
    public static MigratorCommand Parse(string[] args, IConfiguration configuration)
    {
        string? commandName = null;
        string? target = null;
        string? messagingTarget = null;
        string? scriptDirectory = null;
        bool? acceptDataLoss = null;
        bool? forRuntimeRollback = null;
        var schemas = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "migrate" or "downgrade" or "status" when commandName is null && !arg.StartsWith("--"):
                    commandName = arg.ToLowerInvariant();
                    break;
                case "--target":
                    target = TakeValue(args, ref i, arg);
                    break;
                case "--messaging-target":
                    messagingTarget = TakeValue(args, ref i, arg);
                    break;
                case "--schema":
                    schemas.Add(TakeValue(args, ref i, arg));
                    break;
                case "--script":
                    scriptDirectory = TakeValue(args, ref i, arg);
                    break;
                case "--accept-data-loss":
                    acceptDataLoss = true;
                    break;
                case "--for-runtime-rollback":
                    forRuntimeRollback = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{arg}'.\n{Usage}");
            }
        }

        commandName ??= configuration["DbMigrator:Command"]?.ToLowerInvariant();
        target ??= configuration["DbMigrator:Target"];
        messagingTarget ??= configuration["DbMigrator:MessagingTarget"];
        scriptDirectory ??= configuration["DbMigrator:ScriptDirectory"];
        acceptDataLoss ??= configuration.GetValue<bool?>("DbMigrator:AcceptDataLoss");
        forRuntimeRollback ??= configuration.GetValue<bool?>("DbMigrator:ForRuntimeRollback");
        if (schemas.Count == 0)
        {
            schemas.AddRange((configuration["DbMigrator:Schemas"] ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        var kind = commandName switch
        {
            null or "migrate" => MigratorCommandKind.Migrate,
            "downgrade" => MigratorCommandKind.Downgrade,
            "status" => MigratorCommandKind.Status,
            _ => throw new ArgumentException($"Unknown command '{commandName}'.\n{Usage}")
        };

        if (kind is MigratorCommandKind.Downgrade && target is null && messagingTarget is null)
            throw new ArgumentException($"'downgrade' needs --target and/or --messaging-target.\n{Usage}");

        if (kind is not MigratorCommandKind.Downgrade
            && (target ?? messagingTarget ?? scriptDirectory) is not null)
            throw new ArgumentException($"--target/--messaging-target/--script apply to 'downgrade' only.\n{Usage}");

        return new MigratorCommand
        {
            Kind = kind,
            Target = target,
            MessagingTarget = messagingTarget,
            Schemas = schemas,
            ScriptDirectory = scriptDirectory,
            AcceptDataLoss = acceptDataLoss ?? false,
            ForRuntimeRollback = forRuntimeRollback ?? false
        };
    }

    private static string TakeValue(string[] args, ref int i, string option)
    {
        if (i + 1 >= args.Length || args[i + 1].StartsWith("--"))
            throw new ArgumentException($"Option '{option}' expects a value.\n{Usage}");
        return args[++i];
    }
}
