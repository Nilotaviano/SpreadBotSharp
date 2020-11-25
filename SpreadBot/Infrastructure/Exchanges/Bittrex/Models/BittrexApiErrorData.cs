namespace SpreadBot.Infrastructure.Exchanges.Bittrex.Models
{
    public class BittrexApiErrorData
    {
        public string Code { get; set; }
        public string Detail { get; set; }
        public object Data { get; set; }
    }
}
