using EmailSwitch.Database;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace EmailSwitch.Common.Logo
{
	public static class EmailSignatureLogoEndpoint
	{
		public static string EmailSignatureLogoRelativeUrl(string id) => $"{ConstantStrings.EmailSwitchGroupName}{EmailSignatureLogoRoute}{id}";

		public const string EmailSignatureLogoRoute = "/logo/";
		public static RouteGroupBuilder GroupEmailSignatureLogoApisV1(this RouteGroupBuilder group)
		{
			group.MapGet(EmailSignatureLogoRoute + "{id}", async (string id,
				EmailSwitchGeneralInitializer emailSwitchGeneralInitializer,
				EmailSwitchDbService emailSwitchDbService,
				ILogger <RouteGroupBuilder> logger) =>
			{
				try
				{
					var settings = emailSwitchGeneralInitializer.EmailSwitchGeneralSettings;

					if (settings.SignatureLogoBytes.Length == 0)
					{
						// Previously this returned 200 with an empty body, which looks like a broken
						// image rather than a missing one.
						logger.LogCritical("No signature logo is loaded; unable to render the LOGO in email :{ID}.", id);
						return Results.NotFound();
					}

					// Recording the render is diagnostic, so its own failure must not cost the reader
					// the image. Contained here rather than left as an unobserved fire-and-forget task.
					try
					{
						await emailSwitchDbService.RegisterRenderRequest(id);
					}
					catch (Exception ex)
					{
						logger.LogWarning(ex, "Unable to record the LOGO render request for :{ID}.", id);
					}

					return Results.File(settings.SignatureLogoBytes, settings.SignatureLogoContentType);
				}
				catch (Exception ex)
				{
					logger.LogCritical(ex, "Unable to render LOGO in email :{ID}.", id);
					return Results.NotFound();
				}
			})
			.Produces(StatusCodes.Status200OK)
			.Produces(StatusCodes.Status404NotFound);

			return group;
		}
	}
}
