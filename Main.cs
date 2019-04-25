#region Related components
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Collections.Generic;
using System.Globalization;
using System.Xml;
using System.IO;

using Newtonsoft.Json;
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

		public override void Start(string[] args = null, bool initializeRepository = true, Func<IService, Task> nextAsync = null)
		{
			// initialize caching storage
			Cache = new Cache($"VIEApps-Services-{this.ServiceName}", Components.Utility.Logger.GetLoggerFactory());

			// start the service
			base.Start(args, false, nextAsync);
		}

		public override async Task<JToken> ProcessRequestAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default(CancellationToken))
		{
			// check
			if (!requestInfo.Verb.Equals("GET"))
				throw new MethodNotAllowedException(requestInfo.Verb);

			// process
			var stopwatch = Stopwatch.StartNew();
			this.WriteLogs(requestInfo, $"Begin request ({requestInfo.Verb} {requestInfo.GetURI()})");
			try
			{
				JToken json = null;
				switch (requestInfo.ObjectName.ToLower())
				{
					case "rates":
					case "exchangerates":
					case "exchange-rates":
						json = await this.ProcessExchangeRatesAsync(requestInfo, cancellationToken).ConfigureAwait(false);
						break;

					case "stock":
					case "stockquote":
					case "stockquotes":
					case "stock-quote":
					case "stock-quotes":
						json = string.IsNullOrWhiteSpace(requestInfo.GetObjectIdentity())
							? await this.ProcessStockIndexesAsync(requestInfo, cancellationToken).ConfigureAwait(false)
							: await this.ProcessStockQuoteAsync(requestInfo, cancellationToken).ConfigureAwait(false);
						break;

					default:
						throw new InvalidRequestException($"The request is invalid [({requestInfo.Verb}): {requestInfo.GetURI()}]");
				}
				stopwatch.Stop();
				this.WriteLogs(requestInfo, $"Success response - Execution times: {stopwatch.GetElapsedTimes()}");
				if (this.IsDebugResultsEnabled)
					this.WriteLogs(requestInfo,
						$"- Request: {requestInfo.ToJson().ToString(this.IsDebugLogEnabled ? Newtonsoft.Json.Formatting.Indented : Newtonsoft.Json.Formatting.None)}" + "\r\n" +
						$"- Response: {json?.ToString(this.IsDebugLogEnabled ? Newtonsoft.Json.Formatting.Indented : Newtonsoft.Json.Formatting.None)}"
					);
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
			var cached = await Cache.GetAsync<string>("ExchangeRates").ConfigureAwait(false);
			if (!string.IsNullOrWhiteSpace(cached))
				return cached.ToJson();

			var url = "https://www.vietcombank.com.vn/ExchangeRates/ExrateXML.aspx";
			var xmlExchangeRates = new XmlDocument();
			xmlExchangeRates.LoadXml(await UtilityService.GetWebPageAsync(url, null, UtilityService.DesktopUserAgent, cancellationToken).ConfigureAwait(false));
			var xmlRates = xmlExchangeRates.DocumentElement.SelectNodes("//ExrateList/Exrate");

			var exchangeRates = new JObject();
			foreach (XmlNode xmlRate in xmlRates)
			{
				var code = xmlRate.Attributes["CurrencyCode"].Value.ToUpper();
				var name = xmlRate.Attributes["CurrencyName"].Value.Replace(".", " ").ToLower().GetCapitalizedWords();
				var buy = xmlRate.Attributes["Buy"].Value.CastAs<double>();
				var sell = xmlRate.Attributes["Sell"].Value.CastAs<double>();
				var transfer = xmlRate.Attributes["Transfer"].Value.CastAs<double>();
				exchangeRates.Add(code, new JObject()
					{
						{ "Code", code },
						{ "Name", name },
						{ "Buy", buy },
						{ "Sell", sell },
						{ "Transfer", transfer },
					});
			}

			await Task.WhenAll(
				Cache.SetAsync("ExchangeRates", exchangeRates.ToString(Newtonsoft.Json.Formatting.None), 7),
				this.SendUpdateMessageAsync(new UpdateMessage
				{
					DeviceID = "*",
					Type = "Indexes#ExchangeRates",
					Data = exchangeRates
				}, cancellationToken)
			).ConfigureAwait(false);

			return exchangeRates;
		}
		#endregion

		#region Stock quotes
		async Task<JToken> ProcessStockIndexesAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			var cached = await Cache.GetAsync<string>("StockIndexes").ConfigureAwait(false);
			if (!string.IsNullOrWhiteSpace(cached))
				return cached.ToJson();

			var json = new JObject();
			var stockIndexes = JArray.Parse(await UtilityService.GetWebPageAsync("http://banggia.cafef.vn/stockhandler.ashx?index=true", "http://cafef.vn/", UtilityService.DesktopUserAgent, cancellationToken).ConfigureAwait(false));
			foreach (JObject stockIndex in stockIndexes)
			{
				var info = new JObject();
				foreach (var data in stockIndex)
					if (!data.Key.IsEquals("name"))
						info.Add(data.Key.GetCapitalizedFirstLetter(), data.Value);
				json[stockIndex.Get<string>("name")] = info;
			}

			await Task.WhenAll(
				Cache.SetAsync("StockIndexes", json.ToString(Newtonsoft.Json.Formatting.None), 3),
				this.SendUpdateMessageAsync(new UpdateMessage
				{
					DeviceID = "*",
					Type = "Indexes#StockIndexes",
					Data = json
				}, cancellationToken)
			).ConfigureAwait(false);

			return json;
		}

		async Task<JToken> ProcessStockQuoteAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			var code = requestInfo.GetObjectIdentity().ToUpper();
			var cached = await Cache.GetAsync<string>($"StockQuote:{code}").ConfigureAwait(false);
			if (!string.IsNullOrWhiteSpace(cached))
				return cached.ToJson();

			JObject stockInfo = null;
			try
			{
				using (var stream = await UtilityService.GetWebResourceAsync("POST", $"https://finance.vietstock.vn/company/tradinginfo", null, $"code={code}&s=0&t=", "application /x-www-form-urlencoded; charset=utf-8", 90, UtilityService.DesktopUserAgent, "https://finance.vietstock.vn/", null, null, true, System.Net.SecurityProtocolType.Ssl3, null, cancellationToken).ConfigureAwait(false))
				{
					using (var reader = new StreamReader(stream, true))
					{
						var results = await reader.ReadToEndAsync().WithCancellationToken(cancellationToken).ConfigureAwait(false);
						if (this.IsDebugLogEnabled || this.IsDebugResultsEnabled)
							await this.WriteLogsAsync(requestInfo.CorrelationID, $"{code} => {results}").ConfigureAwait(false);
						stockInfo = results?.ToJson() as JObject ?? throw new InformationNotFoundException($"Stock code ({code}) is not found");
					}
				}
			}
			catch (InformationNotFoundException)
			{
				throw;
			}
			catch (Exception ex)
			{
				throw new InformationNotFoundException($"Stock code ({code}) is not found", ex);
			}

			if (stockInfo == null || stockInfo["PriorClosePrice"] == null)
				throw new InformationNotFoundException($"Stock code ({code}) is not found");

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

			var url = $"{this.GetHttpURI("HttpUri:APIs", "https://apis.vieapps.net")}/indexes/stock/{code.UrlEncode()}";
			var name = code;
			try
			{
				var companyInfo = await UtilityService.GetWebPageAsync($"https://finance.vietstock.vn/search/{code.UrlEncode()}", "https://finance.vietstock.vn/", UtilityService.DesktopUserAgent, cancellationToken).ConfigureAwait(false);
				var info = companyInfo.ToJson().Get<string>("data").ToList('|');
				url = info.First(data => data.IsStartsWith("http://") || data.IsStartsWith("https://")).Replace("http://", "https://");
				name = info[info.IndexOf(code) + 1];
			}
			catch { }

			var enCulture = CultureInfo.GetCultureInfo("en-US");
			var viCulture = CultureInfo.GetCultureInfo("vi-VN");

			var chartsUrl = $"https://chart.vietstock.vn/finance/{code.UrlEncode()}";

			var date = DateTime.Now.AddDays(-1);
			if (DateTime.Now.DayOfWeek.Equals(DayOfWeek.Sunday))
				date = date.AddDays(-1);
			else if (DateTime.Now.DayOfWeek.Equals(DayOfWeek.Monday))
				date = date.AddDays(-2);

			var json = new JObject
			{
				{ "Info", new JObject
					{
						{ "Code", code },
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
						},
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
						{ "LowestOf52Weeks", (yearLowestPrice / 1000).ToString("###,##0.00", enCulture) },
					}
				},
				{ "Changes", new JObject
					{
						{ "Volume", (changeVolume / 1000).ToString("###,##0.00", enCulture) },
						{ "Percent", changePercent },
						{ "Type", changeType},
						{ "Color", changeColor },
					}
				},
				{ "Charts", new JObject
					{
						{ "OneDay", $"{chartsUrl}1D.png?v={UtilityService.GetUUID(UtilityService.NewUUID)}" },
						{ "OneWeek", $"{chartsUrl}1W.png?v={UtilityService.GetUUID(UtilityService.NewUUID)}" },
						{ "OneMonth", $"{chartsUrl}1M.png?v={UtilityService.GetUUID(UtilityService.NewUUID)}" },
						{ "ThreeMonths", $"{chartsUrl}3M.png?v={UtilityService.GetUUID(UtilityService.NewUUID)}" },
						{ "SixMonths", $"{chartsUrl}6M.png?v={UtilityService.GetUUID(UtilityService.NewUUID)}" },
						{ "OneYear", $"{chartsUrl}1Y.png?v={UtilityService.GetUUID(UtilityService.NewUUID)}" },
					}
				},
			};

			await Task.WhenAll(
				Cache.SetAsync($"StockQuote:{code}", json.ToString(Newtonsoft.Json.Formatting.None), DateTime.Now.Hour > 7 && DateTime.Now.Hour < 16 ? 5 : 30),
				this.SendUpdateMessageAsync(new UpdateMessage
				{
					DeviceID = "*",
					Type = "Indexes#StockQuote",
					Data = json
				}, cancellationToken)
			).ConfigureAwait(false);

			return json;
		}
		#endregion

	}
}