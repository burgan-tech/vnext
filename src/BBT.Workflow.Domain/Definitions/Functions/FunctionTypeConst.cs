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
    }
}