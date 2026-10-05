using System;
using System.Collections.Generic;
using System.Linq;

namespace EplanEdzManager.EplanApi;

public sealed class EplanPartFilterTerm
{
    public string Manufacturer { get; set; } = string.Empty;
    public string PartNumber { get; set; } = string.Empty;
    public string Variant { get; set; } = string.Empty;
}

public sealed class EplanPartFilterBuilder
{
    public const int DefaultMaximumTerms = 100;
    public const int DefaultMaximumLength = 24000;

    public EplanPartFilterBuilder(int maximumTerms = DefaultMaximumTerms, int maximumLength = DefaultMaximumLength)
    {
        if (maximumTerms <= 0) throw new ArgumentOutOfRangeException(nameof(maximumTerms));
        if (maximumLength < 128) throw new ArgumentOutOfRangeException(nameof(maximumLength));
        MaximumTerms = maximumTerms;
        MaximumLength = maximumLength;
    }

    public int MaximumTerms { get; }
    public int MaximumLength { get; }

    public IReadOnlyList<string> BuildBatches(IEnumerable<EplanPartFilterTerm> terms)
    {
        if (terms == null) throw new ArgumentNullException(nameof(terms));
        var clauses = terms
            .Select(ValidateAndBuildClause)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (clauses.Length == 0) throw new ArgumentException("At least one filter term is required.", nameof(terms));

        var batches = new List<string>();
        var current = new List<string>();
        foreach (var clause in clauses)
        {
            if (clause.Length > MaximumLength)
                throw new ArgumentException("A single part filter term exceeds the configured safe length.", nameof(terms));
            var candidateLength = current.Count == 0
                ? clause.Length
                : current.Sum(value => value.Length) + ((current.Count - 1) * 4) + 4 + clause.Length;
            if (current.Count >= MaximumTerms || candidateLength > MaximumLength)
            {
                batches.Add(string.Join(" OR ", current));
                current.Clear();
            }
            current.Add(clause);
        }
        if (current.Count > 0) batches.Add(string.Join(" OR ", current));
        return batches;
    }

    public static string EscapeLiteral(string value)
    {
        if (value == null) throw new ArgumentNullException(nameof(value));
        if (value.IndexOf('\0') >= 0) throw new ArgumentException("EPLAN filter values cannot contain NUL characters.", nameof(value));
        return value.Replace("'", "''");
    }

    private static string ValidateAndBuildClause(EplanPartFilterTerm term)
    {
        if (term == null) throw new ArgumentException("Filter terms cannot contain null.", nameof(term));
        var partNumber = (term.PartNumber ?? string.Empty).Trim();
        if (partNumber.Length == 0) throw new ArgumentException("PartNumber is required for every EPLAN filter term.", nameof(term));
        var partClause = "partnr='" + EscapeLiteral(partNumber) + "'";
        var manufacturer = (term.Manufacturer ?? string.Empty).Trim();
        if (manufacturer.Length == 0) return "(" + partClause + ")";
        return "(manufacturer='" + EscapeLiteral(manufacturer) + "' AND " + partClause + ")";
    }
}
