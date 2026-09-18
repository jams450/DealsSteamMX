using System;

namespace Deals.API.HostedServices;

/// <summary>
/// Pure scheduling math for the wishlist job. This file intentionally has no project dependencies so it
/// can be compiled and checked in isolation.
/// </summary>
public static class WishlistSchedule
{
    /// <summary>
    /// Returns next occurrence of target local hour, strictly after <paramref name="nowUtc"/>. If now is
    /// exactly on target, next run is tomorrow: otherwise a restart at 03:00 could run the same daily slot
    /// twice. Jitter is sampled inclusively from zero through the supplied duration.
    /// </summary>
    public static TimeSpan DelayUntilNextRun(
        DateTimeOffset nowUtc,
        TimeZoneInfo zone,
        int runAtHour,
        TimeSpan jitter,
        Random random)
    {
        var nextRun = NextRunAt(nowUtc, zone, runAtHour, jitter, random);
        return nextRun - nowUtc;
    }

    /// <summary>Convenience overload using the process-shared random source.</summary>
    public static TimeSpan DelayUntilNextRun(
        DateTimeOffset nowUtc,
        TimeZoneInfo zone,
        int runAtHour,
        TimeSpan jitter) =>
        DelayUntilNextRun(nowUtc, zone, runAtHour, jitter, Random.Shared);

    /// <summary>
    /// Calculates next scheduled instant, including jitter, for logging and waiting. The target day/hour
    /// calculation lives in <see cref="NextOccurrence"/> and is not duplicated by the delay helper.
    /// </summary>
    public static DateTimeOffset NextRunAt(
        DateTimeOffset nowUtc,
        TimeZoneInfo zone,
        int runAtHour,
        TimeSpan jitter,
        Random random)
    {
        var occurrence = NextOccurrence(nowUtc, zone, runAtHour);
        var jitterValue = jitter <= TimeSpan.Zero
            ? TimeSpan.Zero
            : TimeSpan.FromTicks((long)(random.NextDouble() * (jitter.Ticks + 1L)));

        // Expresado en la zona destino, no en UTC: el job loguea este valor con ":o" acompañado del nombre
        // de la zona, y un offset +00:00 al lado de "America/Mexico_City" hace leer 09:00 donde el operador
        // espera 03:00. Convertir no altera el instante (el offset es solo presentación), así que el delay
        // calculado por DelayUntilNextRun sigue siendo el mismo.
        return TimeZoneInfo.ConvertTime(occurrence + jitterValue, zone);
    }

    /// <summary>Convenience overload using the process-shared random source.</summary>
    public static DateTimeOffset NextRunAt(
        DateTimeOffset nowUtc,
        TimeZoneInfo zone,
        int runAtHour,
        TimeSpan jitter) =>
        NextRunAt(nowUtc, zone, runAtHour, jitter, Random.Shared);

    /// <summary>Returns the next target hour in zone, before adding jitter.</summary>
    public static DateTimeOffset NextOccurrence(DateTimeOffset nowUtc, TimeZoneInfo zone, int runAtHour)
    {
        if (runAtHour is < 0 or > 23)
        {
            throw new ArgumentOutOfRangeException(nameof(runAtHour));
        }

        var localNow = TimeZoneInfo.ConvertTime(nowUtc, zone);
        var localDate = localNow.Date;
        var targetLocal = new DateTime(localDate.Year, localDate.Month, localDate.Day, runAtHour, 0, 0, DateTimeKind.Unspecified);

        // Strictly future is deliberate: now == target means today's slot was reached already, so use tomorrow.
        if (targetLocal <= localNow.DateTime)
        {
            targetLocal = targetLocal.AddDays(1);
        }

        // A target inside a DST spring-forward gap has no valid instant. Move to first valid local time,
        // preserving the guarantee that the result remains strictly future.
        while (zone.IsInvalidTime(targetLocal))
        {
            targetLocal = targetLocal.AddMinutes(1);
        }

        var targetUtc = TimeZoneInfo.ConvertTimeToUtc(targetLocal, zone);
        return new DateTimeOffset(targetUtc, TimeSpan.Zero);
    }
}
