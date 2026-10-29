namespace CdcPay.Translation.Raw;

/// <summary>A message that is not a Debezium change or transaction event of the expected shape.</summary>
public sealed class DebeziumFormatException : Exception
{
    public DebeziumFormatException()
    {
    }

    public DebeziumFormatException(string message)
        : base(message)
    {
    }

    public DebeziumFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
