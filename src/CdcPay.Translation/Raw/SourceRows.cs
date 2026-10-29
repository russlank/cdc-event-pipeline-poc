namespace CdcPay.Translation.Raw;

/// <summary>A row image of a captured source table.</summary>
public abstract record SourceRow
{
    /// <summary>Primary key as text, for event ids and diagnostics.</summary>
    public abstract string Key { get; }
}

public sealed record EmployeeRow(
    int TenantId,
    int EmployeeId,
    string Name,
    string Gender,
    DateOnly HireDate,
    DateOnly? TerminationDate) : SourceRow
{
    public override string Key => $"{TenantId}:{EmployeeId}";
}

public sealed record SalaryRow(
    int SalaryId,
    int TenantId,
    int EmployeeId,
    decimal MonthlyAmount,
    string Currency,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    string Reason,
    string PayComponent = Contracts.PayEventContract.DefaultPayComponent) : SourceRow
{
    public override string Key => SalaryId.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record PositionRow(
    int PositionId,
    int TenantId,
    int EmployeeId,
    string Title,
    int Level,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo) : SourceRow
{
    public override string Key => PositionId.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>A row of a captured table that carries no pay facts (Tenant, Debezium signal and heartbeat).</summary>
public sealed record OtherRow(string Table) : SourceRow
{
    public override string Key => Table;
}
