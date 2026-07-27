using System.Reflection;
using BBT.Workflow.Formatters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.HttpApi;

public sealed class FormUrlEncodedFormatterRegistrationTests
{
    [Fact]
    public void AddAspNetCoreModules_DoesNotRegisterFormFormatterForEveryHost()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddAspNetCoreModules(configuration);

        var options = services.BuildServiceProvider().GetRequiredService<IOptions<MvcOptions>>().Value;
        options.InputFormatters.ShouldNotContain(formatter =>
            formatter is FormUrlEncodedJsonElementInputFormatter);
    }

    [Fact]
    public void OrchestrationRegistration_AddsFormFormatter()
    {
        var services = new ServiceCollection();
        services.AddControllers();
        var registrationMethod = typeof(OrchestrationApiServiceCollectionExtensions).GetMethod(
            "AddFormUrlEncodedJsonElementInput",
            BindingFlags.Static | BindingFlags.NonPublic);

        registrationMethod.ShouldNotBeNull();
        registrationMethod!.Invoke(null, [services]);

        var options = services.BuildServiceProvider().GetRequiredService<IOptions<MvcOptions>>().Value;
        options.InputFormatters.ShouldContain(formatter =>
            formatter is FormUrlEncodedJsonElementInputFormatter);
    }
}
