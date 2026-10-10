using Microsoft.Extensions.Caching.Memory;
using System.Security.Claims;

namespace Backend.Services;

// Decides whether an access token that is otherwise valid (signature, expiry) may still be used.
// It is refused when its user no longer exists (the account was deleted), or when it was issued
// before the user's TokensValidAfter (an administrator set their password). Access tokens are
// self-contained, so without this they would keep working until they expire.
//
// The check reads one row per request, so each user's answer is kept in memory for
// Auth:UserStateCacheSeconds (30 by default; 0 turns the cache off, as the tests do). Changes made
// through this API call Evict, so they apply at once; a change made elsewhere (another API
// instance, or directly in the database) can take up to that long to be noticed.
public sealed class AccessTokenGate(IMemoryCache cache, IConfiguration config)
{
    // Access tokens carry their issue time to the millisecond in this claim. The standard "iat"
    // claim has one-second resolution, which would refuse a token issued just after the change
    // in the same second (a login right after an administrator sets the password).
    public const string IssuedAtMillisecondsClaim = "fv_iat_ms";

    private static string CacheKey(string userId) => $"user-auth-state:{userId}";

    private TimeSpan CacheTime => TimeSpan.FromSeconds(Math.Max(0, config.GetValue("Auth:UserStateCacheSeconds", 30)));

    // Forget what is known about this user (after deleting them or invalidating their tokens)
    public void Evict(string userId)
    {
        // Bump first: a read that started before this can no longer cache its (stale) answer
        _generations.AddOrUpdate(userId, 1, (_, g) => g + 1);
        cache.Remove(CacheKey(userId));
    }

    // Per-user count of Evict calls (one small entry per evicted user)
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _generations = new();

    public async Task<bool> IsAcceptedAsync(ClaimsPrincipal principal, DatabaseServices db)
    {
        var userId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(userId, out var id))
            return false;
        userId = id.ToString();

        var state = await GetStateAsync(userId, () => db.GetUserAuthStateAsync(userId));
        if (!state.Exists)
            return false;
        if (state.TokensValidAfter is not { } validAfter)
            return true;

        // A token without an issue time predates this check and can't be shown to be newer
        if (!long.TryParse(principal.FindFirst(IssuedAtMillisecondsClaim)?.Value, out var issuedAt))
            return false;
        return issuedAt >= new DateTimeOffset(validAfter).ToUnixTimeMilliseconds();
    }

    // Public so tests can supply the database read
    public async Task<DatabaseServices.UserAuthState> GetStateAsync(string userId, Func<Task<DatabaseServices.UserAuthState>> read)
    {
        var time = CacheTime;
        if (time == TimeSpan.Zero)
            return await read();

        if (cache.TryGetValue(CacheKey(userId), out DatabaseServices.UserAuthState? cached) && cached != null)
            return cached;

        var generation = _generations.GetValueOrDefault(userId);
        var state = await read();
        if (_generations.GetValueOrDefault(userId) == generation)
            cache.Set(CacheKey(userId), state, time);
        return state;
    }
}
