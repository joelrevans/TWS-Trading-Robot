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

            {   //Use to get contract IDs for symbols
                //var tmp = (await wrapper.GET_SYMBOL_SAMPLES("IRBT")).Where(x => x.Contract.Currency == "USD");
                //System.Diagnostics.Debugger.Break();
            }

            var dividend_stocks_ascending = new ScannerSubscription()
            {
                Instrument = "STK",
                LocationCode = "STK.US",
                ScanCode = "HIGH_DIVIDEND_YIELD_IB",
                NumberOfRows = 750
            };

            if (true)
            {
                await wrapper.CANCEL_ALL_ORDERS();
            }

            var contract_details = await wrapper.GET_SCAN_RESULTS(dividend_stocks_ascending);
            foreach (var stonk in contract_details)
            {
                Console.WriteLine($"PROCESSING CONTRACT: {stonk.Contract.Symbol}");
                await wrapper.EXECUTE_CONVERSION_ARBITRAGE(stonk.Contract.Symbol ?? stonk.Contract.LocalSymbol, stonk.Contract.ConId, 1.50, EVEN_ACCOUNT);
            }
        }
    }
}
