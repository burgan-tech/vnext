namespace BBT.Workflow.Definitions.Functions
{
    /// <summary>
    /// Well-known system function keys (state, view, data, schema, authorize, permissions).
    /// </summary>
    public static class FunctionTypeConst
    {
        public const string Longpooling = "state";
        public const string View = "view";
        public const string Data = "data";
        public const string Schema = "schema";
        public const string Extensions = "extensions";
        /// <summary>System function: returns allow/deny for given role, transitionKey, privilege.</summary>
        public const string Authorize = "authorize";
        /// <summary>System function: returns authorization matrix for the workflow.</summary>
        public const string AuthorizationMatrix = "permissions";
        /// <summary>System function: returns runtime hierarchy of an instance as recursive tree.</summary>
        public const string Hierarchy = "hierarchy";
        /// <summary>System function: returns active instances with Human subtype assigned to the current user.</summary>
        public const string HumanTask = "human-task";
        /// <summary>System function: returns the flow-level master schema the instance is bound to (forwards to the active subflow when present).</summary>
        public const string Master = "master";
        /// <summary>System function: returns the workflow's declared functions, each linked to its info endpoint.</summary>
        public const string Catalog = "catalog";
        /// <summary>System function: returns the instance's task execution journal (metadata only).</summary>
        public const string TaskHistory = "task-history";
        /// <summary>System function: returns the recorded actions (execution sub-steps) of one task journal row.</summary>
        public const string ActionHistory = "action-history";
    }
}