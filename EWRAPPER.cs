using System;
using System.Collections.Generic;
using System.Text;
using IBApi;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using TWS_BOT.MODELS;

namespace TWS_BOT
{
    public class EWRAPPER : EWrapper
    {
        private EClientSocket socket { get; set; }
        private EReaderMonitorSignal signal { get; set; }
        private EReader reader { get; set; }

        private static HashSet<int> ACTIVE_REQUESTS = new HashSet<int>();
        private static int reqId_seed = 0;
        private int GET_NEW_REQUEST_ID() {
            int new_request_id = reqId_seed++;
            ACTIVE_REQUESTS.Add(new_request_id);
            return new_request_id;
        }

        private static Queue<DateTime> REQUEST_THROTTLE_PIPELINE = new Queue<DateTime>();
        private async Task REQUESTS_THROTTLE()
        {
            while (REQUEST_THROTTLE_PIPELINE.Count >= 50)
            {
                if (DateTime.Now.Subtract(REQUEST_THROTTLE_PIPELINE.Peek()) > TimeSpan.FromSeconds(1))
                {
                    REQUEST_THROTTLE_PIPELINE.Dequeue();
                }
                else
                {
                    await Task.Yield();
                }
            }
            REQUEST_THROTTLE_PIPELINE.Enqueue(DateTime.Now);
        }

        private static int next_valid_order_id { get; set; }
        private static bool waiting_on_next_valid_order_id = true;
        public async Task<int> GET_NEXT_ORDER_ID()
        {
            waiting_on_next_valid_order_id = true;
            await REQUESTS_THROTTLE();
            socket.reqIds(0);
            while (waiting_on_next_valid_order_id)
            {
                await Task.Yield();
            }
            return next_valid_order_id;
        }

        public EWRAPPER(int port, bool delayed_quotes)
        {
            signal = new EReaderMonitorSignal();
            
            socket = new EClientSocket(this, signal);
            socket.eConnect("localhost", port, 1);
            
            reader = new EReader(socket, signal);
            reader.Start();

            var reader_thread = new Thread(() => {
                while (socket.IsConnected())
                {
                    signal.waitForSignal();
                    reader.processMsgs();
                }
            });
            reader_thread.IsBackground = true;
            reader_thread.Start();
        }

        private static Mutex PLACE_ORDER_MUTEX = new Mutex();
        private HashSet<int> ACTIVE_PLACE_ORDER_REQUESTS = new HashSet<int>();
        public async Task PLACE_ORDER(Contract contract, Order order)
        {
            var order_id = await GET_NEXT_ORDER_ID();
            await REQUESTS_THROTTLE();
            ACTIVE_PLACE_ORDER_REQUESTS.Add(order_id);
            PLACE_ORDER_MUTEX.WaitOne();
            socket.placeOrder(order_id, contract, order);
            PLACE_ORDER_MUTEX.ReleaseMutex();
            while (ACTIVE_PLACE_ORDER_REQUESTS.Contains(order_id))
            {
                await Task.Yield();
            }
        }

        private Dictionary<int, DIVIDEND_SUMMARY> ACTIVE_DIVIDEND_SUMMARY_REQUESTS = new Dictionary<int, DIVIDEND_SUMMARY>();
        public async Task<DIVIDEND_SUMMARY> GET_DIVIDEND_SUMMARY(string symbol)
        {
            var request_id = GET_NEW_REQUEST_ID();
            await REQUESTS_THROTTLE();
            socket.reqMktData(request_id, new Contract() { Symbol = symbol, SecType = "STK", Currency = "USD", Exchange = "SMART" }, "456", false, false, null);
            ACTIVE_DIVIDEND_SUMMARY_REQUESTS.Add(request_id, new DIVIDEND_SUMMARY());
            while (ACTIVE_REQUESTS.Contains(request_id))
            {
                await Task.Yield();
            }
            socket.cancelMktData(request_id);
            var retval = ACTIVE_DIVIDEND_SUMMARY_REQUESTS[request_id];
            ACTIVE_DIVIDEND_SUMMARY_REQUESTS.Remove(request_id);
            return retval;
        }

