﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutomaticStockTrader.Core.Alpaca;
using AutomaticStockTrader.Domain;
using AutomaticStockTrader.Repository;

namespace AutomaticStockTrader.Core.Strategies.MeanReversionStrategy
{
    public class MeanReversionStrategy : IStrategy
    {
        public Task<bool?> ShouldBuyStock(IList<StockInput> HistoricalData)
        {
            if (HistoricalData.Count > 20)
            {
                HistoricalData = HistoricalData.OrderByDescending(x => x.Time).Take(20).ToList();

                var avg = HistoricalData.Select(x => x.ClosingPrice).Average();
                var diff = avg - HistoricalData.OrderByDescending(x => x.Time).First().ClosingPrice;

                return Task.FromResult<bool?>(diff >= 0);
            }
            else
            {
                return Task.FromResult<bool?>(null);
            }
        }
    }
}
