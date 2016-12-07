﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using NServiceBus.Timeout.Core;
using System.Threading.Tasks;
using NServiceBus.Extensibility;

class TimeoutPersister : IPersistTimeouts, IQueryTimeouts
{
    Func<Task<DbConnection>> connectionBuilder;
    string insertCommandText;
    string removeByIdCommandText;
    string removeBySagaIdCommandText;
    string selectByIdCommandText;
    string rangeComandText;
    string nextCommandText;

    public TimeoutPersister(
        Func<Task<DbConnection>> connectionBuilder, 
        string schema, 
        string tablePrefix)
    {
        this.connectionBuilder = connectionBuilder;

        insertCommandText = $@"
insert into [{schema}].[{tablePrefix}TimeoutData]
(
    Id,
    Destination,
    SagaId,
    State,
    Time,
    Headers,
    PersistenceVersion
)
VALUES
(
    @Id,
    @Destination,
    @SagaId,
    @State,
    @Time,
    @Headers,
    @PersistenceVersion
)";

        removeByIdCommandText = $@"
DELETE from [{schema}].[{tablePrefix}TimeoutData]
OUTPUT deleted.SagaId
where Id = @Id";

        removeBySagaIdCommandText = $@"
DELETE from [{schema}].[{tablePrefix}TimeoutData]
where SagaId = @SagaId";

        selectByIdCommandText = $@"
SELECT
    Destination,
    SagaId,
    State,
    Time,
    Headers
from [{schema}].[{tablePrefix}TimeoutData]
where Id = @Id";

        rangeComandText = $@"
SELECT Id, Time
from [{schema}].[{tablePrefix}TimeoutData]
where time BETWEEN @StartTime AND @EndTime";

        nextCommandText = $@"
SELECT TOP 1 Time from [{schema}].[{tablePrefix}TimeoutData]
where Time > @EndTime
ORDER BY TIME";
    }

    public async Task<TimeoutData> Peek(string timeoutId, ContextBag context)
    {
        using (var connection = await connectionBuilder())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = selectByIdCommandText;
            command.AddParameter("Id", timeoutId);
            using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow))
            {
                if (!await reader.ReadAsync())
                {
                    return null;
                }

                var headers = ReadHeaders(reader);
                return new TimeoutData
                {
                    Id = timeoutId,
                    Destination = reader.GetString(0),
                    SagaId = reader.GetGuid(1),
                    State = (byte[]) reader.GetValue(2),
                    Time = reader.GetDateTime(3),
                    Headers = headers,
                };
            }
        }
    }

    Dictionary<string, string> ReadHeaders(DbDataReader reader)
    {
        using (var textReader = reader.GetTextReader(4))
        {
            return Serializer.Deserialize<Dictionary<string, string>>(textReader);
        }
    }

    public async Task Add(TimeoutData timeout, ContextBag context)
    {
        using (var connection = await connectionBuilder())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = insertCommandText;
            var id = Guid.NewGuid();
            timeout.Id = id.ToString();
            command.AddParameter("Id", id);
            command.AddParameter("Destination", timeout.Destination);
            command.AddParameter("SagaId", timeout.SagaId);
            command.AddParameter("State", timeout.State);
            command.AddParameter("Time", timeout.Time);
            command.AddParameter("Headers", Serializer.Serialize(timeout.Headers));
            command.AddParameter("PersistenceVersion", StaticVersions.PersistenceVersion);
            await command.ExecuteNonQueryEx();
        }
    }

    public async Task<bool> TryRemove(string timeoutId, ContextBag context)
    {
        using (var connection = await connectionBuilder())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = removeByIdCommandText;
            command.AddParameter("Id", timeoutId);
            using (var reader = await command.ExecuteReaderAsync())
            {
                if (!reader.HasRows)
                {
                    return false;
                }
            }
        }
        return true;
    }
    public async Task<TimeoutsChunk> GetNextChunk(DateTime startSlice)
    {
        var list = new List<TimeoutsChunk.Timeout>();
        var now = DateTime.UtcNow;
        DateTime nextTimeToRunQuery;
        using (var connection = await connectionBuilder())
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = rangeComandText;
                command.AddParameter("StartTime", startSlice);
                command.AddParameter("EndTime", now);
                using (var reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        var id = reader.GetGuid(0).ToString();
                        list.Add(new TimeoutsChunk.Timeout(id, reader.GetDateTime(1)));
                    }
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = nextCommandText;
                command.AddParameter("EndTime", now);
                var executeScalar = await command.ExecuteScalarAsync();
                if (executeScalar == null)
                {
                    nextTimeToRunQuery = now.AddMinutes(10);
                }
                else
                {
                    nextTimeToRunQuery = (DateTime)executeScalar;
                }
            }
        }
        return new TimeoutsChunk(list.ToArray(), nextTimeToRunQuery);
    }

    public async Task RemoveTimeoutBy(Guid sagaId, ContextBag context)
    {
        using (var connection = await connectionBuilder())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = removeBySagaIdCommandText;
            command.AddParameter("SagaId", sagaId);
            await command.ExecuteNonQueryEx();
        }
    }

}
