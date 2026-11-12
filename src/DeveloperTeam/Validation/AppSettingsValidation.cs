using Microsoft.Extensions.Options;
using Npgsql;

namespace DeveloperTeam.Validation;

public static class AppSettingsValidation
{
    public static void Validate(IConfiguration configuration)
    {
        var errors = new List<string>();
        var role = configuration["APP_ROLE"] ?? "api";
        var isWorker = role.Equals("worker", StringComparison.OrdinalIgnoreCase);
        if (!isWorker && !role.Equals("api", StringComparison.OrdinalIgnoreCase))
            errors.Add("APP_ROLE must be api or worker.");

        var connectionString = configuration.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            errors.Add("ConnectionStrings:Default is required.");
        else
        {
            try
            {
                var connection = new NpgsqlConnectionStringBuilder(connectionString);
                if (string.IsNullOrWhiteSpace(connection.Host))
                    errors.Add("ConnectionStrings:Default must specify Host.");
            }
            catch (ArgumentException)
            {
                errors.Add("ConnectionStrings:Default must be a valid PostgreSQL connection string.");
            }
        }

        var workspaceRoot = configuration["Worker:WorkspaceRoot"];
        if (workspaceRoot is not null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(workspaceRoot))
                    errors.Add("Worker:WorkspaceRoot must not be empty.");
                else
                    _ = Path.GetFullPath(workspaceRoot);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                errors.Add("Worker:WorkspaceRoot must be a valid path.");
            }
        }

        var timeout = configuration["Worker:TaskTimeoutMinutes"];
        if (timeout is not null && (!int.TryParse(timeout, out var minutes) || minutes <= 0
            || (double)minutes * 60_000 > uint.MaxValue - 1))
            errors.Add("Worker:TaskTimeoutMinutes must be a positive integer within the supported timer range (1–71582).");

        var fixAttempts = configuration["Worker:MaxFixAttempts"];
        if (fixAttempts is not null && (!int.TryParse(fixAttempts, out var attempts) || attempts < 0))
            errors.Add("Worker:MaxFixAttempts must be a nonnegative integer (default: 2).");

        if (isWorker)
        {
            if (string.IsNullOrWhiteSpace(configuration["OpenAI:ApiKey"]))
                errors.Add("OpenAI:ApiKey is required for the worker.");
            if (string.IsNullOrWhiteSpace(configuration["OpenAI:Model"]))
                errors.Add("OpenAI:Model is required for the worker.");
            if (configuration["OpenAI:ReviewerModel"] is { } reviewer && string.IsNullOrWhiteSpace(reviewer))
                errors.Add("OpenAI:ReviewerModel must not be empty when specified.");
        }

        if (errors.Count > 0)
            throw new OptionsValidationException("AppSettings", typeof(IConfiguration), errors);
    }
}
