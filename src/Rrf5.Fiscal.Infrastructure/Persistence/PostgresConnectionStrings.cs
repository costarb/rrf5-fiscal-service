using Npgsql;

namespace Rrf5.Fiscal.Infrastructure.Persistence;

/// <summary>Service-wide defaults applied to every PostgreSQL connection string.</summary>
public static class PostgresConnectionStrings
{
    /// <summary>
    /// Disables GSSAPI encryption negotiation unless the string sets it: the service
    /// does not use Kerberos, and the chiseled image has no <c>libgssapi_krb5</c>, so
    /// the default probe only produces a load error on every new physical connection.
    /// </summary>
    public static string WithServiceDefaults(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (!connectionString.Contains("GSS", StringComparison.OrdinalIgnoreCase))
        {
            builder.GssEncryptionMode = GssEncryptionMode.Disable;
        }

        return builder.ConnectionString;
    }
}
