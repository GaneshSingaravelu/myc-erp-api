namespace ERP.Identity.Entities;

/// <summary>
/// TENANT CODE — the one authoritative normalization for <see cref="Tenant.Code"/>.
///
/// THE INVARIANT
/// =============
/// A tenant code is a CASE-INSENSITIVE identifier whose canonical persisted form is
/// TRIMMED, INVARIANT LOWERCASE. Every write stores the canonical form and every lookup
/// compares the canonical form, so equality never depends on the database's collation.
///
/// WHY NOT RELY ON COLLATION?
/// ==========================
/// SQL Server's default collation is case-insensitive, so <c>X-Tenant-Id: DEV-TENANT</c>
/// resolves there today by accident of configuration. PostgreSQL compares case-sensitively,
/// where the same header would fail to resolve at all. Canonicalizing in the application
/// makes both engines behave identically, which is the point: the provider must not change
/// who a tenant is.
///
/// Normalizing on write is also what keeps the unique index on <c>Code</c> meaningful. If
/// only reads were normalized, <c>Acme</c> and <c>acme</c> could both be persisted on
/// PostgreSQL and the index would accept them as two different tenants.
///
/// RESIDUAL RISK, STATED NOT HIDDEN
/// ================================
/// This closes every write path the application controls. A value inserted outside the
/// application — direct SQL, a data import, a manual fix — can still be non-canonical,
/// because database-level enforcement (a CHECK constraint, a generated column, citext or a
/// provider-specific collation) is deliberately not part of this design.
///
/// WHY NOT A VALUE OBJECT?
/// =======================
/// <c>Tenant.Code</c> is a plain <c>string</c> on an EF-mapped entity, and there is no
/// validation layer in this codebase today. A static normalizer enforces the invariant at
/// the boundaries that matter without a mapping change or a new framework.
/// </summary>
public static class TenantCode
{
    /// <summary>
    /// Returns the canonical form of a tenant code: surrounding whitespace removed, then
    /// invariant-lowercased.
    ///
    /// <para><b>Invalid input.</b> <c>null</c>, empty and whitespace-only all return
    /// <see cref="string.Empty"/>. This method never throws and never fabricates an
    /// identifier from nothing.</para>
    ///
    /// <para>Returning empty rather than throwing is deliberate: it preserves the existing
    /// failure semantics of the callers. <c>GetByCodeAsync</c> and
    /// <c>GetByIdentifierAsync</c> answer "not found" for an unusable code, and
    /// <c>ExistsByCodeAsync</c> answers <c>false</c> — an empty canonical form simply
    /// matches no tenant, so those contracts are unchanged. Throwing here would turn a
    /// lookup miss into an exception.</para>
    ///
    /// <para><c>ToLowerInvariant</c> rather than <c>ToLower</c> so the result cannot depend
    /// on the current culture.</para>
    /// </summary>
    /// <param name="code">A raw tenant code from a header, configuration, or a request.</param>
    /// <returns>The canonical code, or <see cref="string.Empty"/> when the input is unusable.</returns>
    public static string Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return string.Empty;

        return code.Trim().ToLowerInvariant();
    }
}
