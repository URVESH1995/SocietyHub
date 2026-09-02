namespace SocietyHub.SharedKernel.Tenancy;

/// <summary>
/// An explicit society scope for work that runs outside a request.
///
/// Tenancy normally comes from a signed claim on the bearer token, which is the only safe
/// source for anything a caller can influence. But seeding, outbox dispatch, message consumers
/// and retention jobs all write tenant-scoped rows with no request anywhere in sight — and the
/// write-side guard correctly refuses them, because from its point of view an unscoped write is
/// exactly what a tenancy bug looks like.
///
/// This is the way to say "I am deliberately acting for this society", in a form that is
/// greppable, has a bounded lifetime, and cannot be set by accident.
///
/// <para>
/// Two rules make it safe, and both are enforced in <c>ITenantContext</c> implementations
/// rather than here:
/// </para>
/// <list type="number">
/// <item>A request's claim always wins. Ambient state must never override a token, or a value
/// left behind by background work becomes a cross-tenant read on the next request that reuses
/// the thread.</item>
/// <item>It only ever applies where there is no claim at all — never as a fallback for a
/// request whose claim failed to parse, which is a failure, not an absence.</item>
/// </list>
/// </summary>
public static class TenantScope
{
    // AsyncLocal rather than a field or ThreadLocal: the value has to follow an async call
    // chain across continuations that may resume on different threads, which is exactly what
    // every seeder and consumer does at its first await.
    private static readonly AsyncLocal<Guid?> Ambient = new();

    /// <summary>
    /// The society background work has declared it is acting for, or null outside any scope.
    /// </summary>
    public static Guid? CurrentSocietyId => Ambient.Value;

    /// <summary>
    /// Enters a scope for one society. Dispose restores whatever was there before, so nesting
    /// behaves — a consumer handling a message for society A that calls a helper scoped to B
    /// does not leave B in place afterwards.
    /// </summary>
    public static IDisposable For(Guid societyId)
    {
        if (societyId == Guid.Empty)
        {
            // Guid.Empty is the value an uninitialised field has, and letting it through would
            // silently scope work to a society that cannot exist — then quietly write rows
            // nobody can ever read back.
            throw new ArgumentException(
                "Guid.Empty is not a society. Pass the real id.", nameof(societyId));
        }

        var previous = Ambient.Value;
        Ambient.Value = societyId;

        return new Restore(previous);
    }

    private sealed class Restore : IDisposable
    {
        private readonly Guid? _previous;
        private bool _disposed;

        public Restore(Guid? previous) => _previous = previous;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Ambient.Value = _previous;
        }
    }
}
