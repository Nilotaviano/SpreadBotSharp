using SpreadBot.Infrastructure.Exchanges.Bittrex.Models;

namespace SpreadBot.Models.Repository
{
    public class BalanceData : IMessage
    {
        public BalanceData() { }

        public BalanceData(BittrexApiBalanceData.Balance balance)
        {
            CurrencyAbbreviation = balance.CurrencySymbol;
            Amount = balance.Available;
        }

        public MessageType MessageType => MessageType.BalanceData;

        public string CurrencyAbbreviation { get; set; }
        public decimal Amount { get; set; }
    }
}
