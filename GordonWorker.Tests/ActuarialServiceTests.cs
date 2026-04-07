using GordonWorker.Models;
using GordonWorker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GordonWorker.Tests;

public class ActuarialServiceTests
{
    private readonly ActuarialService _service = new(NullLogger<ActuarialService>.Instance);

    private static AppSettings DefaultSettings() => new()
    {
        SalaryKeywords = "TCP 131,SALARY,PAYROLL",
        FixedCostKeywords = "RENT,INSURANCE,MEDICAL AID,GYM",
        IgnoredFixedCosts = new List<string>(),
    };

    // ----- IsSalary -----

    [Theory]
    [InlineData("ACME PAYROLL DEPOSIT", true)]
    [InlineData("salary credit march", true)]
    [InlineData("TCP 131 EMPLOYER", true)]
    [InlineData("Pick n Pay groceries", false)]
    [InlineData("", false)]
    public void IsSalary_DetectsKeywords(string description, bool expected)
    {
        var tx = new Transaction { Description = description, Amount = 10000m };
        Assert.Equal(expected, _service.IsSalary(tx, DefaultSettings()));
    }

    [Fact]
    public void IsSalary_NullDescription_ReturnsFalse()
    {
        var tx = new Transaction { Description = null, Amount = 10000m };
        Assert.False(_service.IsSalary(tx, DefaultSettings()));
    }

    // ----- IsFixedCost -----

    [Theory]
    [InlineData("Monthly Rent", true)]
    [InlineData("Discovery Medical Aid", true)]
    [InlineData("Coffee shop", false)]
    public void IsFixedCost_DetectsKeywords(string category, bool expected)
    {
        Assert.Equal(expected, _service.IsFixedCost(category, DefaultSettings()));
    }

    [Fact]
    public void IsFixedCost_RespectsIgnoreList()
    {
        var settings = DefaultSettings();
        settings.IgnoredFixedCosts = new List<string> { "GYM" };
        Assert.False(_service.IsFixedCost("Virgin Active GYM", settings));
        Assert.True(_service.IsFixedCost("RENT for office", settings));
    }

    // ----- NormalizeDescription -----

    [Fact]
    public void NormalizeDescription_NullOrWhitespace_ReturnsUncategorized()
    {
        Assert.Equal("Uncategorized", _service.NormalizeDescription(null));
        Assert.Equal("Uncategorized", _service.NormalizeDescription("   "));
    }

    [Fact]
    public void NormalizeDescription_StripsNoiseTokensAndKeepsFirstTwoWords()
    {
        // The normaliser uppercases, strips digits/region tags/payment-jargon, and keeps the first two
        // remaining tokens. This is the contract callers depend on for grouping similar merchants.
        var result = _service.NormalizeDescription("Pick n Pay 12345 ZA JHB DEBIT ORDER");
        Assert.Equal("PICK N", result);
    }

    [Fact]
    public void NormalizeDescription_StripsMonthNames()
    {
        var result = _service.NormalizeDescription("Netflix March Subscription");
        // "March" should be removed, leaving "NETFLIX SUBSCRIPTION" → first two words "NETFLIX SUBSCRIPTION"
        Assert.Equal("NETFLIX SUBSCRIPTION", result);
    }

    // ----- AnalyzeHealthAsync smoke test -----

    [Fact]
    public async Task AnalyzeHealthAsync_EmptyHistory_DoesNotThrowAndReturnsBalance()
    {
        var settings = DefaultSettings();
        var report = await _service.AnalyzeHealthAsync(new List<Transaction>(), currentBalance: 5000m, settings);
        Assert.NotNull(report);
        Assert.Equal(5000m, report.CurrentBalance);
    }
}
