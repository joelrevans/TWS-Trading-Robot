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
                var tmp = (await wrapper.GET_SYMBOL_SAMPLES("GME")).Where(x => x.Contract.Currency == "USD");
            }

            var symbols_conid = new Dictionary<string, int>();
            //symbols_conid.Add("UWMC", 467250056);
            //symbols_conid.Add("SNDL", 376499916);
            //symbols_conid.Add("TSLA", 76792991);
            //symbols_conid.Add("GS", 4627828);

            //symbols_conid.Add("CLOV", 464040647);
            //symbols_conid.Add("CLNE", 44465608);
            //symbols_conid.Add("WISH", 460492611);
            //symbols_conid.Add("AMC", 140070600);
            //symbols_conid.Add("BB", 131217639);
            //symbols_conid.Add("GME", 36285627);
            //symbols_conid.Add("RKT", 438058842);
            
            if (true)
            {
                await wrapper.CANCEL_ALL_ORDERS();
                System.Diagnostics.Debugger.Break();
            }

            foreach (var symbol_conid in symbols_conid)
            {
                await wrapper.EXECUTE_NEGATIVE_PRICED_SPREADS_STRATEGY(symbol_conid.Key, symbol_conid.Value, EVEN_ACCOUNT, ODD_ACCOUNT);
            }
        }
    }
}
