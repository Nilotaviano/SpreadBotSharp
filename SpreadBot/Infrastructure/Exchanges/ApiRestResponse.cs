namespace SpreadBot.Infrastructure.Exchanges
{
    public class ApiRestResponse<T>
    {
        public T Data { get; set; }
        public int Sequence { get; set; }
    }
}