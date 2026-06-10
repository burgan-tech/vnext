namespace BBT.Workflow.Runtime;

/// <summary>
/// Provides configuration options and schema management for the workflow runtime environment.
/// This class maintains a collection of system schemas and provides access to their types.
/// </summary>
public class RuntimeOptions
{
    /// <summary>
    /// Configuration section name for runtime options
    /// </summary>
    public const string SectionName = "Runtime";

    /// <summary>
    /// Gets the collection of system schemas used by the workflow runtime.
    /// This dictionary contains mappings between schema names and their corresponding information.
    /// </summary>
    /// <value>
    /// A <see cref="RuntimeSysSchemaDictionary"/> containing all registered system schemas.
    /// </value>
    public RuntimeSysSchemaDictionary Schemas { get; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether schema migrations should be automatically executed.
    /// When set to false, the runtime service will operate in read-only mode and will not attempt
    /// to create or migrate database schemas. This is useful for execution applications that should
    /// not perform schema operations, leaving that responsibility to orchestration applications.
    /// </summary>
    /// <value>
    /// <c>true</c> if schema migrations should be executed automatically; otherwise, <c>false</c>.
    /// Default value is <c>true</c> for backward compatibility.
    /// </value>
    public bool EnableSchemaMigration { get; set; } = true;

    /// <summary>
    /// Retrieves the .NET type associated with the specified schema name.
    /// </summary>
    /// <param name="name">The name of the schema to get the type for.</param>
    /// <returns>The <see cref="Type"/> associated with the specified schema name.</returns>
    /// <exception cref="KeyNotFoundException">Thrown when the specified schema name is not found in the collection.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="name"/> is null.</exception>
    public Type GetSchemaType(string name)
    {
        return this.Schemas[name].Type;
    }

    /// <summary>
    /// Retrieves the schema name associated with the specified .NET type.
    /// This method performs a reverse lookup from type to schema name.
    /// </summary>
    /// <param name="type">The .NET type to get the schema name for.</param>
    /// <returns>The schema name associated with the specified type.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no schema is found for the specified type.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="type"/> is null.</exception>
    public string GetSchemaNameByType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        
        var schemaInfo = Schemas.Values.FirstOrDefault(s => s.Type == type);
        if (schemaInfo == null)
        {
            throw new InvalidOperationException($"No schema found for type {type.Name}");
        }
        return schemaInfo.Name;
    }
}

/// <summary>
/// Represents information about a system schema in the workflow runtime.
/// This class encapsulates the name, database schema, and .NET type for a system schema.
/// </summary>
/// <param name="name">The logical name of the schema used for identification.</param>
/// <param name="schema">The database schema name where the tables are located.</param>
/// <param name="type">The .NET type that represents this schema's data structure.</param>
public class RuntimeSysSchemaInfo(string name, string schema, Type type)
{
    /// <summary>
    /// Gets the logical name of the schema.
    /// </summary>
    /// <value>
    /// The name used to identify this schema within the workflow system.
    /// </value>
    public string Name { get; } = name;

    /// <summary>
    /// Gets the database schema name where the tables for this schema are located.
    /// </summary>
    /// <value>
    /// The database schema name used for table organization.
    /// </value>
    public string Schema { get; } = schema;

    /// <summary>
    /// Gets the .NET type that represents the data structure for this schema.
    /// </summary>
    /// <value>
    /// The <see cref="Type"/> that defines the structure and behavior of entities in this schema.
    /// </value>
    public Type Type { get; } = type;

    /// <summary>
    /// Constant representing the system flows schema identifier.
    /// </summary>
    public const string Flows = "sys-flows";

    /// <summary>
    /// Constant representing the system functions schema identifier.
    /// </summary>
    public const string Functions = "sys-functions";

    /// <summary>
    /// Constant representing the system schemas schema identifier.
    /// </summary>
    public const string Schemas = "sys-schemas";

    /// <summary>
    /// Constant representing the system tasks schema identifier.
    /// </summary>
    public const string Tasks = "sys-tasks";

    /// <summary>
    /// Constant representing the system views schema identifier.
    /// </summary>
    public const string Views = "sys-views";

    /// <summary>
    /// Constant representing the system extensions schema identifier.
    /// </summary>
    public const string Extensions = "sys-extensions";

    /// <summary>
    /// Constant representing the system mappings schema identifier.
    /// A mapping component (<see cref="BBT.Workflow.Definitions.Mapping"/>) is a reusable, versioned
    /// C# script (e.g. a helper class library) that transition mappings can reference.
    /// </summary>
    public const string Mappings = "sys-mappings";
}

/// <summary>
/// A specialized dictionary for managing system schema information.
/// Extends <see cref="Dictionary{TKey, TValue}"/> to provide convenient methods for adding schema entries.
/// </summary>
public class RuntimeSysSchemaDictionary : Dictionary<string, RuntimeSysSchemaInfo>
{
    /// <summary>
    /// Adds a new system schema entry to the dictionary with the specified parameters.
    /// This method creates a new <see cref="RuntimeSysSchemaInfo"/> instance and adds it to the collection.
    /// </summary>
    /// <param name="name">The logical name of the schema for identification purposes.</param>
    /// <param name="schema">The database schema name where tables are located.</param>
    /// <param name="type">The .NET type that represents the schema's data structure.</param>
    /// <remarks>
    /// If a schema with the same name already exists, it will be replaced with the new entry.
    /// </remarks>
    public void Add(string name, string schema, Type type)
    {
        this[name] = new RuntimeSysSchemaInfo(name, schema, type);
    }
}