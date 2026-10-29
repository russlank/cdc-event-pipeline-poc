using CdcPay.Contracts.Events;
using CdcPay.PayHistory.Storage;
using Npgsql;

namespace CdcPay.IntegrationTests;

/// <summary>The store and schema against PostgreSQL, without Kafka.</summary>
public sealed class PayHistoryStoreTests(PipelineFixture pipeline)
{
    [Fact]
    public async Task ApplySchema_Twice_Succeeds()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var dataSource = NpgsqlDataSource.Create(await pipeline.CreateDatabaseAsync(cancellationToken));

        await ReadModelSchema.ApplyAsync(dataSource, cancellationToken);
        await ReadModelSchema.ApplyAsync(dataSource, cancellationToken);

        Assert.True(await new PayHistoryReader(dataSource).IsAvailableAsync(cancellationToken));
    }

    [Fact]
    public async Task ProjectAsync_SameEventTwice_IsADuplicateTheSecondTime()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var dataSource = NpgsqlDataSource.Create(await pipeline.CreateDatabaseAsync(cancellationToken));
        await ReadModelSchema.ApplyAsync(dataSource, cancellationToken);
        PayHistoryStore store = new(dataSource);
        PayEvent payEvent = FixtureStream.Events[0];

        ProjectionResult first = await store.ProjectAsync(payEvent, cancellationToken);
        ProjectionResult second = await store.ProjectAsync(payEvent, cancellationToken);

        Assert.Equal((ProjectionResult.Applied, ProjectionResult.Duplicate), (first, second));
        Assert.Single(await new PayHistoryReader(dataSource).HistoryAsync(1, 1003, cancellationToken));
    }

    [Fact]
    public async Task Schema_OverlappingCurrentRows_AreRejected()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var dataSource = NpgsqlDataSource.Create(await pipeline.CreateDatabaseAsync(cancellationToken));
        await ReadModelSchema.ApplyAsync(dataSource, cancellationToken);
        const string Insert =
            """
            INSERT INTO pay_history (tenant_id, employee_id, pay_component, salary_id, monthly_amount, currency,
                                     valid_from, valid_to, recorded_from, event_id)
            VALUES (1, 1003, 'Base', $1, 40000, 'SEK', $2, NULL, now(), gen_random_uuid())
            """;

        await ExecuteAsync(1, new DateOnly(2025, 1, 1));
        PostgresException rejected = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(2, new DateOnly(2026, 1, 1)));

        Assert.Equal(PostgresErrorCodes.ExclusionViolation, rejected.SqlState);

        async Task ExecuteAsync(int salaryId, DateOnly validFrom)
        {
            await using NpgsqlCommand command = dataSource.CreateCommand(Insert);
            command.Parameters.Add(new NpgsqlParameter { Value = salaryId });
            command.Parameters.Add(new NpgsqlParameter { Value = validFrom });
            _ = await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
