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
            symbols_conid.Add("AMC", 140070600);
            //symbols_conid.Add("BB", 131217639);
            //symbols_conid.Add("GME", 36285627);
            //symbols_conid.Add("RKT", 438058842);
            
            if (true)
            {
                await wrapper.CANCEL_ALL_ORDERS();
                System.Diagnostics.Debugger.Break();
            }

            if (true)
            {
                foreach (var symbol_conid in symbols_conid)
                {
                    var available_options = new LinkedList<ContractDetails>();
                    var symbol = symbol_conid.Key;
                    var conid = symbol_conid.Value;
                    var symbols_data = (await wrapper.GET_SYMBOL_SAMPLES(symbol)).Where(x => x.Contract.Currency == "USD");
                    var symbol_data = symbols_data.Single(x => x.Contract.ConId == conid);

                    var option_chains = await wrapper.GET_OPTION_CHAINS(symbol_data.Contract);
                    foreach (var option_chain in option_chains.Where(x => x.exchange == "SMART"))
                    {
                        foreach (var expiration in option_chain.expirations)
                        {
                            foreach (var strike in option_chain.strikes)
                            {
                                var contract = new Contract()
                                {
                                    Strike = strike,
                                    LastTradeDateOrContractMonth = expiration,
                                    Symbol = symbol,
                                    SecType = "OPT",
                                    Currency = "USD",
                                    Multiplier = option_chain.multiplier,
                                    PrimaryExch = option_chain.exchange
                                };
                                var contract_details = await wrapper.GET_CONTRACT_DETAILS(contract);
                                if (contract_details.Length > 0)
                                {
                                    foreach (var contract_detail in contract_details)
                                    {
                                        available_options.AddLast(contract_detail);
                                        if (available_options.Count() % 100 == 0)
                                        {
                                            Console.WriteLine(available_options.Count());
                                        }
                                    }
                                }
                            }
                        }
                    }
                    File.WriteAllText($"cached_options_{symbol}.json", JsonSerializer.Serialize(available_options));
                }
            }

            foreach (var symbol_conid in symbols_conid)
            {
                var available_options = new LinkedList<ContractDetails>();
                var symbol = symbol_conid.Key;
                var cached_options = await File.ReadAllTextAsync($"cached_options_{symbol}.json");
                available_options = JsonSerializer.Deserialize<LinkedList<ContractDetails>>(cached_options);
                foreach (var exchange_groups in available_options.GroupBy(x => x.Contract.Exchange).Where(x => x.Key == "SMART"))
                {
                    foreach (var expiration_group in exchange_groups.GroupBy(x => x.RealExpirationDate))
                    {
                        var calls = expiration_group.Where(x => x.Contract.Right == "C").OrderByDescending(x => x.Contract.Strike).ToArray();
                        var puts = expiration_group.Where(x => x.Contract.Right == "P").OrderByDescending(x => x.Contract.Strike).ToArray();
                        for (int i = 0; i + 1 < calls.Count(); ++i)
                        {
                            if (calls[i].Contract.Strike != puts[i].Contract.Strike)
                            {
                                throw new Exception("UH OH!");
                            }
                            {   //CALLS
                                var contract = new Contract()
                                {
                                    Symbol = symbol,
                                    SecType = "BAG",
                                    Currency = "USD",
                                    Exchange = exchange_groups.Key
                                };

                                var upper_leg = new ComboLeg()
                                {
                                    ConId = calls[i].Contract.ConId,
                                    Ratio = 1,
                                    Action = "SELL",
                                    Exchange = calls[i].Contract.Exchange
                                };

                                var lower_leg = new ComboLeg()
                                {
                                    ConId = calls[i + 1].Contract.ConId,
                                    Ratio = 1,
                                    Action = "BUY",
                                    Exchange = calls[i + 1].Contract.Exchange
                                };

                                contract.ComboLegs = new List<ComboLeg>();
                                contract.ComboLegs.Add(upper_leg);
                                contract.ComboLegs.Add(lower_leg);

                                var order = new Order()
                                {
                                    Action = "BUY",
                                    OrderType = "LMT",
                                    Tif = "GTC",
                                    TotalQuantity = 5,
                                    LmtPrice = -0.02,
                                    Account = i % 2 == 0 ? EVEN_ACCOUNT : ODD_ACCOUNT
                                };

                                var difference = calls[i].Contract.Strike - calls[i + 1].Contract.Strike;
                                if (difference > 0)
                                {
                                    await wrapper.PLACE_ORDER(contract, order);
                                }   //ELSE ERR
                            }

                            { //PUTS
                                var contract = new Contract()
                                {
                                    Symbol = symbol,
                                    SecType = "BAG",
                                    Currency = "USD",
                                    Exchange = exchange_groups.Key
                                };

                                var upper_leg = new ComboLeg()
                                {
                                    ConId = puts[i].Contract.ConId,
                                    Ratio = 1,
                                    Action = "BUY",
                                    Exchange = puts[i].Contract.Exchange
                                };

                                var lower_leg = new ComboLeg()
                                {
                                    ConId = puts[i + 1].Contract.ConId,
                                    Ratio = 1,
                                    Action = "SELL",
                                    Exchange = puts[i + 1].Contract.Exchange
                                };

                                contract.ComboLegs = new List<ComboLeg>();
                                contract.ComboLegs.Add(upper_leg);
                                contract.ComboLegs.Add(lower_leg);

                                var order = new Order()
                                {
                                    Action = "BUY",
                                    OrderType = "LMT",
                                    Tif = "GTC",
                                    TotalQuantity = 5,
                                    LmtPrice = -0.02,
                                    Account = i % 2 == 0 ? EVEN_ACCOUNT : ODD_ACCOUNT
                                };

                                var difference = puts[i].Contract.Strike - puts[i + 1].Contract.Strike;
                                if (difference > 0)
                                {
                                    await wrapper.PLACE_ORDER(contract, order);
                                }   //ELSE ERR
                            }
                        }
                    }
                }
            }
        }
    }
}
