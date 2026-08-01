using EmailSwitch.Common;
using EmailSwitch.Common.DTOs;
using EmailSwitch.Common.Logo;
using EmailSwitch.Database.DTOs;
using HumanLanguages;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using MongoDbService;
using MongoDbTokenManager;
using SMSwitch.Common.DTOs;
using uSignIn.CommonSettings.Settings;

namespace EmailSwitch.Database
{
	public sealed class EmailSwitchDbService
	{
		private readonly IMongoCollection<EmailSwitchSession> _emailSwitchSessionCollection;
		private readonly EmailSwitchInitializer _emailSwitchInitializer;
		private readonly AbstractTokenService _tokenService;
		private readonly EmailSwitchGeneralInitializer _emailSwitchGeneralInitializer;
		private readonly SettingsService _settingsService;
		private readonly ILogger<EmailSwitchDbService> _logger;
		private readonly SemaphoreSlim _indexGate = new(1, 1);
		private volatile bool _indexReady;

		public EmailSwitchDbService(
			MongoService mongoService,
			EmailSwitchInitializer emailSwitchInitializer,
			AbstractTokenService tokenService,
			EmailSwitchGeneralInitializer emailSwitchGeneralInitializer,
			SettingsService settingsService,
			ILogger<EmailSwitchDbService> logger)
		{
			_emailSwitchSessionCollection = mongoService.Database.GetCollection<EmailSwitchSession>(nameof(EmailSwitchSession), new MongoCollectionSettings() { ReadConcern = ReadConcern.Majority, WriteConcern = WriteConcern.WMajority });

			_emailSwitchInitializer = emailSwitchInitializer;
			_tokenService = tokenService;
			_emailSwitchGeneralInitializer = emailSwitchGeneralInitializer;
			_settingsService = settingsService;
			_logger = logger;
		}

		/// <summary>
		/// Creates the session lookup index on first use. Deferred out of the constructor so building
		/// the DI container does not block on a network round trip, and a failed attempt is not
		/// cached so a transient outage does not leave the collection permanently unindexed.
		///
		/// No TTL index here on purpose: the README documents these sessions as an audit record, so
		/// they are kept rather than reaped.
		/// </summary>
		private async Task EnsureSessionIndex()
		{
			if (_indexReady)
			{
				return;
			}

			await _indexGate.WaitAsync();
			try
			{
				if (_indexReady)
				{
					return;
				}

				// Compound, matching GetLatestSession: equality on EmailId then descending
				// ExpiryTimeUTC, which serves both the range predicate and the sort. Left unnamed so
				// MongoDB auto-names it and recreating an existing index is a no-op.
				var indexModel = new CreateIndexModel<EmailSwitchSession>(
					Builders<EmailSwitchSession>.IndexKeys
						.Ascending(session => session.EmailId)
						.Descending(session => session.ExpiryTimeUTC));

				await _emailSwitchSessionCollection.Indexes.CreateOneAsync(indexModel);
				_indexReady = true;
			}
			catch (Exception exception)
			{
				// A missing index costs performance, not correctness, so let the caller proceed.
				_logger.LogWarning(exception, "Unable to ensure the lookup index on {Collection}; queries will scan the collection.", nameof(EmailSwitchSession));
			}
			finally
			{
				_indexGate.Release();
			}
		}

