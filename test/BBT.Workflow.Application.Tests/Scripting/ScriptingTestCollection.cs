using Xunit;

namespace BBT.Workflow.Scripting;

/// <summary>
/// Defines a test collection to ensure scripting tests run sequentially.
/// This prevents test interference when using shared compiled script caches.
/// </summary>
[CollectionDefinition("ScriptingTests", DisableParallelization = true)]
public class ScriptingTestCollection
{
    // This class has no code, and is never created. Its purpose is simply
    // to be the place to apply [CollectionDefinition] and all the
    // ICollectionFixture<> interfaces.
}

