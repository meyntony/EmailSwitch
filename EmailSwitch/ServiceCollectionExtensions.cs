using EmailSwitch.Common;
using EmailSwitch.Database;
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

			// SendGridInitializer derives from EmailSwitchGeneralInitializer. Registering both
			// independently produced two instances, each reading the signature logo from disk, and
			// left EmailSignatureLogoEndpoint and EmailSwitchDbService reading different copies.
			// Forwarding the base type to the same singleton keeps one instance and one disk read.
			// Revisit this if EmailProvider ever gains a second provider - the base registration
			// would then no longer belong to SendGrid.
			services.AddSingleton<SendGridInitializer>();
			services.AddSingleton<EmailSwitchGeneralInitializer>(serviceProvider => serviceProvider.GetRequiredService<SendGridInitializer>());

			services.AddSingleton<EmailSwitchDbService>();

			services.AddScoped<SendGridService>();

			services.AddScoped<EmailSwitchService>();
		}
	}
}
