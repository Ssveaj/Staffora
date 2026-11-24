using Staffora.Core.Models;
using Staffora.Core.Scheduling;
using Staffora.Core.Settings;

namespace Staffora.Services.Settings;

public sealed class TenantClock
{
    private readonly TenantSettingsService settings;
    private readonly TimeProvider clock;

    public TenantClock(TenantSettingsService settings, TimeProvider clock)
    {
        this.settings = settings;
        this.clock = clock;
    }

    public Task<Result<TimeZoneInfo>> GetTimeZoneAsync(CancellationToken cancellationToken = default) =>
        this.settings.GetAsync(TenantSettingDefinitions.TimeZone, cancellationToken);

    public Task<Result<TimeZoneInfo>> GetTimeZoneOfAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        this.settings.GetOfTenantAsync(TenantSettingDefinitions.TimeZone, tenantId, cancellationToken);

    public async Task<Result<TenantHolidays>> GetHolidaysAsync(CancellationToken cancellationToken = default) =>
        HolidaysOf(await this.settings.GetAsync(TenantSettingDefinitions.PublicHolidays, cancellationToken), await this.settings.GetAsync(TenantSettingDefinitions.ClosedOnPublicHolidays, cancellationToken));

    public async Task<Result<TenantHolidays>> GetHolidaysOfAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        HolidaysOf(await this.settings.GetOfTenantAsync(TenantSettingDefinitions.PublicHolidays, tenantId, cancellationToken), await this.settings.GetOfTenantAsync(TenantSettingDefinitions.ClosedOnPublicHolidays, tenantId, cancellationToken));

    public async Task<Result<DateTimeOffset>> NowAsync(CancellationToken cancellationToken = default)
    {
        var zone = await GetTimeZoneAsync(cancellationToken);
        return zone.IsSuccess ? Result<DateTimeOffset>.Success(ToLocal(this.clock.GetUtcNow(), zone.Value!)) : Result<DateTimeOffset>.Failure(zone.Error);
    }

    public async Task<Result<DateOnly>> TodayAsync(CancellationToken cancellationToken = default)
    {
        var now = await NowAsync(cancellationToken);
        return now.IsSuccess ? Result<DateOnly>.Success(DateOnly.FromDateTime(now.Value.DateTime)) : Result<DateOnly>.Failure(now.Error);
    }

    public DateOnly TodayIn(TimeZoneInfo zone) => DateOnly.FromDateTime(ToLocal(this.clock.GetUtcNow(), zone).DateTime);

    public static DateTime ToUtc(DateOnly date, TimeOnly time, TimeZoneInfo zone) => WallClock.ToUtc(date, time, zone);

    public static DateTimeOffset ToLocal(DateTimeOffset instant, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(instant, zone);

    public static DateTimeOffset ToLocal(DateTime utc, TimeZoneInfo zone) =>
        utc.Kind == DateTimeKind.Utc
            ? TimeZoneInfo.ConvertTime(new DateTimeOffset(utc, TimeSpan.Zero), zone)
            : throw new ArgumentException("An instant is converted from UTC only (ADR 0020).", nameof(utc));

    private static Result<TenantHolidays> HolidaysOf(Result<HolidayCalendar> calendar, Result<bool> closed) =>
        !calendar.IsSuccess ? Result<TenantHolidays>.Failure(calendar.Error)
        : !closed.IsSuccess ? Result<TenantHolidays>.Failure(closed.Error)
        : Result<TenantHolidays>.Success(new TenantHolidays(calendar.Value!, closed.Value));
}
