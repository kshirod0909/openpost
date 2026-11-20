using System.Data;
using System.Data.Common;
using OpenPost.Api.Data;

namespace OpenPost.Api.Data;

/// <summary>
/// Small ADO.NET escape hatch for statements EF cannot express (UPDATE … RETURNING,
/// SELECT … FOR UPDATE SKIP LOCKED). Uses the DbContext's own connection so it participates in
/// the same pool and, when a transaction is active, the same transaction.
/// </summary>
public static class RawSql
{
    public static async Task<List<T>> QueryAsync<T>(
        OpenPostDbContext db,
        string sql,
        CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandType = CommandType.Text;

        if (db.Database.CurrentTransaction is { } transaction)
            command.Transaction = transaction.GetDbTransaction();

        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        var results = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (await reader.IsDBNullAsync(0, ct)) continue;
            var value = reader.GetValue(0);
            results.Add(value is T typed ? typed : (T)Convert.ChangeType(value, typeof(T)));
        }

        return results;
    }
}
