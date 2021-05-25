using System.Collections.Generic;
using System.Linq;

namespace SpreadBot.Models.Repository
{
    public class CompleteBalanceData
    {
        public CompleteBalanceData(long sequence, IEnumerable<Balance> balances)
        {
            Sequence = sequence;
            Balances = balances;
        }

        public long Sequence { get; set; }

        public IEnumerable<Balance> Balances { get; set; }

    }
}