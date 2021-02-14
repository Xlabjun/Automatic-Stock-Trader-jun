using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Linq;
using Moq;
using System.Threading.Tasks;
using System.Diagnostics;
using AutomaticStockTrader.Core.Alpaca;
using AutomaticStockTrader.Core.Configuration;
using AutomaticStockTrader.Core.Strategies;
using AutomaticStockTrader.Core.Strategies.MLStrategy;
using AutomaticStockTrader.Core.Strategies.MeanReversionStrategy;
using AutomaticStockTrader.Core.Strategies.MicrotrendStrategy;
using AutomaticStockTrader.Repository;
using AutomaticStockTrader.Domain;
using AutomaticStockTrader.Repository.Models;
using Order = AutomaticStockTrader.Domain.Order;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Alpaca.Markets;

namespace AutomaticStockTrader.Tests.Stategies
{
    [TestClass, TestCategory("Large")]
    public class LargeStrategiesTests
    {
        private IAlpacaClient _alpacaClient;
        private Mock<IAlpacaClient> _mockAlpacaClient;
        private StockContext _context;
        private ITrackingRepository _repo;
        private IConfigurationRoot _config;

        [TestInitialize]
        public void SetUp()
        {
            LaunchSettingsFixture.SetupEnvVars();
            _config = new ConfigurationBuilder()
                .AddJsonFile($"appsettings.json", true, true)
                .AddEnvironmentVariables()
                .Build();

            var alpacaConfig = _config.Get<AlpacaConfig>();
            var env = alpacaConfig.Alpaca_Use_Live_Api ? Environments.Live : Environments.Paper;
            var key = new SecretKey(alpacaConfig.Alpaca_App_Id, alpacaConfig.Alpaca_Secret_Key);

            _alpacaClient = new AlpacaClient(
                Options.Create(alpacaConfig), 
                env.GetAlpacaTradingClient(key), 
                env.GetAlpacaStreamingClient(key), 
                env.GetAlpacaDataClient(key), 
                env.GetAlpacaDataStreamingClient(key)
            );

            _mockAlpacaClient = new Mock<IAlpacaClient>();
            _mockAlpacaClient.Setup(x => x.GetTotalEquity()).ReturnsAsync(100_000m);

            _context = new StockContext();
            _context.Database.EnsureDeleted();
            _context.Database.EnsureCreated(); 

            _repo = new TrackingRepository(_context);
        }

        [TestCleanup]
        public void CleanUp()
        {
            _alpacaClient?.Dispose();
            _repo?.Dispose();
        }

        [TestMethod]
        public async Task ShouldBuyStock_MeanReversionStrategy_MakesMoney()
        {
            var strategy = new MeanReversionStrategy();
            
            var totalMoneyMade = await TestStrategy(strategy);

            if (totalMoneyMade == 0) Assert.Inconclusive("No money lost or made");
            Assert.IsTrue(totalMoneyMade > 0);        
        }

        [TestMethod]
        public async Task ShouldBuyStock_MicrotrendStrategy_MakesMoney()
        {
            var strategy = new MicrotrendStrategy();

            var totalMoneyMade = await TestStrategy(strategy);

            if(totalMoneyMade == 0) Assert.Inconclusive("No money lost or made");
            Assert.IsTrue(totalMoneyMade > 0);
        }

        [TestMethod]
        public async Task ShouldBuyStock_MLStrategy_MakesMoney()
        {
            var strategy = new MLStrategy(_config.Get<MLConfig>());

            var totalMoneyMade = await TestStrategy(strategy, true);

            if (totalMoneyMade == 0) Assert.Inconclusive("No money lost or made");
            Assert.IsTrue(totalMoneyMade > 0);
        }

        private async Task<decimal> TestStrategy(IStrategy strategy, bool useHistoricalData = false)
        {
            var totalMoneyMade = 0m;

            foreach (var stock in _config.Get<StockConfig>().Stock_List)
            {
                var strategyHandler = new StrategyHandler(Mock.Of<ILogger<StrategyHandler>>(), _mockAlpacaClient.Object, _repo, strategy, TradingFrequency.Minute, 0.1m, stock);

                var closingPrice = await TestStrategyOnStock(strategyHandler, stock, useHistoricalData);

                var orders = _context.Orders
                    .Where(x => x.Position.StockSymbol == stock)
                    .Select(x => new { quantity = x.ActualSharesBought.Value, price = x.ActualCostPerShare.Value })
                    .ToList();            

                var moneyMade = orders.Any() 
                    ? (orders.Select(x => x.quantity * x.price).Aggregate((x, y) => x + y) + orders.Select(x => x.quantity).Aggregate((x, y) => x + y) * closingPrice) * (-1)
                    : 0;

                Debug.WriteLine($"Money made on {stock}: {moneyMade}");
             
                totalMoneyMade += moneyMade;
                Debug.WriteLine($"Total so far: {totalMoneyMade}");
            }

            return totalMoneyMade;
        }

        private async Task<decimal> TestStrategyOnStock(StrategyHandler strategy, string stock, bool useHistoricaData)
        {
            var data = (await _alpacaClient.GetStockData(stock)).OrderBy(x => x.Time).ToList();

            var sizeOfTestSet = useHistoricaData ? data.Count / 5 : data.Count;
            var testData = data.Take(sizeOfTestSet);
            
            strategy.HistoricalData.Clear();
            strategy.HistoricalData.AddRange(data.Skip(sizeOfTestSet).ToList());

            var lastPrice = 0m;
            foreach (var min in testData)
            {
                _mockAlpacaClient
                    .Setup(x => x.PlaceOrder(It.Is<StrategysStock>(x => x.StockSymbol == min.StockSymbol), It.IsAny<Order>()))
                    .Callback<StrategysStock, Order>((s, o) => _repo.CompleteOrder(new CompletedOrder 
                    { 
                        StockSymbol = min.StockSymbol, 
                        MarketPrice = min.ClosingPrice, 
                        SharesBought = o.SharesBought 
                    }).Wait());

                await strategy.HandleNewData(min);
                lastPrice = min.ClosingPrice;
            }

            return lastPrice;
        }
    }
}
