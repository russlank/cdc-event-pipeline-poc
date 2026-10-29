using System.Text;
using CdcPay.Translation.Raw;

namespace CdcPay.UnitTests.Translation;

/// <summary>Parsing of messages recorded from the running Debezium SQL Server connector.</summary>
public sealed class DebeziumParserTests
{
    [Fact]
    public void ParseChange_WithStreamedInsert_ReadsRowAndTransaction()
    {
        RawChange change = DebeziumParser.ParseChange(Fixture("salary-insert-raise.json", "cdc.HrCore.dbo.Salary"));

        Assert.Equal(ChangeOperation.Create, change.Operation);
        Assert.Equal("HrCore.dbo.Salary", change.DataCollection);
        Assert.Null(change.Before);
        SalaryRow salary = Assert.IsType<SalaryRow>(change.After);
        Assert.Equal(114, salary.SalaryId);
        Assert.Equal(3012, salary.EmployeeId);
        Assert.Equal(38137.00m, salary.MonthlyAmount);
        Assert.Equal(new DateOnly(2026, 11, 1), salary.EffectiveFrom);
        Assert.Null(salary.EffectiveTo);
        Assert.Equal("Raise", salary.Reason);
        Assert.False(change.Snapshot);
        Assert.Equal("0000002e:000003c8:0006", change.TransactionId);
        Assert.Equal(2, change.TotalOrder);
        Assert.Equal("0000002e:000003c8:0006", change.CommitLsn);
        Assert.Equal("0000002e:000003c8:0004", change.ChangeLsn);
        Assert.Equal(1793232531913, change.CommitTimestampMs);
    }

    [Fact]
    public void ParseChange_WithStreamedUpdate_ReadsBothImages()
    {
        RawChange change = DebeziumParser.ParseChange(Fixture("salary-update-close.json", "cdc.HrCore.dbo.Salary"));

        Assert.Equal(ChangeOperation.Update, change.Operation);
        SalaryRow before = Assert.IsType<SalaryRow>(change.Before);
        SalaryRow after = Assert.IsType<SalaryRow>(change.After);
        Assert.Null(before.EffectiveTo);
        Assert.Equal(new DateOnly(2026, 11, 1), after.EffectiveTo);
        Assert.Equal(1, change.TotalOrder);
    }

    [Fact]
    public void ParseChange_WithSnapshotRow_HasNoTransactionOrChangeLsn()
    {
        RawChange change = DebeziumParser.ParseChange(Fixture("salary-snapshot.json", "cdc.HrCore.dbo.Salary"));

        Assert.Equal(ChangeOperation.Read, change.Operation);
        Assert.True(change.Snapshot);
        Assert.Null(change.TransactionId);
        Assert.Null(change.TotalOrder);
        Assert.Null(change.ChangeLsn);
        Assert.Equal("Hire", Assert.IsType<SalaryRow>(change.After).Reason);
    }

    [Fact]
    public void ParseChange_WithEmployeeAndPosition_ReadsTypedRows()
    {
        EmployeeRow employee = Assert.IsType<EmployeeRow>(DebeziumParser.ParseChange(Fixture("employee-snapshot.json", "cdc.HrCore.dbo.Employee")).After);
        PositionRow position = Assert.IsType<PositionRow>(DebeziumParser.ParseChange(Fixture("position-snapshot.json", "cdc.HrCore.dbo.Position")).After);

        Assert.Equal(1001, employee.EmployeeId);
        Assert.Null(employee.TerminationDate);
        Assert.Equal(1, position.PositionId);
        Assert.Equal(2, position.Level);
        Assert.Equal(new DateOnly(2025, 8, 2), position.EffectiveFrom);
    }

    [Fact]
    public void ParseChange_WithDelete_HasBeforeImageOnly()
    {
        RawChange change = DebeziumParser.ParseChange(Fixture("tenant-delete.json", "cdc.HrCore.dbo.Tenant"));

        Assert.Equal(ChangeOperation.Delete, change.Operation);
        Assert.Equal("Tenant", Assert.IsType<OtherRow>(change.Before).Table);
        Assert.Null(change.After);
    }

