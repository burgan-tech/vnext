using BBT.Workflow.Execution.RuntimeCaller;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.RuntimeCaller;

/// <summary>
/// The host's runtime-caller identity: signs this runtime's calls to a peer's runtime-internal
/// endpoints and verifies the token on this runtime's own gated endpoints (vnext#1265).
/// Singleton; the key ring is read once at construction (rotation restarts the hosts).
/// </summary>
public sealed class RuntimeCallerIdentity : IRuntimeCallerSigner, IRuntimeCallerVerifier
{
    private readonly RuntimeCallerKeyRing? _ring;
    private readonly int _maxClockSkewSeconds;
    private readonly IRuntimeInfoProvider _runtimeInfo;
    private readonly TimeProvider _timeProvider;

    /// <summary>Loads the key ring named by <see cref="RuntimeCallerOptions.KeyFile"/>, if any.</summary>
    public RuntimeCallerIdentity(
        IOptions<RuntimeCallerOptions> options,
        IRuntimeInfoProvider runtimeInfo,
        ILogger<RuntimeCallerIdentity> logger,
        TimeProvider? timeProvider = null)
    {
        var value = options.Value;
        _maxClockSkewSeconds = value.MaxClockSkewSeconds;
        _runtimeInfo = runtimeInfo;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _ring = RuntimeCallerKeyRingLoader.Load(value.KeyFile);
        if (_ring is not null)
            logger.RuntimeCallerIdentityLoaded(_ring.ActiveKeyId, _ring.Count);
    }

    /// <summary>Test seam: an identity over an in-memory ring.</summary>
    internal RuntimeCallerIdentity(
        RuntimeCallerKeyRing? ring,
        IRuntimeInfoProvider runtimeInfo,
        TimeProvider timeProvider,
        int maxClockSkewSeconds = RuntimeCallerToken.DefaultMaxClockSkewSeconds)
    {
        _ring = ring;
        _runtimeInfo = runtimeInfo;
        _timeProvider = timeProvider;
        _maxClockSkewSeconds = maxClockSkewSeconds;
    }

    /// <inheritdoc />
    public bool IsConfigured => _ring is not null;

    /// <inheritdoc />
    public void Stamp(HttpRequestMessage request, RuntimeCallTarget target, string callerClass)
    {
        request.Headers.Remove(RuntimeCallerToken.HeaderName);
        if (_ring is null)
            return;

        request.Headers.TryAddWithoutValidation(
            RuntimeCallerToken.HeaderName,
            RuntimeCallerToken.Create(_ring.ActiveKeyId, _ring.ActiveKey, target, _runtimeInfo.Domain, callerClass,
                _timeProvider.GetUtcNow()));
    }

    /// <inheritdoc />
    public RuntimeCallerTokenVerdict Verify(string? token, RuntimeCallTarget target)
        => _ring is null
            ? new RuntimeCallerTokenVerdict(string.IsNullOrWhiteSpace(token)
                ? RuntimeCallerTokenStatus.Missing
                : RuntimeCallerTokenStatus.UnknownKey)
            : RuntimeCallerToken.Verify(token, _ring, target, _timeProvider.GetUtcNow(), _maxClockSkewSeconds);
}

/// <summary>Reads a key-ring file; shared by the identity and the startup validator.</summary>
internal static class RuntimeCallerKeyRingLoader
{
    /// <summary>Null when <paramref name="keyFile"/> is unset.</summary>
    /// <exception cref="InvalidOperationException">The file is missing, unreadable or not a valid ring.</exception>
    public static RuntimeCallerKeyRing? Load(string? keyFile)
    {
        if (string.IsNullOrWhiteSpace(keyFile))
            return null;

        try
        {
            return RuntimeCallerKeyRing.Parse(File.ReadAllText(keyFile));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"The runtime-caller key file '{keyFile}' cannot be read: {exception.GetType().Name}.");
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                $"The runtime-caller key file '{keyFile}' is invalid: {exception.Message}");
        }
    }
}
