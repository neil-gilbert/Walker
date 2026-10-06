using Xunit;
namespace Payments.Tests;
public sealed class PaymentTests
{
    [Fact] public void GreaterBalanceCanPurchase() => Assert.True(new PaymentService().CanPurchase(11, 10));
    [Fact] public void LowerBalanceCannotPurchase() => Assert.False(new PaymentService().CanPurchase(9, 10));
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void BothConditionsRequired(bool active, bool approved, bool expected)
        => Assert.Equal(expected, new PaymentService().CanAuthorise(active, approved));
    [Fact] public void NamedCustomerExists() => Assert.True(new PaymentService().HasCustomer("Ada"));
    // Deliberately weak: both multiplication and division produce a positive fee.
    [Fact] public void FeeIsPositive() => Assert.True(new PaymentService().Fee(10) > 0);
}
