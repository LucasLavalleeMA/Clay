using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Reflection;

namespace Clay;

public static class AppInfo
{
	public static string Version { get; } =
		(Assembly.GetExecutingAssembly()
			.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
			?.InformationalVersion ?? "0.0.0")
		.Split('+')[0];   // strips the commit-hash suffix the .NET 8 SDK adds

	public static string DisplayVersion => $"{Version}";
}