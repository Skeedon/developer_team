using DeveloperTeam.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace DeveloperTeam.Tests.Validation;

public class AppSettingsValidationTests
{
    private static IConfiguration Configuration(params (string Key, string? Value)[] overrides)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = "Host=localhost;Database=test"
        };
        foreach (var (key, value) in overrides) values[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("api")]
    [InlineData("API")]
    public void ApiStartsWithoutWorkerCredentials(string? role)
    {
        AppSettingsValidation.Validate(Configuration(("APP_ROLE", role)));
    }

    [Theory]
    [InlineData("worker")]
    [InlineData("WORKER")]
    public void WorkerAcceptsCredentialsAndOptionalDefaults(string role)
    {
        AppSettingsValidation.Validate(Configuration(
            ("APP_ROLE", role), ("OpenAI:ApiKey", "test-key"), ("OpenAI:Model", "test-model")));
    }

    [Theory]
    [InlineData("APP_ROLE", "")]
    [InlineData("APP_ROLE", "unknown")]
    [InlineData("ConnectionStrings:Default", null)]
    [InlineData("ConnectionStrings:Default", " ")]
    [InlineData("ConnectionStrings:Default", "invalid")]
    [InlineData("ConnectionStrings:Default", "Database=test")]
    [InlineData("Worker:WorkspaceRoot", " ")]
    [InlineData("Worker:WorkspaceRoot", "invalid\0path")]
    [InlineData("Worker:TaskTimeoutMinutes", "")]
    [InlineData("Worker:TaskTimeoutMinutes", "abc")]
    [InlineData("Worker:TaskTimeoutMinutes", "1.5")]
    [InlineData("Worker:TaskTimeoutMinutes", "0")]
    [InlineData("Worker:TaskTimeoutMinutes", "-1")]
    [InlineData("Worker:TaskTimeoutMinutes", "71583")]
    [InlineData("Worker:TaskTimeoutMinutes", "2147483648")]
    public void InvalidSettingReportsItsKey(string key, string? value)
    {
        var error = Assert.Throws<OptionsValidationException>(() =>
            AppSettingsValidation.Validate(Configuration((key, value))));

        Assert.Contains(key, error.Message);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("20")]
    [InlineData("71582")]
    public void AcceptsSupportedTimeouts(string timeout)
    {
        AppSettingsValidation.Validate(Configuration(("Worker:TaskTimeoutMinutes", timeout)));
    }

    [Fact]
    public void AggregatesWorkerErrorsWithoutExposingValues()
    {
        const string secret = "sensitive-test-value";
        var error = Assert.Throws<OptionsValidationException>(() =>
            AppSettingsValidation.Validate(Configuration(
                ("APP_ROLE", "worker"),
                ("ConnectionStrings:Default", $"Password={secret};UnknownOption=value"),
                ("OpenAI:ApiKey", " "),
                ("OpenAI:ReviewerModel", ""),
                ("Worker:TaskTimeoutMinutes", "0"))));

        Assert.Equal(5, error.Failures.Count());
        Assert.Contains("OpenAI:ApiKey", error.Message);
        Assert.Contains("OpenAI:Model", error.Message);
        Assert.Contains("OpenAI:ReviewerModel", error.Message);
        Assert.DoesNotContain(secret, error.ToString());
    }
}
