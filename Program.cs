using System;
using IBApi;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;

namespace TWS_BOT
{
    class Program
    {
        static async Task Main(string[] args)
        {
            string EVEN_ACCOUNT = "U5135304";    //YOLO
            string ODD_ACCOUNT = "U5153751";   //ROTH

            var wrapper = new EWRAPPER(7496, true);
            await wrapper.GET_NEXT_ORDER_ID();

            if (false)
            {
                await wrapper.CANCEL_ALL_ORDERS();
            }

            //{   //Use to get contract IDs for symbols
            //    string symbol = "BBBY";
            //    var tmp = (await wrapper.GET_SYMBOL_SAMPLES(symbol)).Where(x => x.Contract.Currency == "USD" && x.Contract.Symbol == symbol);
            //    System.Diagnostics.Debugger.Break();
            //}

            //await wrapper.EXECUTE_NEGATIVE_PRICED_SPREADS_STRATEGY("BBBY", 266630, EVEN_ACCOUNT, ODD_ACCOUNT);

            var dividend_stocks_ascending = new ScannerSubscription()
            {
                Instrument = "STK",
                LocationCode = "STK.US",
                ScanCode = "HIGH_DIVIDEND_YIELD_IB",
                NumberOfRows = 750
            };

            var contract_details = await wrapper.GET_SCAN_RESULTS(dividend_stocks_ascending, null);
            var random_generator = new Random();
            foreach (var stonk in contract_details.OrderBy(x => random_generator.Next()))
            {
                Console.WriteLine($"PROCESSING CONTRACT: {stonk.Contract.Symbol}");
                await wrapper.EXECUTE_CONVERSION_ARBITRAGE(stonk.Contract.Symbol ?? stonk.Contract.LocalSymbol, stonk.Contract.ConId, 1.10, ODD_ACCOUNT, 30, 5);
            }

            //await wrapper.EXECUTE_CONVERSION_ARBITRAGE("ZIM", 468091282, 1.20, EVEN_ACCOUNT);
        }
    }
}
