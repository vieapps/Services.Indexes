#region Related components
using System;
using System.Linq;
using System.Xml;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Globalization;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using net.vieapps.Components.Utility;
using net.vieapps.Components.Security;
using net.vieapps.Components.Caching;
#endregion

namespace net.vieapps.Services.Indexes
{
	public class ServiceComponent : ServiceBase
	{
		public static Cache Cache { get; internal set; }

		public override string ServiceName => "Indexes";

		public override void Start(string[] args = null, bool initializeRepository = true, Action<IService> next = null)
		{
			Cache = new Cache($"VIEApps-Services-{this.ServiceName}", Components.Utility.Logger.GetLoggerFactory());
			this.Syncable = false;
			base.Start(args, false, next);
		}

		public override async Task<JToken> ProcessRequestAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default)
		{
			// check
			if (!requestInfo.Verb.Equals("GET"))
				throw new MethodNotAllowedException(requestInfo.Verb);

			// process
			var stopwatch = Stopwatch.StartNew();
			await this.WriteLogsAsync(requestInfo, $"Begin request ({requestInfo.Verb} {requestInfo.GetURI()})").ConfigureAwait(false);
			using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.CancellationToken))
				try
				{
					JToken json = null;
					switch (requestInfo.ObjectName.ToLower())
					{
						case "rates":
						case "exchange":
						case "exchanges":
						case "exchangerates":
						case "exchange.rates":
						case "exchange-rates":
							json = await this.ProcessExchangeRatesAsync(requestInfo, cts.Token).ConfigureAwait(false);
							break;

						case "stock":
						case "stocks":
						case "stockquote":
						case "stockquotes":
						case "stock.quote":
						case "stock.quotes":
						case "stock-quote":
						case "stock-quotes":
							json = string.IsNullOrWhiteSpace(requestInfo.GetObjectIdentity())
								? await this.ProcessStockIndexesAsync(requestInfo, cts.Token).ConfigureAwait(false)
								: await this.ProcessStockQuoteAsync(requestInfo, cts.Token).ConfigureAwait(false);
							break;

						default:
							throw new InvalidRequestException($"The request is invalid [({requestInfo.Verb}): {requestInfo.GetURI()}]");
					}
					stopwatch.Stop();
					await this.WriteLogsAsync(requestInfo, $"Success response - Execution times: {stopwatch.GetElapsedTimes()}").ConfigureAwait(false);
					if (this.IsDebugResultsEnabled)
						await this.WriteLogsAsync(requestInfo, $"- Request: {requestInfo.ToString(this.JsonFormat)}" + "\r\n" + $"- Response: {json?.ToString(this.JsonFormat)}").ConfigureAwait(false);
					return json;
				}
				catch (Exception ex)
				{
					throw this.GetRuntimeException(requestInfo, ex, stopwatch);
				}
		}

		#region Exchange rates
		async Task<JToken> ProcessExchangeRatesAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			var cached = await Cache.GetAsync<string>("ExchangeRates", cancellationToken).ConfigureAwait(false);
			if (!string.IsNullOrWhiteSpace(cached))
				return cached.ToJson();

			var exchangeRates = new JObject();
			var xmlExchangeRates = new XmlDocument();
			xmlExchangeRates.LoadXml(await UtilityService.FetchHttpAsync("https://portal.vietcombank.com.vn/Usercontrols/TVPortal.TyGia/pXML.aspx?b=1", cancellationToken).ConfigureAwait(false));
			xmlExchangeRates.DocumentElement.SelectNodes("//ExrateList/Exrate").ToList().ForEach(xmlRate =>
			{
				var code = xmlRate.Attributes["CurrencyCode"].Value.ToUpper();
				var name = xmlRate.Attributes["CurrencyName"].Value.Replace(".", " ").ToLower().GetCapitalizedWords();
				var buy = xmlRate.Attributes["Buy"].Value.Equals("-") ? 0 : xmlRate.Attributes["Buy"].Value.CastAs<double>();
				var sell = xmlRate.Attributes["Sell"].Value.Equals("-") ? 0 : xmlRate.Attributes["Sell"].Value.CastAs<double>();
				var transfer = xmlRate.Attributes["Transfer"].Value.Equals("-") ? 0 : xmlRate.Attributes["Transfer"].Value.CastAs<double>();
				exchangeRates.Add(code, new JObject
				{
					{ "Code", code },
					{ "Name", name },
					{ "Buy", buy },
					{ "Sell", sell },
					{ "Transfer", transfer },
				});
			});

			await Cache.SetAsync("ExchangeRates", exchangeRates.ToString(Newtonsoft.Json.Formatting.None), DateTime.Now.Hour > 7 && DateTime.Now.Hour < 17 ? 7 : 30, cancellationToken).ConfigureAwait(false);

			return exchangeRates;
		}
		#endregion

		#region Stock quotes
		async Task<JToken> ProcessStockIndexesAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			var cached = await Cache.GetAsync<string>("StockIndexes", cancellationToken).ConfigureAwait(false);
			if (!string.IsNullOrWhiteSpace(cached))
				return cached.ToJson();

			var json = new JObject();
			JArray.Parse(await new Uri("https://banggia.cafef.vn/stockhandler.ashx?index=true").FetchHttpAsync(cancellationToken).ConfigureAwait(false))
				.Select(stockIndex => stockIndex as JObject)
				.ToList()
				.ForEach(stockIndex =>
				{
					var info = new JObject();
					foreach (var data in stockIndex)
						if (!data.Key.IsEquals("name"))
							info.Add(data.Key.GetCapitalizedFirstLetter(), data.Value);
					json[stockIndex.Get<string>("name")] = info;
				});

			await Cache.SetAsync("StockIndexes", json.ToString(Newtonsoft.Json.Formatting.None), DateTime.Now.Hour > 7 && DateTime.Now.Hour < 17 ? 7 : 60, cancellationToken).ConfigureAwait(false);
			return json;
		}

		async Task<JToken> ProcessStockQuoteAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			var stockCode = requestInfo.GetObjectIdentity().ToUpper();
			var cached = await Cache.GetAsync<string>($"StockQuote:{stockCode}", cancellationToken).ConfigureAwait(false);
			if (!string.IsNullOrWhiteSpace(cached))
				return cached.ToJson();

			JObject stockInfo = null;
			var headers = new Dictionary<string, string>
			{
				["Referer"] = "https://finance.vietstock.vn",
				["Origin"] = "https://finance.vietstock.vn"
			};

			try
			{
				using (var htmlResponse = await new Uri("https://finance.vietstock.vn").SendHttpRequestAsync(headers, 90, cancellationToken).ConfigureAwait(false))
				{
					var cookies = htmlResponse.Cookies.ToList();
					var languageID = cookies.FirstOrDefault(cookie => cookie.Name == "language")?.Value ?? "vi-VN";
					var sessionID = cookies.FirstOrDefault(cookie => cookie.Name == "ASP.NET_SessionId")?.Value;
					var cookieTokenID = cookies.FirstOrDefault(cookie => cookie.Name == "__RequestVerificationToken")?.Value;

					var html = await htmlResponse.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
					var start = html.IndexOf("<input name=__RequestVerificationToken");
					var end = start > 0 ? html.IndexOf(">", start) : 0;
					start = start > 0 ? html.IndexOf("value=", start) : 0;
					var formTokenID = start > 0 && end > 0 ? html.Substring(start, end - start).ToArray("=").Last() : "";

					headers = new Dictionary<string, string>(headers)
					{
						["Cookie"] = $"finance_viewedstock={stockCode},language={languageID}{(string.IsNullOrWhiteSpace(sessionID) ? "" : $",ASP.NET_SessionId={sessionID}")}{(string.IsNullOrWhiteSpace(cookieTokenID) ? "" : $",__RequestVerificationToken={cookieTokenID}")}",
						["Content-Type"] = "application/x-www-form-urlencoded; charset=utf-8",
						["X-Requested-With"] = "XMLHttpRequest"
					};
					var body = $"code={stockCode}&s=0&t=&__RequestVerificationToken={formTokenID}";
					using (var jsonResponse = await new Uri("https://finance.vietstock.vn/company/tradinginfo").SendHttpRequestAsync("POST", headers, body, 90, cancellationToken).ConfigureAwait(false))
					{
						var results = await jsonResponse.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
						if (this.IsDebugLogEnabled || this.IsDebugResultsEnabled)
							await this.WriteLogsAsync(requestInfo.CorrelationID, $"{stockCode} => {results}").ConfigureAwait(false);
						stockInfo = results?.ToJson() as JObject ?? throw new InformationNotFoundException($"Stock code ({stockCode}) is not found");
					}
				}
			}
			catch (Exception ex)
			{
				throw ex is InformationNotFoundException ? ex : new InformationNotFoundException($"Stock code ({stockCode}) is not found", ex);
			}

			if (stockInfo == null || stockInfo["PriorClosePrice"] == null)
				throw new InformationNotFoundException($"Stock code ({stockCode}) is not found");

			var referencePrice = stockInfo.Get<double>("PriorClosePrice");
			var closePrice = stockInfo.Get<double>("LastPrice");
			var openPrice = stockInfo.Get<double>("OpenPrice");
			var averagePrice = stockInfo.Get<double>("AvrPrice");
			var ceilingPrice = stockInfo.Get<double>("CeilingPrice");
			var floorPrice = stockInfo.Get<double>("FloorPrice");
			var highestPrice = stockInfo.Get<double>("HighestPrice");
			var lowestPrice = stockInfo.Get<double>("LowestPrice");
			var yearHighestPrice = stockInfo.Get<double>("Max52W");
			var yearLowestPrice = stockInfo.Get<double>("Min52W");
			var changeVolume = stockInfo.Get<double>("Change");
			var changePercent = stockInfo.Get<string>("PerChange") + "%";
			var changeMode = stockInfo.Get<int>("ColorId");
			var changeType = "none";
			var changeColor = "yellow";
			if (changeMode > 0)
			{
				changeType = "up";
				changeColor = "green";
			}
			else if (changeMode < 0)
			{
				changeType = "down";
				changeColor = "red";
			}
			var volume = stockInfo.Get<long>("TotalVol");
			var capital = stockInfo.Get<double>("MarketCapital") / 1000000000;
			var shares = stockInfo.Get<long>("KLCPNY");

			var url = $"{this.GetHttpURI("APIs", "https://apis.vieapps.net")}/indexes/stock/{stockCode.UrlEncode()}";
			var name = stockCode;
			try
			{
				var companyInfo = await new Uri($"https://finance.vietstock.vn/search/{stockCode.UrlEncode()}").FetchHttpAsync(headers, 90, cancellationToken).ConfigureAwait(false);
				var info = companyInfo.ToJson().Get<string>("data").ToList('|');
				url = info.First(data => data.IsStartsWith("http://") || data.IsStartsWith("https://")).Replace("http://", "https://");
				name = info[info.IndexOf(stockCode) + 1];
			}
			catch { }

			var enCulture = CultureInfo.GetCultureInfo("en-US");
			var viCulture = CultureInfo.GetCultureInfo("vi-VN");

			var chartsUrl = $"https://chart.vietstock.vn/finance/{stockCode.UrlEncode()}";

			var date = DateTime.Now.DayOfWeek.Equals(DayOfWeek.Saturday)
				? DateTime.Now.AddDays(-1)
				: DateTime.Now.DayOfWeek.Equals(DayOfWeek.Sunday)
					? DateTime.Now.AddDays(-2)
					: DateTime.Now;

			var json = new JObject
			{
				{ "Info", new JObject
					{
						{ "Code", stockCode },
						{ "Name", name },
						{ "Date", date },
						{ "Volume", volume.ToString("###,###,###,##0", viCulture) + " tỷ đ" },
						{ "Capital", capital.ToString("###,###,###,##0", viCulture) + " tỷ đ" },
						{ "Shares", shares.ToString("###,###,###,##0", viCulture) },
						{ "Source", new JObject
							{
								{ "Label", "VietStock.vn" },
								{ "Url", url },
							}
						}
					}
				},
				{ "Prices", new JObject
					{
						{ "Unit", "1.000 đ" },
						{ "Current", (closePrice / 1000).ToString("###,##0.00", enCulture)  },
						{ "Reference", (referencePrice / 1000).ToString("###,##0.00", enCulture) },
						{ "Close", (closePrice / 1000).ToString("###,##0.00", enCulture)  },
						{ "Open", (openPrice / 1000).ToString("###,##0.00", enCulture) },
						{ "Ceiling", (ceilingPrice / 1000).ToString("###,##0.00", enCulture) },
						{ "Floor", (floorPrice / 1000).ToString("###,##0.00", enCulture) },
						{ "Highest", (highestPrice / 1000).ToString("###,##0.00", enCulture) },
						{ "Lowest", (lowestPrice / 1000).ToString("###,##0.00", enCulture) },
						{ "Average", (averagePrice / 1000).ToString("###,##0.00", enCulture) },
						{ "HighestOf52Weeks", (yearHighestPrice / 1000).ToString("###,##0.00", enCulture) },
						{ "LowestOf52Weeks", (yearLowestPrice / 1000).ToString("###,##0.00", enCulture) }
					}
				},
				{ "Changes", new JObject
					{
						{ "Volume", (changeVolume / 1000).ToString("###,##0.00", enCulture) },
						{ "Percent", changePercent },
						{ "Type", changeType},
						{ "Color", changeColor }
					}
				},
				{ "Charts", new JObject
					{
						{ "OneDay", $"{chartsUrl}1D.png?v={UtilityService.GetRandomNumber()}" },
						{ "OneWeek", $"{chartsUrl}1W.png?v={UtilityService.GetRandomNumber()}" },
						{ "OneMonth", $"{chartsUrl}1M.png?v={UtilityService.GetRandomNumber()}" },
						{ "ThreeMonths", $"{chartsUrl}3M.png?v={UtilityService.GetRandomNumber()}" },
						{ "SixMonths", $"{chartsUrl}6M.png?v={UtilityService.GetRandomNumber()}" },
						{ "OneYear", $"{chartsUrl}1Y.png?v={UtilityService.GetRandomNumber()}" }
					}
				}
			};

			await Cache.SetAsync($"StockQuote:{stockCode}", json.ToString(Newtonsoft.Json.Formatting.None), DateTime.Now.Hour > 7 && DateTime.Now.Hour < 17 ? 7 : 60, cancellationToken).ConfigureAwait(false);
			return json;
		}
		#endregion

	}
}