    [Fact]
    public void ParseTransactionMarker_WithEnd_ReadsCountsPerTable()
    {
        TransactionMarker marker = DebeziumParser.ParseTransactionMarker(Fixture("transaction-end.json", "cdc.transaction"));

        Assert.Equal(TransactionStatus.End, marker.Status);
        Assert.Equal("0000002e:000003c8:0006", marker.TransactionId);
        Assert.Equal(2, marker.EventCount);
        Assert.Equal(2, marker.EventCountsByDataCollection["HrCore.dbo.Salary"]);
    }

    [Fact]
    public void ParseTransactionMarker_WithBegin_HasNoCounts()
    {
        TransactionMarker marker = DebeziumParser.ParseTransactionMarker(Fixture("transaction-begin.json", "cdc.transaction"));

        Assert.Equal(TransactionStatus.Begin, marker.Status);
        Assert.Null(marker.EventCount);
        Assert.Empty(marker.EventCountsByDataCollection);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("{\"op\":\"x\",\"source\":{\"table\":\"Salary\"}}")]
    [InlineData("{\"before\":null,\"after\":null,\"op\":\"c\",\"source\":{\"db\":\"HrCore\",\"schema\":\"dbo\",\"table\":\"Salary\",\"ts_ms\":1,\"snapshot\":\"false\",\"commit_lsn\":\"a\"}}")]
    [InlineData("{\"before\":null,\"after\":{\"SalaryId\":\"oops\"},\"op\":\"c\",\"source\":{\"db\":\"HrCore\",\"schema\":\"dbo\",\"table\":\"Salary\",\"ts_ms\":1,\"snapshot\":\"false\",\"commit_lsn\":\"a\"}}")]
    public void ParseChange_WithMalformedMessage_ThrowsFormatException(string json)
    {
        RawMessage message = new(new KafkaPosition("cdc.HrCore.dbo.Salary", 0, 1), null, Encoding.UTF8.GetBytes(json));

        _ = Assert.Throws<DebeziumFormatException>(() => DebeziumParser.ParseChange(message));
    }

    [Theory]
    [InlineData("Employee 1001 earns 42500.00")]
    [InlineData("{\"before\":null,\"after\":{\"SalaryId\":1,\"TenantId\":1,\"EmployeeId\":1001,\"MonthlyAmount\":\"forty-two-thousand-SECRET\",\"Currency\":\"SEK\",\"EffectiveFrom\":1,\"EffectiveTo\":null,\"Reason\":\"Hire\"},\"op\":\"c\",\"source\":{\"db\":\"HrCore\",\"schema\":\"dbo\",\"table\":\"Salary\",\"ts_ms\":1,\"snapshot\":\"false\",\"commit_lsn\":\"a\"}}")]
    [InlineData("{\"op\":\"SECRET-op\",\"source\":{\"table\":\"Salary\"}}")]
    public void ParseChange_WithMalformedMessage_DoesNotQuoteItsContent(string json)
    {
        RawMessage message = new(new KafkaPosition("cdc.HrCore.dbo.Salary", 0, 1), null, Encoding.UTF8.GetBytes(json));

        DebeziumFormatException exception = Assert.Throws<DebeziumFormatException>(() => DebeziumParser.ParseChange(message));

        Assert.DoesNotContain("SECRET", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Employee", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("42500", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseTransactionMarker_WithUnknownStatus_ThrowsFormatException()
    {
        RawMessage message = new(new KafkaPosition("cdc.transaction", 0, 1), null, Encoding.UTF8.GetBytes("{\"status\":\"MAYBE\",\"id\":\"x\",\"ts_ms\":1}"));

        _ = Assert.Throws<DebeziumFormatException>(() => DebeziumParser.ParseTransactionMarker(message));
    }

    private static RawMessage Fixture(string name, string topic) =>
        new(new KafkaPosition(topic, 0, 0), null, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Translation", "Fixtures", name)));
}