		internal async Task<EmailSwitchSession?> GetOrCreateAndGetLatestSession(EmailIdentifier emailPendingVerification, MobileNumber[] verifiedMobileNumbers, EmailIdentifier[] verifiedEmails, HashSet<LanguageIsoCode> preferredLanguageIsoCodeList, UserAgent userAgent)
		{
			var latestSession = await GetLatestSession(emailPendingVerification);
			if (latestSession != null)
			{
				return latestSession;
			}

			// The session id is needed to mint the token and to build the logo URL, so it is settled
			// before the session itself is constructed.
			var sessionId = Guid.NewGuid().ToString();
			var startTimeUTC = DateTime.UtcNow;
			var sessionTimeoutInSeconds = _emailSwitchInitializer.EmailControls.SessionTimeoutInSeconds;

			var generatedCode = await _tokenService.Generate(
								logId: typeof(EmailSwitchDbService).FullName ?? nameof(EmailSwitchDbService),
								id: sessionId,
								validityInSeconds: sessionTimeoutInSeconds,
								numberOfDigits: _emailSwitchGeneralInitializer.EmailSwitchGeneralSettings.OtpLength);

			latestSession = new EmailSwitchSession()
			{
				SessionId = sessionId,
				EmailId = emailPendingVerification.ToString(),
				StartTimeUTC = startTimeUTC,
				ExpiryTimeUTC = startTimeUTC.AddSeconds(sessionTimeoutInSeconds),
				SendOTPEmail = EmailTemplates.TemplateCreator.CreateSendOTPEmail(
					emailPendingVerification: emailPendingVerification,
					verifiedMobileNumbers: verifiedMobileNumbers,
					verifiedEmails: verifiedEmails,
					preferredLanguageIsoCodeList: preferredLanguageIsoCodeList,
					userAgent: userAgent,
					generatedCode: generatedCode,
					// Shown to the reader a little short of the real deadline, so a code is not
					// advertised as valid in the seconds while it is expiring.
					expiryDateTimeOffset: startTimeUTC.AddSeconds(sessionTimeoutInSeconds - 10),
					signatureLogoUri: new Uri(_settingsService.BaseUri, EmailSignatureLogoEndpoint.EmailSignatureLogoRelativeUrl(sessionId)))
			};

			await _emailSwitchSessionCollection.InsertOneAsync(latestSession);

			return latestSession;
		}

		internal async Task<EmailSwitchSession?> GetLatestSession(EmailIdentifier emailPendingVerification)
		{
			await EnsureSessionIndex();

			// Storing the timestamps as plain BSON dates lets the server apply the expiry and
			// already-verified rules and do the sort, so at most a handful of documents come back.
			// The two remaining conditions in HasNotExpired count array members, which is awkward to
			// express as a filter and pointless against a set this small, so they stay in memory.
			var candidates = await _emailSwitchSessionCollection
				.Find(Builders<EmailSwitchSession>.Filter.And(
					Filter(emailPendingVerification),
					Builders<EmailSwitchSession>.Filter.Eq(session => session.SuccessfullyVerifiedTimestampUTC, null),
					Builders<EmailSwitchSession>.Filter.Gt(session => session.ExpiryTimeUTC, DateTime.UtcNow)))
				.SortByDescending(session => session.ExpiryTimeUTC)
				.Limit(16)
				.ToListAsync();

			return candidates.FirstOrDefault(session => session.HasNotExpired(_emailSwitchInitializer.EmailControls.MaximumFailedAttemptsToVerify));
		}

		private FilterDefinition<EmailSwitchSession> Filter(string sessionId) => Builders<EmailSwitchSession>.Filter.Eq(t => t.SessionId, sessionId);
		private FilterDefinition<EmailSwitchSession> Filter(EmailIdentifier emailPendingVerification) => Builders<EmailSwitchSession>.Filter.Eq(t => t.EmailId, emailPendingVerification.ToString());

		internal async Task UpdateSession(EmailSwitchSession session)
		{
			var options = new ReplaceOptions { IsUpsert = true };
			await _emailSwitchSessionCollection.ReplaceOneAsync(Filter(session.SessionId), session, options);
		}

		internal async Task RegisterRenderRequest(string id)
		{
			// A single targeted push, rather than read-modify-replace, so concurrent opens of the
			// same email cannot overwrite each other's render records.
			await _emailSwitchSessionCollection.UpdateOneAsync(
				Filter(id),
				Builders<EmailSwitchSession>.Update.Push(session => session.LogoRenderedAttemptsUTC, DateTime.UtcNow));
		}
	}
}
