using System.Collections;
using System.Diagnostics;
using Staffora.Core.Execution;

namespace Staffora.Services.Diagnostics;

public sealed record LogIdentity(Guid? TenantId, Guid? UserId, Guid? ImpersonatorId)
{
    public static readonly LogIdentity None = new(null, null, null);

    public static LogIdentity From(RuntimeSnapshot session) => new(session.TenantId, session.EffectiveUserId, session.ImpersonatedById);
}

public sealed class LogScope : IReadOnlyList<KeyValuePair<string, object?>>
{
    public const string CorrelationIdKey = "CorrelationId";
    public const string RequestPathKey = "RequestPath";
    public const string TenantIdKey = "TenantId";
    public const string UserIdKey = "UserId";
    public const string ImpersonatorIdKey = "ImpersonatorId";

    public const string CorrelationHeader = "X-Correlation-ID";

    public const int MaxCorrelationIdLength = 64;

    private volatile LogIdentity? identity;

    public LogScope(string correlationId, string? requestPath, LogIdentity? identity = null, bool followsTrace = false)
    {
        CorrelationId = correlationId;
        RequestPath = requestPath;
        FollowsTrace = followsTrace;
        this.identity = identity;
    }

    public string CorrelationId { get; }

    public string? RequestPath { get; }

    public bool FollowsTrace { get; }

    public LogIdentity? Identity => this.identity;

    public void Identify(LogIdentity identity) => this.identity = identity ?? throw new ArgumentNullException(nameof(identity));

    public static string NewCorrelationId() => Guid.NewGuid().ToString("N");

    public static string CorrelationIdFrom(string? callerValue)
    {
        if (!string.IsNullOrWhiteSpace(callerValue) && callerValue.Length <= MaxCorrelationIdLength && callerValue.All(IsCorrelationChar))
        {
            return callerValue;
        }

        return Activity.Current is { } activity && activity.TraceId != default ? activity.TraceId.ToHexString() : NewCorrelationId();
    }

    private static bool IsCorrelationChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':';

    public int Count => 5;

    public KeyValuePair<string, object?> this[int index]
    {
        get
        {
            var identity = this.identity ?? LogIdentity.None;
            return index switch
            {
                0 => new(CorrelationIdKey, CorrelationId),
                1 => new(RequestPathKey, RequestPath),
                2 => new(TenantIdKey, identity.TenantId),
                3 => new(UserIdKey, identity.UserId),
                4 => new(ImpersonatorIdKey, identity.ImpersonatorId),
                _ => throw new ArgumentOutOfRangeException(nameof(index)),
            };
        }
    }

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        for (var index = 0; index < Count; index++)
        {
            yield return this[index];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString()
    {
        var identity = this.identity ?? LogIdentity.None;
        return $"{CorrelationIdKey}:{CorrelationId} {RequestPathKey}:{RequestPath} {TenantIdKey}:{identity.TenantId} {UserIdKey}:{identity.UserId} {ImpersonatorIdKey}:{identity.ImpersonatorId}";
    }
}
