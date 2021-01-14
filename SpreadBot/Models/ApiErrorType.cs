namespace SpreadBot.Models
{
    public enum ApiErrorType
    {
        None = 0,
        UnknownError,
        InsufficientFunds,
        Throttled,
        MarketOffline,
        DustTrade,
        Unauthorized,
        RetryLater,
        OrderNotOpen,
        CannotEstimateCommission,
        PrecisionNotAllowed
    }
}
