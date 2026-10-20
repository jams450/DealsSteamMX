using System.Text.Json;

namespace Deals.API.Services.Jobs;

/// <summary>
/// Effective options of one execution: every option the job declares, resolved against its documented
/// default. It is immutable and complete on purpose — an executor never has to remember what "absent"
/// means, so a scheduled run (<see cref="Empty"/>) and a manual run behave the same way.
/// </summary>
public sealed class JobRunOptions
{
    private readonly IReadOnlyDictionary<string, bool> _values;

    public JobRunOptions(IReadOnlyDictionary<string, bool> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        _values = values;
    }

    /// <summary>Options of a run nobody parameterised (every scheduled cycle).</summary>
    public static JobRunOptions Empty { get; } =
        new(new Dictionary<string, bool>(StringComparer.Ordinal));

    public IReadOnlyDictionary<string, bool> Values => _values;

    public bool IncludePriceRefresh => Get(JobOptionKeys.IncludePriceRefresh);

    public bool ProduceAlerts => Get(JobOptionKeys.Produce);

    public bool DispatchAlerts => Get(JobOptionKeys.Dispatch);

    public bool DryRun => Get(JobOptionKeys.DryRun);

    public bool Get(string key) => _values.TryGetValue(key, out var value) && value;

    /// <summary>Ordered snapshot for <c>job_runs.details</c>: same keys, stable order, no secrets.</summary>
    public IReadOnlyDictionary<string, bool> ToDetails() =>
        _values.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
}

public sealed record JobOptionParseResult(
    bool Succeeded,
    JobRunOptions Options,
    string? Error,
    bool RequiresConfirmation = false);

/// <summary>
/// Validates the bounded option payload of a manual run. Deliberately strict:
/// <list type="bullet">
/// <item>an unknown key is an error, not a silent no-op — a typo must not run the job with defaults while
/// the operator believes the override applied;</item>
/// <item>a non-boolean value is an error, because the only options a job has are switches;</item>
/// <item>a destructive job refuses to run without <c>confirm: true</c> unless it is a dry run.</item>
/// </list>
/// Nothing here reads configuration, so the same payload always validates the same way and can be checked
/// without a host.
/// </summary>
public static class JobOptionParser
{
    public static JobOptionParseResult Parse(
        JobDefinition definition,
        JsonElement? options,
        bool confirm)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var values = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var descriptor in definition.Options)
        {
            values[descriptor.Key] = descriptor.DefaultValue;
        }

        if (options is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } payload)
        {
            if (payload.ValueKind != JsonValueKind.Object)
            {
                return Fail("Las opciones deben ser un objeto JSON con valores true o false.", definition);
            }

            foreach (var property in payload.EnumerateObject())
            {
                var descriptor = definition.Options.FirstOrDefault(
                    candidate => string.Equals(candidate.Key, property.Name, StringComparison.Ordinal));
                if (descriptor is null)
                {
                    return Fail($"La opción '{property.Name}' no aplica al job '{definition.Job}'.", definition);
                }

                if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return Fail($"La opción '{property.Name}' debe ser true o false.", definition);
                }

                values[descriptor.Key] = property.Value.ValueKind == JsonValueKind.True;
            }
        }

        var effective = new JobRunOptions(values);
        if (definition.RequiresConfirmation && !confirm && !effective.DryRun)
        {
            return new JobOptionParseResult(
                Succeeded: false,
                effective,
                Error: $"«{definition.DisplayName}» borra datos: confirma la ejecución explícitamente o usa la simulación.",
                RequiresConfirmation: true);
        }

        return new JobOptionParseResult(true, effective, null);
    }

    private static JobOptionParseResult Fail(string error, JobDefinition definition) =>
        new(false, DefaultsOf(definition), error);

    private static JobRunOptions DefaultsOf(JobDefinition definition) =>
        new(definition.Options.ToDictionary(
            descriptor => descriptor.Key,
            descriptor => descriptor.DefaultValue,
            StringComparer.Ordinal));
}