        public async Task CANCEL_ALL_ORDERS()
        {
            socket.reqGlobalCancel();
        }

        public async Task<IEnumerable<ContractDetails>> GET_ALL_OPTIONS_CONTRACT_DETAILS(string symbol, int contract_id)
        {
            var available_options = new List<ContractDetails>();
            
            var contract = new Contract()
            {
                Symbol = symbol,
                SecType = "OPT",
                Currency = "USD",
                PrimaryExch = "SMART"
            };
            var contract_details = await GET_CONTRACT_DETAILS(contract);
            if (contract_details.Length > 0)
            {
                foreach (var contract_detail in contract_details)
                {
                    available_options.Add(contract_detail);
                }
            }
            
            return available_options.Where(x=>x.UnderConId == contract_id);
        }

        public async Task EXECUTE_NEGATIVE_PRICED_SPREADS_STRATEGY(string symbol, int contract_id, string even_account_id, string odd_account_id)
        {
            var available_options = await GET_ALL_OPTIONS_CONTRACT_DETAILS(symbol, contract_id);
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
                                Account = i % 2 == 0 ? even_account_id : odd_account_id
                            };

                            var difference = calls[i].Contract.Strike - calls[i + 1].Contract.Strike;
                            if (difference > 0)
                            {
                                await PLACE_ORDER(contract, order);
                            }
                            else
                            {
                                throw new Exception("This should be unreachable!");
                            }
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
                                Account = i % 2 == 0 ? even_account_id : odd_account_id
                            };

