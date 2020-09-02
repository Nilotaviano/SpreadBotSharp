using SpreadBot.Models.API;
using System;
using System.Collections.Generic;
using System.Text;

namespace SpreadBot.Infrastructure
{
    public class ExchangeRequestException : Exception
    {
        public ExchangeRequestException(ApiErrorData apiErrorData)
        {
            ApiErrorData = apiErrorData;
        }

        public ApiErrorData ApiErrorData { get; }
    }
}
