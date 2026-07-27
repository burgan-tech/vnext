namespace BBT.Workflow;

public class DomainConsts
{
    public const string FlowCompleted = "{0}.vnext.flow-completed";
    
    public class MetaDataKeys
    {
        public const string Id = "parent.id";
        public const string Key = "parent.key";
        public const string Domain = "parent.domain";
        public const string Flow = "parent.flow";
        public const string Version = "parent.version";
        public const string State = "parent.state";
        public const string FlowType = "parent.flowtype";
        public const string Transition = "parent.transition";
        public const string Sync = "sync";
        public const string Callback = "callback";
        public const string TimeoutOverride = "subflow.timeout_override";
        public const string TransitionRoleOverrides = "subflow.transition_role_overrides";
        public const string StateRoleOverrides = "subflow.state_role_overrides";
        /// <summary>Root (ancestor) flow instance ID — always carries the original A-flow ID down the entire chain.</summary>
        public const string RootInstanceId = "root.instance.id";
        /// <summary>
        /// JSON array of distributed resource-lock keys acquired by this instance (owner = instance ID).
        /// Recorded on Acquire so the instance's terminal cleanup can release them automatically,
        /// independent of which transition path completes the instance.
        /// </summary>
        public const string ResourceLocks = "resource.locks";
    }
}