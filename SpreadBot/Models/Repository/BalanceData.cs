using Huobi.Net.Objects;
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

        public BalanceData(HuobiBalance balance)
        {
            CurrencyAbbreviation = balance.Currency;
            Amount = balance.Balance;
        }

        public MessageType MessageType => MessageType.BalanceData;

        private string currencyAbbreviation;
        public string CurrencyAbbreviation { get => currencyAbbreviation; set => currencyAbbreviation = value.ToUpper(); }
        public decimal Amount { get; set; }
    }
}
