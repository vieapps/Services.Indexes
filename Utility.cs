#region Related components
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;

using Newtonsoft.Json.Linq;

using net.vieapps.Components.Utility;
using net.vieapps.Components.Caching;
#endregion

namespace net.vieapps.Services.Indexes
{
	public static class Utility
	{
		static int _CacheTime = 0;

		internal static int CacheExpirationTime
		{
			get
			{
				if (Utility._CacheTime < 1)
					try
					{
						Utility._CacheTime = UtilityService.GetAppSetting("Cache:ExpirationTime", "30").CastAs<int>();
					}
					catch
					{
						Utility._CacheTime = 30;
					}
				return Utility._CacheTime;
			}
		}

		static Cache _Cache = new Cache("VIEApps-Services-Systems", Utility.CacheExpirationTime, UtilityService.GetAppSetting("Cache:Provider"));

		public static Cache Cache { get { return Utility._Cache; } }
	}
}