                            var difference = puts[i].Contract.Strike - puts[i + 1].Contract.Strike;
                            if (difference > 0)
                            {
                                await PLACE_ORDER(contract, order);
                            }
                            else
                            {
                                throw new Exception("This should be unreachable!");
                            }
                        }
                    }
                }
            }
        }

        public async Task EXECUTE_CONVERSION_ARBITRAGE(string symbol, int contract_id, double annualized_return, string account, int max_age, int quantity = 1)
        {
            var symbol_descriptions = await GET_SYMBOL_SAMPLES(symbol);
            var symbol_description = symbol_descriptions.Single(x => x.Contract.ConId == contract_id);
            var option_chain = await GET_OPTION_CHAINS(symbol_description.Contract);
            if(option_chain.Length == 0)
            {
                return;
            }
            var available_options = await GET_ALL_OPTIONS_CONTRACT_DETAILS(symbol, contract_id);
            if (!available_options.Any())
            {
                return;
            }
            var dividend_summary = await GET_DIVIDEND_SUMMARY(symbol);
            foreach (var expiration_group in available_options.Where(x => x.Contract.Exchange == "SMART").GroupBy(x => x.RealExpirationDate))
            {
                var calls = expiration_group.Where(x => x.Contract.Right == "C").OrderByDescending(x => x.Contract.Strike).ToArray();
                var puts = expiration_group.Where(x => x.Contract.Right == "P").OrderByDescending(x => x.Contract.Strike).ToArray();
                for (int i = 0; i + 1 < calls.Count(); ++i)
                {
                    if (calls[i].Contract.Strike != puts[i].Contract.Strike)
                    {
                        throw new Exception("UH OH!");
                    }

                    double dividend = 0; 
                    var expiration_date = HELPER.PARSE_DATE(expiration_group.Key);
                    if(dividend_summary.NEXT_DIVIDEND_DATE != null && dividend_summary.NEXT_DIVIDEND_DATE.Value < expiration_date)
                    {
                        dividend = dividend_summary.NEXT_DIVIDEND_AMOUNT ?? 0;   
                    }

                    int contract_duration = (expiration_date - DateTime.Now).Days;

                    if(contract_duration < 2 || contract_duration > max_age)
                    {
                        continue;
                    }
                    double strike = calls[i].Contract.Strike;
                    double conversion_spread_profit = strike - (strike + dividend) / ((annualized_return - 1) * contract_duration / 365 + 1) - 0.02;

                    var contract = new Contract()
                    {                        
                        Symbol = symbol,
                        SecType = "BAG",
                        Currency = "USD",
                        Exchange = "SMART"
                    };

                    var call_leg = new ComboLeg()
                    {
                        ConId = calls[i].Contract.ConId,
                        Ratio = 1,
                        Action = "SELL",
                        Exchange = calls[i].Contract.Exchange
                    };
                            
                    var put_leg = new ComboLeg()
                    {
                        ConId = puts[i].Contract.ConId,
                        Ratio = 1,
                        Action = "BUY",
                        Exchange = calls[i].Contract.Exchange
                    };

                    var stock_leg = new ComboLeg()
                    {
                        ConId = symbol_description.Contract.ConId,
                        Ratio = int.Parse(calls[i].Contract.Multiplier),
                        Action = "BUY",
                        Exchange = "SMART" //symbol_description.Contract.Exchange??symbol_description.Contract.PrimaryExch
                    };

                    contract.ComboLegs = new List<ComboLeg>();
                    contract.ComboLegs.Add(call_leg);
                    contract.ComboLegs.Add(put_leg);
                    contract.ComboLegs.Add(stock_leg);

                    var order = new Order()
                    {
                        Action = "BUY",
                        OrderType = "LMT",
                        Tif = "DAY",
                        TotalQuantity = quantity,
                        LmtPrice = Math.Round(calls[i].Contract.Strike - Math.Max(conversion_spread_profit, 0), 2, MidpointRounding.ToZero),
                        Account = account
                    };

                    await PLACE_ORDER(contract, order);
                }
            }
        }

        private Dictionary<int, Order> PENDING_SUBMIT_ORDERS { get; set; } = new Dictionary<int, Order>();
        private Dictionary<int, Order> PENDING_CANCEL_ORDERS { get; set; } = new Dictionary<int, Order>();
        private Dictionary<int, Order> PRE_SUBMITTED_ORDERS { get; set; } = new Dictionary<int, Order>();
        private Dictionary<int, Order> SUBMITTED_ORDERS { get; set; } = new Dictionary<int, Order>();
        private Dictionary<int, Order> API_CANCELLED_ORDERS { get; set; } = new Dictionary<int, Order>();
        private Dictionary<int, Order> CANCELLED_ORDERS { get; set; } = new Dictionary<int, Order>();
        private Dictionary<int, Order> FILLED_ORDERS { get; set; } = new Dictionary<int, Order>();
        private Dictionary<int, Order> INACTIVE_ORDERS { get; set; } = new Dictionary<int, Order>();

        public void accountDownloadEnd(string account)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void accountSummary(int reqId, string account, string tag, string value, string currency)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void accountSummaryEnd(int reqId)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void accountUpdateMulti(int requestId, string account, string modelCode, string key, string value, string currency)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void accountUpdateMultiEnd(int requestId)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void bondContractDetails(int reqId, ContractDetails contract)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void commissionReport(CommissionReport commissionReport)
        {
            Console.WriteLine("COMMISSION REPORT");
        }

        public void completedOrder(Contract contract, Order order, OrderState orderState)
        {
            Console.WriteLine("COMPLETED ORDER");
        }

        public void completedOrdersEnd()
        {
            System.Diagnostics.Debugger.Break();
        }

        public void connectAck()
        {
            Console.WriteLine("CONNECT ACK");
        }

        public void connectionClosed()
        {
            System.Diagnostics.Debugger.Break();
        }

        private Dictionary<int, LinkedList<ContractDetails>> CONTRACT_DETAILS_QUEUES = new Dictionary<int, LinkedList<ContractDetails>>();

        public async Task<ContractDetails[]> GET_CONTRACT_DETAILS(Contract contract)
        {
            int request_id = GET_NEW_REQUEST_ID();
            CONTRACT_DETAILS_QUEUES.Add(request_id, new LinkedList<ContractDetails>());
            await REQUESTS_THROTTLE();
            socket.reqContractDetails(request_id, contract);
            while (ACTIVE_REQUESTS.Contains(request_id)) {
                await Task.Yield();
            }
            var contract_details = CONTRACT_DETAILS_QUEUES[request_id];
            CONTRACT_DETAILS_QUEUES.Remove(request_id);
            return contract_details.ToArray();
        }

        public void contractDetails(int reqId, ContractDetails contractDetails)
        {
            CONTRACT_DETAILS_QUEUES[reqId].AddLast(contractDetails);
        }

        public void contractDetailsEnd(int reqId)
        {
            ACTIVE_REQUESTS.Remove(reqId);
        }

        public void currentTime(long time)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void deltaNeutralValidation(int reqId, DeltaNeutralContract deltaNeutralContract)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void displayGroupList(int reqId, string groups)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void displayGroupUpdated(int reqId, string contractInfo)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void error(Exception e)
        {
            Console.WriteLine($"ERROR: {e.Message}");
        }

        public void error(string str)
        {
            Console.WriteLine($"ERROR: {str}");
        }

        void EWrapper.error(int id, int errorCode, string errorMsg, string advancedOrderRejectJson)
        {
            switch (errorCode)
            {
                case 162:
                    break;
                case 165:
                    break;
                case 200:
                    ACTIVE_REQUESTS.Remove(id);
                    ACTIVE_PLACE_ORDER_REQUESTS.Remove(id);
                    Console.WriteLine($"Code: {errorCode}\nMsg: {errorMsg}\nReqId:{id}");
                    break;
                case 201:
                    Console.WriteLine($"Code: {errorCode}\nMsg: {errorMsg}\nReqId:{id}");
                    ACTIVE_PLACE_ORDER_REQUESTS.Remove(id);
                    break;
                case 354:
                    Console.WriteLine($"Code: {errorCode}\nMsg: {errorMsg}\nReqId:{id}");
                    ACTIVE_REQUESTS.Remove(id);
                    break;
                case 2104:
                    break;
                case 2106:
                    break;
                case 2139:
                    //ACKNOWLEDGE PAPER TRADING
                    break;
                case 2158:
                    break;
                default:
                    Console.WriteLine($"Code: {errorCode}\nMsg: {errorMsg}\nReqId:{id}");
                    break;
            }
        }

        public void execDetails(int reqId, Contract contract, Execution execution)
        {
            Console.WriteLine("Exec Details");
        }

        public void execDetailsEnd(int reqId)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void familyCodes(FamilyCode[] familyCodes)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void fundamentalData(int reqId, string data)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void headTimestamp(int reqId, string headTimestamp)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void histogramData(int reqId, HistogramEntry[] data)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void historicalData(int reqId, Bar bar)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void historicalDataEnd(int reqId, string start, string end)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void historicalDataUpdate(int reqId, Bar bar)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void historicalNews(int requestId, string time, string providerCode, string articleId, string headline)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void historicalNewsEnd(int requestId, bool hasMore)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void historicalTicks(int reqId, HistoricalTick[] ticks, bool done)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void historicalTicksBidAsk(int reqId, HistoricalTickBidAsk[] ticks, bool done)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void historicalTicksLast(int reqId, HistoricalTickLast[] ticks, bool done)
        {
            System.Diagnostics.Debugger.Break();
        }

        private string[] account_ids { get; set; }
        public string[] ACCOUNT_IDS { 
            get
            {
                return account_ids;
            }
        }

        public void managedAccounts(string accountsList)
        {
            account_ids = accountsList.Split(',');
        }

        public void marketDataType(int reqId, int marketDataType)
        {
            //Console.WriteLine("marketDataType");
            //System.Diagnostics.Debugger.Break();
        }

        public void marketRule(int marketRuleId, PriceIncrement[] priceIncrements)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void mktDepthExchanges(DepthMktDataDescription[] depthMktDataDescriptions)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void newsArticle(int requestId, int articleType, string articleText)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void newsProviders(NewsProvider[] newsProviders)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void nextValidId(int orderId)
        {
            next_valid_order_id = orderId;
            waiting_on_next_valid_order_id = false;
        }

        public void openOrder(int orderId, Contract contract, Order order, OrderState orderState)
        {
            switch (orderState.Status)
            {
                case "PendingSubmit":
                    if (!PENDING_SUBMIT_ORDERS.ContainsKey(orderId))
                    {
                        PENDING_SUBMIT_ORDERS.Add(orderId, order);
                    }
                    break;
                case "PreSubmitted":
                    if (!PRE_SUBMITTED_ORDERS.ContainsKey(orderId))
                    {
                        PRE_SUBMITTED_ORDERS.Add(orderId, order);
                    }
                    break;
                case "Submitted":
                    if (!SUBMITTED_ORDERS.ContainsKey(orderId))
                    {
                        SUBMITTED_ORDERS.Add(orderId, order);
                    }
                    break;
                case "Filled":
                    if (!FILLED_ORDERS.ContainsKey(orderId))
                    {
                        FILLED_ORDERS.Add(orderId, order);
                    }
                    break;
                case "ApiCancelled":
                    if (!API_CANCELLED_ORDERS.ContainsKey(orderId))
                    {
                        API_CANCELLED_ORDERS.Add(orderId, order);
                    }
                    break;
                case "CancelledOrders":
                    if (!CANCELLED_ORDERS.ContainsKey(orderId))
                    {
                        CANCELLED_ORDERS.Add(orderId, order);
                    }
                    break;
                case "Inactive":
                    if (!INACTIVE_ORDERS.ContainsKey(orderId))
                    {
                        INACTIVE_ORDERS.Add(orderId, order);
                    }
                    break;
            }
            ACTIVE_PLACE_ORDER_REQUESTS.Remove(orderId);
        }

        public void openOrderEnd()
        {
            Console.WriteLine("An order was cancelled.");
        }

        public void orderBound(long orderId, int apiClientId, int apiOrderId)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void orderStatus(int orderId, string status, double filled, double remaining, double avgFillPrice, int permId, int parentId, double lastFillPrice, int clientId, string whyHeld, double mktCapPrice)
        {
            var dictionaries = new Dictionary<int, Order>[]
            {
                PENDING_SUBMIT_ORDERS,
                PENDING_CANCEL_ORDERS,
                PRE_SUBMITTED_ORDERS,
                SUBMITTED_ORDERS,
                FILLED_ORDERS,
                API_CANCELLED_ORDERS,
                CANCELLED_ORDERS,
                INACTIVE_ORDERS
            };
            Dictionary<int, Order> origin_dictionary = null;
            Order selected_order = null;
            foreach(var dictionary in dictionaries)
            {
                if (dictionary.ContainsKey(orderId))
                {
                    origin_dictionary = dictionary;
                    selected_order = dictionary[orderId];
                    break;
                }
            }

            switch (status)
            {
                case "PendingSubmit":
                    break;
            }//TODO
        }

        public void pnl(int reqId, double dailyPnL, double unrealizedPnL, double realizedPnL)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void pnlSingle(int reqId, int pos, double dailyPnL, double unrealizedPnL, double realizedPnL, double value)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void position(string account, Contract contract, double pos, double avgCost)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void positionEnd()
        {
            System.Diagnostics.Debugger.Break();
        }

        public void positionMulti(int requestId, string account, string modelCode, Contract contract, double pos, double avgCost)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void positionMultiEnd(int requestId)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void realtimeBar(int reqId, long date, double open, double high, double low, double close, long volume, double WAP, int count)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void receiveFA(int faDataType, string faXmlData)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void rerouteMktDataReq(int reqId, int conId, string exchange)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void rerouteMktDepthReq(int reqId, int conId, string exchange)
        {
            System.Diagnostics.Debugger.Break();
        }

        Dictionary<int, List<ContractDetails>> SCAN_RESULT_QUEUE = new Dictionary<int, List<ContractDetails>>();
        public async Task<IEnumerable<ContractDetails>> GET_SCAN_RESULTS(ScannerSubscription scan_parameters, List<TagValue> scanner_filter_options)
        {
            var request_id = GET_NEW_REQUEST_ID();
            await REQUESTS_THROTTLE();
            socket.reqScannerSubscription(request_id, scan_parameters, null, scanner_filter_options);
            SCAN_RESULT_QUEUE.Add(request_id, new List<ContractDetails>());
            while (ACTIVE_REQUESTS.Contains(request_id))
            {
                await Task.Yield();
            }
            var retval = SCAN_RESULT_QUEUE[request_id];
            SCAN_RESULT_QUEUE.Remove(request_id);
            return retval;
        }

        public void scannerData(int reqId, int rank, ContractDetails contractDetails, string distance, string benchmark, string projection, string legsStr)
        {
            SCAN_RESULT_QUEUE[reqId].Add(contractDetails);
        }

        public void scannerDataEnd(int reqId)
        {
            ACTIVE_REQUESTS.Remove(reqId);
            socket.cancelScannerSubscription(reqId);
        }

        public void scannerParameters(string xml)
        {
            System.Diagnostics.Debugger.Break();
        }

        public Dictionary<int, LinkedList<OPTION_CHAIN>> OPTION_CHAIN_QUEUE = new Dictionary<int, LinkedList<OPTION_CHAIN>>();
        public async Task<OPTION_CHAIN[]> GET_OPTION_CHAINS(Contract underlying_contract)
        {
            int request_id = GET_NEW_REQUEST_ID();
            OPTION_CHAIN_QUEUE.Add(request_id, new LinkedList<OPTION_CHAIN>());
            await REQUESTS_THROTTLE();
            socket.reqSecDefOptParams(request_id, underlying_contract.Symbol, "", underlying_contract.SecType, underlying_contract.ConId);
            while (ACTIVE_REQUESTS.Contains(request_id))
            {
                await Task.Yield();
            }
            var retval = OPTION_CHAIN_QUEUE[request_id].ToArray();
            OPTION_CHAIN_QUEUE.Remove(request_id);
            return retval;
        }
        public void securityDefinitionOptionParameter(int reqId, string exchange, int underlyingConId, string tradingClass, string multiplier, HashSet<string> expirations, HashSet<double> strikes)
        {
            var option_chain = new OPTION_CHAIN()
            {
                exchange = exchange,
                underlyingConId = underlyingConId,
                tradingClass = tradingClass,
                multiplier = int.Parse(multiplier),
                expirations = expirations,
                strikes = strikes
            };
            OPTION_CHAIN_QUEUE[reqId].AddLast(option_chain);
        }
        public void securityDefinitionOptionParameterEnd(int reqId)
        {
            ACTIVE_REQUESTS.Remove(reqId);
        }

        public void smartComponents(int reqId, Dictionary<int, KeyValuePair<string, char>> theMap)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void softDollarTiers(int reqId, SoftDollarTier[] tiers)
        {
            System.Diagnostics.Debugger.Break();
        }

        private Dictionary<int, ContractDescription[]> SYMBOL_SAMPLES_QUEUE = new Dictionary<int, ContractDescription[]>();
        public async Task<ContractDescription[]> GET_SYMBOL_SAMPLES(string symbol_pattern)
        {
            int request_id = GET_NEW_REQUEST_ID();
            await REQUESTS_THROTTLE();
            socket.reqMatchingSymbols(request_id, symbol_pattern);
            while (ACTIVE_REQUESTS.Contains(request_id))
            {
                await Task.Yield();
            }
            var retval = SYMBOL_SAMPLES_QUEUE[request_id];
            SYMBOL_SAMPLES_QUEUE.Remove(request_id);
            return retval;
        }
        public void symbolSamples(int reqId, ContractDescription[] contractDescriptions)
        {
            SYMBOL_SAMPLES_QUEUE.Add(reqId, contractDescriptions);
            ACTIVE_REQUESTS.Remove(reqId);
        }

        public void tickByTickAllLast(int reqId, int tickType, long time, double price, int size, TickAttribLast tickAttriblast, string exchange, string specialConditions)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void tickByTickBidAsk(int reqId, long time, double bidPrice, double askPrice, int bidSize, int askSize, TickAttribBidAsk tickAttribBidAsk)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void tickByTickMidPoint(int reqId, long time, double midPoint)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void tickEFP(int tickerId, int tickType, double basisPoints, string formattedBasisPoints, double impliedFuture, int holdDays, string futureLastTradeDate, double dividendImpact, double dividendsToLastTradeDate)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void tickGeneric(int tickerId, int field, double value)
        {
            //Console.WriteLine("tickGeneric");
            //System.Diagnostics.Debugger.Break();
        }

        public void tickNews(int tickerId, long timeStamp, string providerCode, string articleId, string headline, string extraData)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void tickOptionComputation(int tickerId, int field, double impliedVolatility, double delta, double optPrice, double pvDividend, double gamma, double vega, double theta, double undPrice)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void tickPrice(int tickerId, int field, double price, TickAttrib attribs)
        {
            //Console.WriteLine("tickPrice");
            //System.Diagnostics.Debugger.Break();
        }

        public void tickReqParams(int tickerId, double minTick, string bboExchange, int snapshotPermissions)
        {
            //Console.WriteLine("tickReqParams");
            //System.Diagnostics.Debugger.Break();
        }

        public void tickSize(int tickerId, int field, int size)
        {
            //Console.WriteLine("tickSize");
            //System.Diagnostics.Debugger.Break();
        }

        public void tickSnapshotEnd(int tickerId)
        {
            ACTIVE_REQUESTS.Remove(tickerId);
        }

        public void tickString(int tickerId, int field, string value)
        {
            if(field == 59)
            {
                string[] dividend_properties = value.Split(',');
                var dividend_summary = ACTIVE_DIVIDEND_SUMMARY_REQUESTS[tickerId];
                if (!string.IsNullOrWhiteSpace(dividend_properties[0]))
                {
                    dividend_summary.NEXT_12_MONTH_DIVIDEND_TOTAL = double.Parse(dividend_properties[0]);
                }
                if (!string.IsNullOrWhiteSpace(dividend_properties[1]))
                {
                    dividend_summary.PREV_12_MONTH_DIVIDEND_TOTAL = double.Parse(dividend_properties[1]);
                }

                if (!string.IsNullOrWhiteSpace(dividend_properties[2]))
                {
                    dividend_summary.NEXT_DIVIDEND_DATE = HELPER.PARSE_DATE(dividend_properties[2]);
                }
                if (!string.IsNullOrWhiteSpace(dividend_properties[3]))
                {
                    dividend_summary.NEXT_DIVIDEND_AMOUNT = double.Parse(dividend_properties[3]);
                }
                ACTIVE_REQUESTS.Remove(tickerId);
            }
        }

        public void updateAccountTime(string timestamp)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void updateAccountValue(string key, string value, string currency, string accountName)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void updateMktDepth(int tickerId, int position, int operation, int side, double price, int size)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void updateMktDepthL2(int tickerId, int position, string marketMaker, int operation, int side, double price, int size, bool isSmartDepth)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void updateNewsBulletin(int msgId, int msgType, string message, string origExchange)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void updatePortfolio(Contract contract, double position, double marketPrice, double marketValue, double averageCost, double unrealizedPNL, double realizedPNL, string accountName)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void verifyAndAuthCompleted(bool isSuccessful, string errorText)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void verifyAndAuthMessageAPI(string apiData, string xyzChallenge)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void verifyCompleted(bool isSuccessful, string errorText)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void verifyMessageAPI(string apiData)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void tickOptionComputation(int tickerId, int field, int tickAttrib, double impliedVolatility, double delta, double optPrice, double pvDividend, double gamma, double vega, double theta, double undPrice)
        {
            System.Diagnostics.Debugger.Break();
        }

        public void replaceFAEnd(int reqId, string text)
        {
            System.Diagnostics.Debugger.Break();
        }

        void EWrapper.tickSize(int tickerId, int field, decimal size)
        {
            Console.WriteLine("tickSize");
            //System.Diagnostics.Debugger.Break();
        }

        void EWrapper.updatePortfolio(Contract contract, decimal position, double marketPrice, double marketValue, double averageCost, double unrealizedPNL, double realizedPNL, string accountName)
        {
            System.Diagnostics.Debugger.Break();
        }

        void EWrapper.orderStatus(int orderId, string status, decimal filled, decimal remaining, double avgFillPrice, int permId, int parentId, double lastFillPrice, int clientId, string whyHeld, double mktCapPrice)
        {
            //Console.WriteLine("orderStatus");
            //System.Diagnostics.Debugger.Break();
        }

        void EWrapper.updateMktDepth(int tickerId, int position, int operation, int side, double price, decimal size)
        {
            System.Diagnostics.Debugger.Break();
        }

        void EWrapper.updateMktDepthL2(int tickerId, int position, string marketMaker, int operation, int side, double price, decimal size, bool isSmartDepth)
        {
            System.Diagnostics.Debugger.Break();
        }

        void EWrapper.position(string account, Contract contract, decimal pos, double avgCost)
        {
            System.Diagnostics.Debugger.Break();
        }

        void EWrapper.realtimeBar(int reqId, long date, double open, double high, double low, double close, decimal volume, decimal WAP, int count)
        {
            System.Diagnostics.Debugger.Break();
        }

        void EWrapper.positionMulti(int requestId, string account, string modelCode, Contract contract, decimal pos, double avgCost)
        {
            System.Diagnostics.Debugger.Break();
        }

        void EWrapper.pnlSingle(int reqId, decimal pos, double dailyPnL, double unrealizedPnL, double realizedPnL, double value)
        {
            System.Diagnostics.Debugger.Break();
        }

        void EWrapper.tickByTickAllLast(int reqId, int tickType, long time, double price, decimal size, TickAttribLast tickAttribLast, string exchange, string specialConditions)
        {
            System.Diagnostics.Debugger.Break();
        }

        void EWrapper.tickByTickBidAsk(int reqId, long time, double bidPrice, double askPrice, decimal bidSize, decimal askSize, TickAttribBidAsk tickAttribBidAsk)
        {
            System.Diagnostics.Debugger.Break();
        }

        void EWrapper.wshMetaData(int reqId, string dataJson)
        {
            System.Diagnostics.Debugger.Break();
        }

        void EWrapper.wshEventData(int reqId, string dataJson)
        {
            System.Diagnostics.Debugger.Break();
        }

        void EWrapper.historicalSchedule(int reqId, string startDateTime, string endDateTime, string timeZone, HistoricalSession[] sessions)
        {
            System.Diagnostics.Debugger.Break();
        }

        void EWrapper.userInfo(int reqId, string whiteBrandingId)
        {
            System.Diagnostics.Debugger.Break();
        }
    }
}
