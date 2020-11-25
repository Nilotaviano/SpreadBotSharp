using SpreadBot.Infrastructure.Exchanges.Bittrex.Models;

namespace SpreadBot.Models
{
    public class ApiRestResponse<T>
    {
        public T Data { get; set; }
        public int Sequence { get; set; }
        public ApiErrorType Error { get; set; }
    }
}