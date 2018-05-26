#region Related components
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Collections.Generic;
using System.Globalization;
using System.Xml;

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
		public static Cache Cache { get; } = new Cache("VIEApps-Services-Indexes", UtilityService.GetAppSetting("Cache:ExpirationTime", "30").CastAs<int>(), false, UtilityService.GetAppSetting("Cache:Provider"), Components.Utility.Logger.GetLoggerFactory());

		public override string ServiceName => "Indexes";

		public ServiceComponent() : base() { }

		public override void Start(string[] args = null, bool initializeRepository = true, Func<ServiceBase, Task> nextAsync = null) => base.Start(args, false, nextAsync);

		public override async Task<JObject> ProcessRequestAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default(CancellationToken))
		{
			// check
			if (!requestInfo.Verb.Equals("GET"))
				throw new MethodNotAllowedException(requestInfo.Verb);

			// process
			try
			{
				switch (requestInfo.ObjectName.ToLower())
				{
					case "rates":
					case "exchangerates":
					case "exchange-rates":
						return await this.ProcessExchangeRatesAsync(requestInfo, cancellationToken);

					case "stock":
					case "stockquote":
					case "stockquotes":
					case "stock-quote":
					case "stock-quotes":
						return string.IsNullOrWhiteSpace(requestInfo.GetObjectIdentity())
							? await this.ProcessStockIndexesAsync(requestInfo, cancellationToken)
							: await this.ProcessStockQuoteAsync(requestInfo, cancellationToken);

					default:
						throw new InvalidRequestException($"The request is invalid [({requestInfo.Verb}): {requestInfo.URI}]");
				}
			}
			catch (Exception ex)
			{
				throw this.GetRuntimeException(requestInfo, ex);
			}
		}

		#region Exchange rates
		async Task<JObject> ProcessExchangeRatesAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			var cached = await Cache.GetAsync<string>("ExchangeRates").ConfigureAwait(false);
			if (!string.IsNullOrWhiteSpace(cached))
				return JObject.Parse(cached);

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
				this.SendUpdateMessageAsync(new UpdateMessage()
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
		async Task<JObject> ProcessStockIndexesAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			var cached = await Cache.GetAsync<string>("StockIndexes").ConfigureAwait(false);
			if (!string.IsNullOrWhiteSpace(cached))
				return JObject.Parse(cached);

			var json = new JObject();
			var stockIndexes = JArray.Parse(await UtilityService.GetWebPageAsync("http://banggia.cafef.vn/stockhandler.ashx?index=true", "http://cafef.vn/", UtilityService.DesktopUserAgent, cancellationToken).ConfigureAwait(false));
			foreach (JObject stockIndex in stockIndexes)
			{
				var info = new JObject();
				foreach (var data in stockIndex)
					if (!data.Key.IsEquals("name"))
						info.Add(data.Key.GetCapitalizedFirstLetter(), data.Value);
				json.Add((stockIndex["name"] as JValue).Value as string, info);
			}

			await Task.WhenAll(
				Cache.SetAsync("StockIndexes", json.ToString(Newtonsoft.Json.Formatting.None), 3),
				this.SendUpdateMessageAsync(new UpdateMessage()
				{
					DeviceID = "*",
					Type = "Indexes#StockIndexes",
					Data = json
				}, cancellationToken)
			).ConfigureAwait(false);

			return json;
		}

		async Task<JObject> ProcessStockQuoteAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			var code = requestInfo.GetObjectIdentity().ToUpper();
			var cached = await Cache.GetAsync<string>($"StockQuote:{code}").ConfigureAwait(false);
			if (!string.IsNullOrWhiteSpace(cached))
				return JObject.Parse(cached);

			var enCulture = CultureInfo.GetCultureInfo("en-US");
			var viCulture = CultureInfo.GetCultureInfo("vi-VN");
			var chartsDate = DateTime.Now.AddDays(-1);
			if (DateTime.Now.DayOfWeek.Equals(DayOfWeek.Sunday))
				chartsDate = chartsDate.AddDays(-1);
			else if (DateTime.Now.DayOfWeek.Equals(DayOfWeek.Monday))
				chartsDate = chartsDate.AddDays(-2);
			var chartsUrl = $"https://cafef4.vcmedia.vn/{chartsDate.ToString("yyyyMMdd")}/{code.UrlEncode()}/";

			var stockQuotes = JArray.Parse(await UtilityService.GetWebPageAsync($"https://finance.vietstock.vn/AjaxData/TradingResult/GetStockData.ashx?scode={code.UrlEncode()}", "https://finance.vietstock.vn/", UtilityService.DesktopUserAgent, cancellationToken).ConfigureAwait(false));

			var stockInfo = stockQuotes[0] as JObject;
			var referencePrice = Convert.ToDouble(stockInfo["PriorClosePrice"]);
			var closePrice = (stockInfo["ClosePrice"] as JValue).Value == null ? 0 : Convert.ToDouble(stockInfo["ClosePrice"]);
			var openPrice = Convert.ToDouble(stockInfo["OpenPrice"]);
			var averagePrice = Convert.ToDouble(stockInfo["AvrPrice"]);
			var ceilingPrice = Convert.ToDouble(stockInfo["CeilingPrice"]);
			var floorPrice = Convert.ToDouble(stockInfo["FloorPrice"]);
			var highestPrice = Convert.ToDouble(stockInfo["Highest"]);
			var lowestPrice = Convert.ToDouble(stockInfo["Lowest"]);
			var yearHighestPrice = Convert.ToDouble(stockInfo["YearHigh"]);
			var yearLowestPrice = Convert.ToDouble(stockInfo["YearLow"]);
			var changeVolume = (stockInfo["Oscillate"] as JValue).Value == null ? 0 : Convert.ToDouble(stockInfo["Oscillate"]);
			var changePercent = (stockInfo["PercentOscillate"] as JValue).Value == null ? "0%" : stockInfo["PercentOscillate"].ToString() + "%";
			var changeMode = (stockInfo["ColorId"] as JValue).Value == null ? 0 : Convert.ToInt32(stockInfo["ColorId"]);
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

			var json = new JObject
			{
				{ "Info", new JObject
					{
						{ "Code", code },
						{ "Date", chartsDate },
						{ "Volume", Convert.ToInt32(stockInfo["TradingVolume"]).ToString("###,###,###,##0", viCulture) + " tỷ đ" },
						{ "Capital", Convert.ToInt32(stockInfo["CapitalLevel"]).ToString("###,###,###,##0", viCulture) + " tỷ đ" },
						{ "Shares", Convert.ToInt32(stockInfo["KLCPNY"]).ToString("###,###,###,##0", viCulture) },
						{ "Source", new JObject
							{
								{ "Label", "VietStock.vn" },
								{ "Url", stockInfo["URL"].ToString() },
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
						{ "OneWeek", $"{chartsUrl}7days.png" },
						{ "OneMonth", $"{chartsUrl}1month.png" },
						{ "ThreeMonths", $"{chartsUrl}3months.png" },
						{ "SixMonths", $"{chartsUrl}6months.png" },
						{ "OneYear", $"{chartsUrl}1year.png" },
					}
				},
			};

			await Task.WhenAll(
				Cache.SetAsync($"StockQuote:{code}", json.ToString(Newtonsoft.Json.Formatting.None), 3),
				this.SendUpdateMessageAsync(new UpdateMessage()
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