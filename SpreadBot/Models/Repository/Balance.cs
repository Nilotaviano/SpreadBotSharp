namespace SpreadBot.Models.Repository
{
    public class Balance : IMessage
    {
        public Balance() { }

        public MessageType MessageType => MessageType.BalanceData;

        private string currencyAbbreviation;
        public string CurrencyAbbreviation { get => currencyAbbreviation; set => currencyAbbreviation = value.ToUpper(); }
        public decimal Amount { get; set; }
    }

    public class BalanceData
    {
        public long? Sequence { get; set; }

        public Balance Balance { get; set; }
    }
}
