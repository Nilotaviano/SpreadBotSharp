using SpreadBot.Models.Repository;

namespace SpreadBot.Models
{
    /// <summary>
    /// An order was sent but the exchange's answer doesn't say whether it was accepted (e.g. the request timed out).
    /// Placing it again could duplicate it; <see cref="Order"/> is the order as sent, with status
    /// <see cref="OrderStatus.UNKNOWN"/>, so its client order id can be looked up later.
    /// </summary>
    public class OrderStateUnknownException : ApiException
    {
        public OrderStateUnknownException(OrderData order, string message) : base(ApiErrorType.OrderStateUnknown, message)
        {
            Order = order;
        }

        public OrderData Order { get; }
    }
}
