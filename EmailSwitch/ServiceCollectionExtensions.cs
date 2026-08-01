using EmailSwitch.Common;
using EmailSwitch.Database;
using EmailSwitch.Services.DevConsole;
using EmailSwitch.Services.SendGrid;
using Microsoft.Extensions.DependencyInjection;
using uSignIn.CommonSettings;

namespace EmailSwitch
{
	public static class ServiceCollectionExtensions
	{
		/// <summary>
		/// The host must also call <c>AddMongoDbServices()</c> and <c>AddMongoDbTokenServices()</c>.
		/// EmailSwitch takes a dependency on <c>AbstractTokenService</c> rather than the concrete
		/// <c>MongoDbTokenService</c>, which MongoDbTokenManager only started registering in 10.2.0.
		/// </summary>
		public static void AddEmailSwitchServices(this IServiceCollection services)
		{
			// Idempotent (TryAddSingleton), so this is safe alongside a host that registers it too.
			services.AddCommonSettingsServices();

			services.AddSingleton<EmailSwitchInitializer>();
			services.AddSingleton<EmailSwitchGeneralInitializer>();
			services.AddSingleton<EmailSwitchDbService>();

			// Provider registrations are only constructed when a provider is actually resolved
			// through the keyed lookup below. That is what lets a DevConsole-only setup start with
			// no SendGrid section at all - SendGridInitializer fails fast on missing credentials, so
			// eagerly depending on it anywhere would make credential-free local development
			// impossible.
			services.AddSingleton<SendGridInitializer>();
			services.AddScoped<SendGridService>();
			services.AddScoped<DevConsoleService>();

			// Keyed by provider so EmailSwitchService can resolve one by EmailProvider instead of
			// switching on it. The factories resolve the concrete registrations above, so there is
			// still one instance of each per scope and anything injecting SendGridService directly
			// keeps working.
			services.AddKeyedScoped<IServiceEmails>(EmailProvider.SendGrid, (serviceProvider, _) => serviceProvider.GetRequiredService<SendGridService>());
			services.AddKeyedScoped<IServiceEmails>(EmailProvider.DevConsole, (serviceProvider, _) => serviceProvider.GetRequiredService<DevConsoleService>());

			services.AddScoped<EmailSwitchService>();
		}
	}
}
