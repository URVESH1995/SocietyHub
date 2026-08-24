using SocietyHub.SharedKernel.Results;

namespace SocietyHub.SharedKernel.Globalization;

/// <summary>
/// An amount paired with its ISO 4217 currency.
///
/// Bulk-drive pricing is never a bare <c>decimal</c>. A slab rate stored without its
/// currency is correct only for as long as the platform sells in exactly one country, and
/// retrofitting currency onto a live pricing and ledger schema is among the more painful
/// migrations there is. The cost of carrying it from the start is three characters a row.
/// </summary>
public readonly record struct Money : IComparable<Money>
{
    public Money(decimal amount, string currency)
    {
        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3)
        {
            throw new ArgumentException(
                "Currency must be a three-letter ISO 4217 code.", nameof(currency));
        }

        Amount = amount;
        Currency = currency.ToUpperInvariant();
    }

    public decimal Amount { get; }

    public string Currency { get; }

    public static Money Zero(string currency) => new(0m, currency);

    public static Money Rupees(decimal amount) => new(amount, "INR");

    public static Result<Money> Create(decimal amount, string currency)
    {
        if (amount < 0m)
        {
            return Error.Validation("Money.Negative", "Amount cannot be negative.");
        }

        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3)
        {
            return Error.Validation(
                "Money.Currency", "Currency must be a three-letter ISO 4217 code.");
        }

        return new Money(amount, currency);
    }

    public static Money operator +(Money left, Money right) =>
        new(left.Amount + Assert(left, right).Amount, left.Currency);

    public static Money operator -(Money left, Money right) =>
        new(left.Amount - Assert(left, right).Amount, left.Currency);

    public static Money operator *(Money money, int quantity) =>
        new(money.Amount * quantity, money.Currency);

    public static bool operator <(Money left, Money right) => left.CompareTo(right) < 0;

    public static bool operator >(Money left, Money right) => left.CompareTo(right) > 0;

    public static bool operator <=(Money left, Money right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Money left, Money right) => left.CompareTo(right) >= 0;

    public int CompareTo(Money other) => Amount.CompareTo(Assert(this, other).Amount);

    public override string ToString() => $"{Currency} {Amount:0.00}";

    /// <summary>Adding rupees to dollars is a defect, not a rounding question.</summary>
    private static Money Assert(Money left, Money right) =>
        left.Currency == right.Currency
            ? right
            : throw new InvalidOperationException(
                $"Cannot combine {left.Currency} with {right.Currency}.");
}
