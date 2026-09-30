namespace BBT.Workflow.Authorization;

/// <summary>What the host can do with <c>x-encryption</c> (<c>hash</c>, <c>encrypt</c>) on the write path.</summary>
public interface IFieldEncryptionStatus
{
    /// <summary>
    /// True when new writes will hash and encrypt (<c>SchemaEncryption:EncryptWrites</c>). A schema declaring <c>hash</c>
    /// or <c>encrypt</c> is refused at publish when this is false — otherwise its values would be stored in plaintext
    /// without anyone noticing.
    /// </summary>
    bool CanEncrypt { get; }
}
