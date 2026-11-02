using LiftUp.Core.Domain;
using Xunit;

namespace LiftUp.Core.Tests;

public sealed class PriceTextTests
{
    [Theory]
    [InlineData("5000", 5000)]
    [InlineData("5000.00", 5000)]
    [InlineData("5000.5", 5000.5)]
    [InlineData("5000,50", 5000.5)]
    [InlineData("5'000.50", 5000.5)]
    [InlineData("1.250,50", 1250.5)]
    [InlineData("1,250.50", 1250.5)]
    [InlineData("5.000", 5000)]
    [InlineData("CHF 3000.-", 3000)]
    [InlineData("Fr. 1'200.25", 1200.25)]
    [InlineData("3000.00", 3000)]
    [InlineData("-250", -250)]
    public void Preise_werden_auch_ohne_Nachkommastellen_erkannt(string input, double expected)
    {
        Assert.Equal((decimal)expected, PriceText.Parse(input));
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("   ", 0)]
    [InlineData("keine Zahl", 0)]
    [InlineData("-", 0)]
    public void Unlesbare_Eingaben_zaehlen_als_null(string? input, double expected)
    {
        Assert.Equal((decimal)expected, PriceText.Parse(input));
    }

    [Fact]
    public void Summe_aus_Eingaben_ohne_Nachkommastellen()
    {
        var sum = PriceText.Parse("5000") + PriceText.Parse("2500.50") + PriceText.Parse("CHF 500.-");

        Assert.Equal(8000.5m, sum);
        Assert.Equal("8000.50", PriceText.Format(sum));
    }
}
