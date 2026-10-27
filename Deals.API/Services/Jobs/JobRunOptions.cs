using System.Text.Json;
using Deals.BusinessLogic.Models.Discovery;

namespace Deals.API.Services.Jobs;

/// <summary>
/// Effective options of one execution: every option the job declares, resolved against its documented
/// default. It is immutable and complete on purpose — an executor never has to remember what "absent"
/// means, so a scheduled run (<see cref="Empty"/>) and a manual run behave the same way.
///
/// A value is a boxed <c>bool</c> for a switch and a boxed <c>int</c> for an integer option (spec S5). The shape is
/// deliberately the JSON leaf it came from, so the run record serialises exactly what was asked — <c>false</c> stays
/// <c>false</c> and <c>24</c> stays the number 24, never "24" and never "sí".
/// </summary>
public sealed class JobRunOptions
{
    private readonly IReadOnlyDictionary<string, object> _values;

    public JobRunOptions(IReadOnlyDictionary<string, object> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        foreach (var (key, value) in values)
        {
            if (value is not (bool or int))
            {
                throw new ArgumentException(
                    $"La opción '{key}' debe ser un booleano o un entero.",
                    nameof(values));
            }
        }

        _values = values;
    }

    /// <summary>Options of a run nobody parameterised (every scheduled cycle).</summary>
    public static JobRunOptions Empty { get; } =
        new(new Dictionary<string, object>(StringComparer.Ordinal));

    public IReadOnlyDictionary<string, object> Values => _values;

    public bool IncludePriceRefresh => Get(JobOptionKeys.IncludePriceRefresh);

    public bool ProduceAlerts => Get(JobOptionKeys.Produce);

    public bool DispatchAlerts => Get(JobOptionKeys.Dispatch);

    public bool DryRun => Get(JobOptionKeys.DryRun);

    /// <summary>
    /// The window the manual price pass must use (spec S2/S3). It is <see cref="CatalogPriceRefreshLimits.DefaultAgeHours"/>
    /// when the run did not choose one — the documented default of the option, resolved here as well because a
    /// scheduled-shaped run (<see cref="Empty"/>) never went through the parser.
    /// </summary>
    public int AgeHours =>
        _values.TryGetValue(JobOptionKeys.AgeHours, out var value) && value is int ageHours
            ? ageHours
            : CatalogPriceRefreshLimits.DefaultAgeHours;

    /// <summary>A switch: absent, or anything that is not <c>true</c>, is off.</summary>
    public bool Get(string key) => _values.TryGetValue(key, out var value) && value is true;

    /// <summary>Ordered snapshot for <c>job_runs.details</c>: same keys, stable order, no secrets.</summary>
    public IReadOnlyDictionary<string, object> ToDetails() =>
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
/// <item>a switch accepts only <c>true</c>/<c>false</c>, and an integer option accepts only a whole number inside its
/// declared bounds — a fraction, a numeric string, a boolean or null is refused, because each one would otherwise be
/// coerced into a window nobody asked for (spec S2);</item>
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

        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var descriptor in definition.Options)
        {
            values[descriptor.Key] = DefaultOf(definition, descriptor);
        }

        if (options is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } payload)
        {
            if (payload.ValueKind != JsonValueKind.Object)
            {
                return Fail(
                    "Las opciones deben ser un objeto JSON con valores true o false, o un número entero cuando la " +
                    "opción lo declare.",
                    definition);
            }

            foreach (var property in payload.EnumerateObject())
            {
                var descriptor = definition.Options.FirstOrDefault(
                    candidate => string.Equals(candidate.Key, property.Name, StringComparison.Ordinal));
                if (descriptor is null)
                {
                    return Fail($"La opción '{property.Name}' no aplica al job '{definition.Job}'.", definition);
                }

                if (JobOptionTypes.IsInteger(descriptor.Type))
                {
                    if (property.Value.ValueKind != JsonValueKind.Number ||
                        !property.Value.TryGetInt32(out var number))
                    {
                        return Fail(IntegerError(property.Name, descriptor), definition);
                    }

                    if (number < descriptor.MinValue || number > descriptor.MaxValue)
                    {
                        return Fail(IntegerError(property.Name, descriptor), definition);
                    }

                    values[descriptor.Key] = number;
                    continue;
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

    private static string IntegerError(string key, JobOptionDescriptor descriptor) =>
        $"La opción '{key}' debe ser un número entero entre {descriptor.MinValue} y {descriptor.MaxValue}.";

    /// <summary>
    /// The declared default, checked against the declared type: a descriptor that promises an integer and defaults to
    /// a boolean would silently run every unparameterised request with the wrong option, so it fails loudly here.
    /// </summary>
    private static object DefaultOf(JobDefinition definition, JobOptionDescriptor descriptor)
    {
        if (JobOptionTypes.IsInteger(descriptor.Type))
        {
            if (descriptor.DefaultValue is not int ageHours)
            {
                throw new InvalidOperationException(
                    $"La opción entera '{descriptor.Key}' del job '{definition.Job}' no declara un valor por defecto entero.");
            }

            return ageHours;
        }

        if (descriptor.DefaultValue is not bool flag)
        {
            throw new InvalidOperationException(
                $"La opción booleana '{descriptor.Key}' del job '{definition.Job}' no declara un valor por defecto booleano.");
        }

        return flag;
    }

    private static JobOptionParseResult Fail(string error, JobDefinition definition) =>
        new(false, DefaultsOf(definition), error);

    private static JobRunOptions DefaultsOf(JobDefinition definition) =>
        new(definition.Options.ToDictionary(
            descriptor => descriptor.Key,
            descriptor => DefaultOf(definition, descriptor),
            StringComparer.Ordinal));
}
