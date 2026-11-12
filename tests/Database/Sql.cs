using Npgsql;

namespace Rrf5.Fiscal.Tests.Database;

/// <summary>Small helpers over positional parameters ($1, $2, ...).</summary>
public static class Sql
{
    public static async Task<int> ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, params object?[] args)
    {
        await using var command = Build(connection, transaction, sql, args);
        return await command.ExecuteNonQueryAsync();
    }

    public static async Task<T?> ScalarAsync<T>(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, params object?[] args)
    {
        await using var command = Build(connection, transaction, sql, args);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    public static Task<int> ExecuteAsync(NpgsqlConnection connection, string sql, params object?[] args) =>
        ExecuteAsync(connection, null, sql, args);

    /// <summary>Asserts that <paramref name="action"/> fails with the given SQLSTATE.</summary>
    public static async Task<PostgresException> FailsWithAsync(string sqlState, Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(action);
        Assert.Equal(sqlState, exception.SqlState);
        return exception;
    }

    private static NpgsqlCommand Build(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, object?[] args)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var arg in args)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = arg ?? DBNull.Value });
        }

        return command;
    }
}

/// <summary>SQLSTATE codes asserted by the tests.</summary>
public static class SqlState
{
    public const string ForeignKeyViolation = "23503";
    public const string UniqueViolation = "23505";
    public const string CheckViolation = "23514";
    public const string ExclusionViolation = "23P01";
    public const string InsufficientPrivilege = "42501";
    public const string InvalidTextRepresentation = "22P02";
    public const string ImmutableField = "RF001";
    public const string MonotonicDecrease = "RF002";
    public const string InvalidTransition = "RF003";
    public const string FourEyes = "RF004";
    public const string AppendOnly = "RF005";
    public const string Consistency = "RF006";
}
