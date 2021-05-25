using SpreadBot.Infrastructure.Exchanges.Bittrex.Models;
using SpreadBot.Models.Repository;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SpreadBot.Infrastructure.Exchanges.Bittrex
{
    public static class BittrexTypeConverter
    {
        public static MarketSummaryData ConvertMarketSummaryData(BittrexApiMarketSummariesData bittrexApiMarketSummariesData)
        {
            if (bittrexApiMarketSummariesData == null)
                return null;

            return new MarketSummaryData()
            {
                Sequence = bittrexApiMarketSummariesData.Sequence,
                Markets = bittrexApiMarketSummariesData.Deltas.Select(ConvertMarket).ToArray()
            };
        }

        public static BalanceData ConvertBalanceData(BittrexApiBalanceData bittrexApiBalanceData)
        {
            if (bittrexApiBalanceData == null)
                return null;

            return new BalanceData()
            {
                Sequence = bittrexApiBalanceData.Sequence,
                Balance = ConvertBalance(bittrexApiBalanceData.Delta)
            };
        }

        public static TickerData ConvertTickerData(BittrexApiTickersData bittrexApiTickersData)
        {
            if (bittrexApiTickersData == null)
                return null;

            return new TickerData()
            {
                Sequence = bittrexApiTickersData.Sequence,
                Markets = bittrexApiTickersData.Deltas?.Select(ConvertMarket).ToArray()
            };
        }

        public static OrderData ConvertOrderData(BittrexApiOrderData bittrexApiOrderData)
        {
            if (bittrexApiOrderData == null)
                return null;

            return new OrderData()
            {
                Sequence = bittrexApiOrderData.Sequence,
                Order = ConvertOrder(bittrexApiOrderData.Delta)
            };
        }
        
        public static Market ConvertMarket(BittrexApiMarketSummariesData.MarketSummary marketSummary)
        {
            return marketSummary?.ToMarketData();
        }

        public static Market ConvertMarket(BittrexApiTickersData.Ticker bittrexTickerData)
        {
            return bittrexTickerData?.ToMarketData();
        }

        public static Market ConvertMarket(BittrexApiMarketData bittrexApiMarketData)
        {
            return bittrexApiMarketData?.ToMarketData();
        }

        public static Order ConvertOrder(BittrexApiOrderData.Order bittrexApiOrder)
        {
            return bittrexApiOrder?.ToOrderData();
        }

        public static Balance ConvertBalance(BittrexApiBalanceData.Balance originalBalance)
        {
            if (originalBalance == null)
                return null;

            return new Balance()
            {
                Amount = originalBalance.Available,
                CurrencyAbbreviation = originalBalance.CurrencySymbol
            };
        }
    }
}
