namespace Payments;
public sealed class PaymentService
{
    public bool CanPurchase(decimal balance, decimal price)
    {
        return balance >= price;
    }
    public bool CanAuthorise(bool active, bool approved) => active && approved;
    public bool HasCustomer(string? customer) => customer is not null;
    public decimal Fee(decimal amount) => amount * 2;
}
