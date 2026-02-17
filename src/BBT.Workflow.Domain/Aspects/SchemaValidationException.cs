using System.ComponentModel.DataAnnotations;

namespace BBT.Workflow.Aspects;

/// <summary>
/// Exception thrown when schema validation fails in the <see cref="SchemaValidationAttribute"/> aspect.
/// </summary>
public class SchemaValidationException : Exception
{
    /// <summary>
    /// Gets the validation errors that caused the exception.
    /// </summary>
    public IReadOnlyCollection<ValidationResult>? ValidationErrors { get; }

    /// <summary>
    /// Creates a new instance of <see cref="SchemaValidationException"/>.
    /// </summary>
    /// <param name="message">The error message</param>
    public SchemaValidationException(string message) 
        : base(message)
    {
    }

    /// <summary>
    /// Creates a new instance of <see cref="SchemaValidationException"/> with validation errors.
    /// </summary>
    /// <param name="message">The error message</param>
    /// <param name="validationErrors">The validation errors</param>
    public SchemaValidationException(string message, IReadOnlyCollection<ValidationResult>? validationErrors) 
        : base(message)
    {
        ValidationErrors = validationErrors;
    }

    /// <summary>
    /// Creates a new instance of <see cref="SchemaValidationException"/> with an inner exception.
    /// </summary>
    /// <param name="message">The error message</param>
    /// <param name="innerException">The inner exception</param>
    public SchemaValidationException(string message, Exception innerException) 
        : base(message, innerException)
    {
    }
}
