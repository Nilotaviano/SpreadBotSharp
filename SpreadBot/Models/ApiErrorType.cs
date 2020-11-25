namespace SpreadBot.Infrastructure.Exchanges.Bittrex.Models
{
    public enum ApiErrorType
    {
        None = 0,
        UnknownError,
        InsufficientFunds,
        Throttled,
        MarketOffline,
        DustTrade,
        Unauthorized
    }
}
