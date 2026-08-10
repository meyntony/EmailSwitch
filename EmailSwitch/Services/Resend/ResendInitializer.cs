using EmailSwitch.Common;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;

namespace EmailSwitch.Services.Resend
{
	/// <summary>
	/// Composes <see cref="EmailSwitchGeneralInitializer"/> rather than deriving from it, for the same
	/// reason <c>SendGridInitializer</c> does: a forwarding registration would make every consumer of
	/// the general settings depend on Resend credentials being present, which leaves no way to run on
	/// the DevConsole provider alone.
	/// </summary>
	public sealed class ResendInitializer
	{
		/// <summary>
		/// The named client registered in <c>ServiceCollectionExtensions</c>. Registering it there
		/// constructs nothing and reads no configuration, so it does not disturb the rule that a
		/// provider is only built when it is resolved.
		/// </summary>
		public const string HttpClientName = "EmailSwitch.Resend";

		internal static readonly Uri BaseAddress = new("https://api.resend.com/");

		/// <summary>
		/// A send sits on the login path with a caller waiting on it. HttpClient's own 100 second
		/// default is long past the point where the user has given up and asked for another code.
		/// </summary>
		internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

		private readonly IHttpClientFactory _httpClientFactory;

		internal readonly ResendSettings ResendSettings;

		/// <summary>
		/// Carried here and applied per request rather than set on the named client's default headers:
		/// configuring it at <c>AddHttpClient</c> time would need the API key while the container is
		/// still being built, which is the eager credential read that makes a DevConsole-only host
		/// impossible to start.
		/// </summary>
		internal readonly AuthenticationHeaderValue AuthorizationHeader;

		public ResendInitializer(
			EmailSwitchGeneralInitializer emailSwitchGeneralInitializer,
			IHttpClientFactory httpClientFactory,
			ILogger<ResendInitializer> logger)
		{
			_httpClientFactory = httpClientFactory;

			var resendConfig = emailSwitchGeneralInitializer.EmailSwitchSettings.GetSection(EmailProvider.Resend.ToString());

			var from = resendConfig["From"];
			var apiKey = resendConfig["ApiKey"];

			// Named and thrown rather than logged and swallowed. A swallowed failure here leaves the
			// settings null and turns every later send into a caught NullReferenceException, so email
			// silently never goes out behind a single startup log line.
			var settingsPath = $"{ConstantStrings.EmailSwitchSettingsName}:{EmailProvider.Resend}";

			if (string.IsNullOrWhiteSpace(from))
			{
				throw new ArgumentException($"{settingsPath}:From is missing. Resend cannot send without a sender address.", nameof(emailSwitchGeneralInitializer));
			}

			if (string.IsNullOrWhiteSpace(apiKey))
			{
				throw new ArgumentException($"{settingsPath}:ApiKey (the Resend API key) is missing.", nameof(emailSwitchGeneralInitializer));
			}

			ResendSettings = new ResendSettings()
			{
				OtpLength = emailSwitchGeneralInitializer.EmailSwitchGeneralSettings.OtpLength,
				ResendPrivateSettings = new ResendPrivateSettings()
				{
					From = from,
					ApiKey = apiKey
				}
			};
			AuthorizationHeader = new AuthenticationHeaderValue("Bearer", apiKey);

			logger.LogInformation("Resend initialised for sender {From}.", from);
		}

		internal HttpClient CreateClient() => _httpClientFactory.CreateClient(HttpClientName);
	}
}
