namespace SpreadBot.Models.Repository
{
    public class BalanceData : IMessage
    {
        public MessageType MessageType => MessageType.BalanceData;

        public string CurrencyAbbreviation { get; set; }
        public decimal Amount { get; set; } //Available, excluding funds locked in open orders
        public decimal Total { get; set; }
    }
}
