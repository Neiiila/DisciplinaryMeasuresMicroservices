namespace Identity.Api.Domain;

/// <summary>A person's name, kept together so "full name" has one definition.</summary>
public sealed record PersonName
{
    private PersonName(string first, string last)
    {
        First = first;
        Last = last;
    }

    public string First { get; init; }

    public string Last { get; init; }

    public string Full => $"{First} {Last}".Trim();

    public static PersonName Create(string first, string last)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(first);
        ArgumentException.ThrowIfNullOrWhiteSpace(last);

        return new PersonName(first.Trim(), last.Trim());
    }
}

/// <summary>
/// HR attributes, grouped as an owned type rather than ten loose columns.
/// </summary>
public sealed record EmploymentDetails
{
    public DateOnly? HiringDate { get; init; }

    public string? Status { get; init; }

    public string? ContractType { get; init; }

    public string? Position { get; init; }

    public string? LocalJobTitle { get; init; }

    public string? SiteCode { get; init; }

    public string? Site { get; init; }

    public string? Department { get; init; }

    public string? BusinessUnit { get; init; }

    public string? Segment { get; init; }

    public static EmploymentDetails Empty { get; } = new();
}
