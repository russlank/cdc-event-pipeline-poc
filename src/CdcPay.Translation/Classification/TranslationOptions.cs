namespace CdcPay.Translation.Classification;

/// <summary>Settings of the translation rules (configuration section <c>Translation</c>).</summary>
public sealed class TranslationOptions
{
    public const string SectionName = "Translation";

    /// <summary>ISO 4217 codes accepted in Salary.Currency; anything else is dead-lettered.</summary>
    public IList<string> SupportedCurrencies { get; } = ["SEK", "EUR", "NOK", "DKK", "USD"];

    /// <summary>How long an incomplete source transaction may wait before it is dead-lettered.</summary>
    public TimeSpan TransactionTimeout { get; set; } = TimeSpan.FromSeconds(60);
}
