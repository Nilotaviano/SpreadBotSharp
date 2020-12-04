using System;
using System.Collections.Generic;
using System.Text;

namespace SpreadBot.Models
{
    public class ApiException : Exception
    {
        public ApiException(ApiErrorType apiErrorType, string message) : base(message)
        {
            ApiErrorType = apiErrorType;
        }

        public ApiErrorType ApiErrorType { get; private set; }
    }
}
