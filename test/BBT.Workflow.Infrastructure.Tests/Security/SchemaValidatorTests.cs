using System;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Workflow.Data;
using BBT.Workflow.Security;
using BBT.Workflow.Infrastructure.Security;
using Microsoft.Extensions.Caching.Distributed;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Security;

public class SchemaValidatorTests
{
    private readonly IDistributedCache _cache;
    private readonly IDbContextProvider<WorkflowDbContext> _dbContextProvider;
    private readonly SchemaValidator _validator;

    public SchemaValidatorTests()
    {
        _cache = Substitute.For<IDistributedCache>();
        _dbContextProvider = Substitute.For<IDbContextProvider<WorkflowDbContext>>();
        _validator = new SchemaValidator(_cache, _dbContextProvider);
    }

    [Theory]
    [InlineData("public")]
    [InlineData("sys_flows")]
    [InlineData("sys_extensions")]
    [InlineData("sys_functions")]
    [InlineData("sys_schemas")]
    [InlineData("sys_tasks")]
    [InlineData("sys_views")]
    public async Task ValidateSchemaAsync_SystemSchemas_ShouldPass(string schema)
    {
        // Act
        var result = await _validator.ValidateSchemaAsync(schema);

        // Assert
        result.ShouldBe(schema);
    }

    [Theory]
    [InlineData("public\"; DROP TABLE Instances; --")]
    [InlineData("sys_flows' OR '1'='1")]
    [InlineData("SYS_FLOWS")] // Uppercase should be rejected
    [InlineData("sys-flows")] // Hyphen should be rejected
    [InlineData("../../../etc/passwd")]
    [InlineData("sys_flows; DROP TABLE--")]
    public void ValidateSchemaSync_InvalidSchemas_ShouldThrowSecurityException(string schema)
    {
        // Act & Assert
        Should.Throw<SecurityException>(() => _validator.ValidateSchemaSync(schema));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void ValidateSchemaSync_EmptySchemas_ShouldReturnPublic(string schema)
    {
        // Act
        var result = _validator.ValidateSchemaSync(schema);

        // Assert
        result.ShouldBe("public");
    }

    [Theory]
    [InlineData("Instances")]
    [InlineData("InstancesData")]
    [InlineData("Tasks")]
    [InlineData("Workflows")]
    [InlineData("InstanceCorrelations")]
    public void ValidateTableName_ValidTables_ShouldPass(string tableName)
    {
        // Act
        var result = _validator.ValidateTableName(tableName);

        // Assert
        result.ShouldBe(tableName);
    }

    [Theory]
    [InlineData("Users")]
    [InlineData("Instances; DROP TABLE--")]
    [InlineData("Instances' OR '1'='1")]
    [InlineData("../../../etc/passwd")]
    public void ValidateTableName_InvalidTables_ShouldThrowSecurityException(string tableName)
    {
        // Act & Assert
        Should.Throw<SecurityException>(() => _validator.ValidateTableName(tableName));
    }

    [Fact]
    public void ValidateSchemaSync_TooLongSchema_ShouldThrowSecurityException()
    {
        // Arrange
        var longSchema = new string('a', 64); // PostgreSQL limit is 63

        // Act & Assert
        Should.Throw<SecurityException>(() => _validator.ValidateSchemaSync(longSchema));
    }

    [Fact]
    public void ValidateSchemaSync_NullOrEmpty_ShouldReturnPublic()
    {
        // Act
        var result1 = _validator.ValidateSchemaSync(null);
        var result2 = _validator.ValidateSchemaSync("");
        var result3 = _validator.ValidateSchemaSync("   ");

        // Assert
        result1.ShouldBe("public");
        result2.ShouldBe("public");
        result3.ShouldBe("public");
    }

    [Fact]
    public async Task InvalidateCacheAsync_ShouldCallCacheRemove()
    {
        // Act
        await _validator.InvalidateCacheAsync();

        // Assert
        await _cache.Received(1).RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}

