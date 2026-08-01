using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDbService;
using MongoDbTokenManager;
using System.Text.RegularExpressions;

namespace EmailSwitch.Tests
{
	/// <summary>
	/// Builds a real container through <c>AddEmailSwitchServices()</c>, so tests exercise the actual
	/// registrations rather than a hand-assembled object graph. Constructing services by hand cannot
	/// catch a missing or mis-keyed registration, which is exactly the kind of thing that breaks a
	/// consumer at startup.
	///
	/// MongoDB is not dialled: the driver connects lazily, so a container can be built and services
	/// resolved without a server.
	/// </summary>
	internal static class TestHost
	{
		internal const string UnreachableMongo = "mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=150&connectTimeoutMS=150&socketTimeoutMS=150";

		/// <summary>Settings every configuration needs, with no provider section.</summary>
		internal static Dictionary<string, string?> BaseSettings(string? mongoConnectionString = null) => new()
		{
			["MongoDbSettings:ConnectionString"] = mongoConnectionString ?? UnreachableMongo,
			["MongoDbSettings:DatabaseName"] = "EmailSwitchTests",
			["MongoDbSettings:ConnectionRecordRetentionDays"] = "0",

			["Settings:BaseUrl"] = "https://api.example.com",
			["Settings:FrontendUrl"] = "https://app.example.com",

			["EmailSwitchSettings:OtpLength"] = "6",
			["EmailSwitchSettings:SignatureLogoPath"] = "logo.png",
			["EmailSwitchSettings:Controls:MaximumFailedAttemptsToVerify"] = "3",
			["EmailSwitchSettings:Controls:SessionTimeoutInSeconds"] = "240"
		};

		internal static Dictionary<string, string?> WithSendGrid(this Dictionary<string, string?> settings)
		{
			settings["EmailSwitchSettings:SendGrid:From"] = "noreply@example.com";
			settings["EmailSwitchSettings:SendGrid:Password"] = "SG.fake-api-key";
			return settings;
		}

		internal static Dictionary<string, string?> WithPriority(this Dictionary<string, string?> settings, params string[] providers)
		{
			for (var index = 0; index < providers.Length; index++)
			{
				settings[$"EmailSwitchSettings:Controls:Priority:{index}"] = providers[index];
			}
			return settings;
		}

		internal static ServiceProvider Build(Dictionary<string, string?> settings, string environmentName = "Development", ILoggerProvider? loggerProvider = null)
		{
			var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

			var services = new ServiceCollection();
			services.AddSingleton<IConfiguration>(configuration);
			services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(environmentName));
			services.AddLogging(builder => builder.AddProvider(loggerProvider ?? NullLoggerProvider.Instance));

			services.AddMongoDbServices();
			services.AddMongoDbTokenServices();
			services.AddEmailSwitchServices();

			return services.BuildServiceProvider();
		}

		/// <summary>
		/// Captures log output so a test can read the verification code the way a developer running on
		/// DevConsole does - off the console.
		///
		/// Tests used to read the code back out of the stored session instead. That stopped working,
		/// correctly, once the rendered email began being retired as soon as it could no longer be
		/// needed: the cleartext code is no longer sitting in the database to be read. Going through
		/// the log keeps these tests on the path a developer actually uses, and independent of where
		/// the code is or is not persisted.
		/// </summary>
		internal sealed class LogCapture : ILoggerProvider
		{
			private readonly List<string> _messages = [];

			public ILogger CreateLogger(string categoryName) => new CapturingLogger(_messages);

			/// <summary>The six digit code DevConsole wrote, or empty if it never logged one.</summary>
			public string CapturedOtp
			{
				get
				{
					lock (_messages)
					{
						return _messages
							.Select(message => Regex.Match(message, @"Verification Code: (\d+)"))
							.Where(match => match.Success)
							.Select(match => match.Groups[1].Value)
							.LastOrDefault() ?? string.Empty;
					}
				}
			}

			public void Dispose() { }

			private sealed class CapturingLogger(List<string> messages) : ILogger
			{
				public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

				public bool IsEnabled(LogLevel logLevel) => true;

				public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
				{
					lock (messages)
					{
						messages.Add(formatter(state, exception));
					}
				}
			}
		}

		private sealed class TestHostEnvironment : IHostEnvironment
		{
			public TestHostEnvironment(string environmentName) => EnvironmentName = environmentName;

			public string EnvironmentName { get; set; }
			public string ApplicationName { get; set; } = "EmailSwitch.Tests";
			public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
			public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
		}
	}
}
