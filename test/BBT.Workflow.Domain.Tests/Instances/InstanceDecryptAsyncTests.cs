using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// <see cref="Instance.Data"/> is always the row as stored — an <c>x-encryption.type: "encrypt"</c> field is its token, on the
/// live aggregate and on a script snapshot alike. A script opens its OWN instance's value with
/// <see cref="Instance.DecryptAsync"/>, which takes a PATH, never a value, through the protector the script context bound.
/// </summary>
public sealed class InstanceDecryptAsyncTests
{
    private const string Token = "ENCRYPTED:AES256:i1:AQIDBAUGBwgJCgsMDQ4PEBESExQ";
    private const string Stored = $$"""{ "vault": { "email": "{{Token}}" }, "label": "plain" }""";
    private const string Plain = """{ "vault": { "email": "user@example.com" }, "label": "plain" }""";

    private static (Instance Live, Instance Script, FakeProtector Protector) Setup(bool openable = true)
    {
        var live = InstanceFactory.CreateDefault("decrypt");
        live.SeedStoredData(Stored);
        var protector = new FakeProtector(openable);
        var script = live.CreateSnapshot();
        script.BindDecryption(protector, "flow_schema");
        return (live, script, protector);
    }

    [Fact]
    public void DataIsTheRowAsStored_OnTheLiveAggregateAndOnTheScriptSnapshot()
    {
        var (live, script, _) = Setup();

        ((string)live.Data!.vault.email).ShouldBe(Token);
        ((string)script.Data!.vault.email).ShouldBe(Token);
        live.LatestData!.Data.Json.ShouldBe(script.LatestData!.Data.Json);
    }

    [Fact]
    public async Task DecryptAsync_OpensTheInstancesOwnEncryptedPath()
    {
        var (_, script, protector) = Setup();

        (await script.DecryptAsync("vault.email")).ShouldBe("user@example.com");
        protector.LastSchema.ShouldBe("flow_schema");
    }

    [Theory]
    [InlineData("label")]          // a plain field
    [InlineData("vault.missing")]  // no such path
    [InlineData("")]
    [InlineData(Token)]            // a token string passed as if it were a path
    public async Task DecryptAsync_AnythingButAnOwnEncryptedPath_IsNull(string path)
        => (await Setup().Script.DecryptAsync(path)).ShouldBeNull();

    [Fact]
    public async Task DecryptAsync_AValueThatCannotBeOpened_IsNull_NeverTheToken()
        => (await Setup(openable: false).Script.DecryptAsync("vault.email")).ShouldBeNull();

    /// <summary>Without a bound protector (the live aggregate, a host without the keyring) nothing is ever decrypted.</summary>
    [Fact]
    public async Task DecryptAsync_WithoutABoundProtector_IsNull()
        => (await Setup().Live.DecryptAsync("vault.email")).ShouldBeNull();

    /// <summary>Several fields of one row: the row is opened once.</summary>
    [Fact]
    public async Task DecryptAsync_OpensTheRowOnce_ForSeveralCalls()
    {
        var (_, script, protector) = Setup();

        await script.DecryptAsync("vault.email");
        await script.DecryptAsync("vault.email");

        protector.Calls.ShouldBe(1);
    }

    /// <summary>
    /// The caller's token reaches the load and is not kept; a cancelled call throws (not swallowed, not turned into null)
    /// and caches nothing, so a later call with a live token still opens the value.
    /// </summary>
    [Fact]
    public async Task DecryptAsync_ACancelledToken_Throws_AndALaterCallStillWorks()
    {
        var (_, script, protector) = Setup();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => script.DecryptAsync("vault.email", cts.Token));
        protector.LastToken.ShouldBe(cts.Token);

        (await script.DecryptAsync("vault.email")).ShouldBe("user@example.com");
    }

    /// <summary>A copy of a script snapshot (parallel branch, refresh) keeps the ability to decrypt.</summary>
    [Fact]
    public async Task ACopyOfTheScriptSnapshot_KeepsTheDecryptor()
        => (await Setup().Script.CreateSnapshot().DecryptAsync("vault.email")).ShouldBe("user@example.com");

    private sealed class FakeProtector(bool openable) : IInstanceDataProtector
    {
        public int Calls { get; private set; }
        public string? LastSchema { get; private set; }
        public CancellationToken LastToken { get; private set; }

        public Task<InstanceDataView> UnprotectAsync(
            string? schema, Guid instanceId, JsonData stored, CancellationToken cancellationToken = default)
        {
            LastSchema = schema;
            LastToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(openable
                ? new InstanceDataView(new JsonData(Plain), new Dictionary<string, string> { ["vault.email"] = Token })
                : new InstanceDataView(stored, undecryptable: new HashSet<string> { "vault.email" }));
        }
    }
}
