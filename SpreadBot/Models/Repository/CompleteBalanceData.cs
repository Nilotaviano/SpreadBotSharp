using System.Collections.Generic;
using System.Linq;
using SpreadBot.Infrastructure.Exchanges;
using SpreadBot.Models.API;

namespace SpreadBot.Models.Repository
{
    public class CompleteBalanceData
    {
        public CompleteBalanceData(ApiRestResponse<ApiBalanceData.Balance[]> balancesData)
        {
            Sequence = balancesData.Sequence;
            Balances = balancesData.Data?.Select(balance => new BalanceData(balance));
        }

        public int Sequence { get; private set; }

        public IEnumerable<BalanceData> Balances { get; private set; }

    }
}