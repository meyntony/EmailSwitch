using EmailSwitch.Common;
using EmailSwitch.Common.DTOs;
using EmailSwitch.Common.Logo;
using EmailSwitch.Database.DTOs;
using HumanLanguages;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
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
		/// <summary>
		/// GetOrCreateAndGetLatestSession keeps at most one live session per address, so this only
		/// has to be large enough to cover stale rows that have not yet aged out of the filter.
		/// </summary>
		private const int MaximumCandidateSessions = 16;

		private const int IndexOptionsConflictErrorCode = 85;
		private const int IndexKeySpecsConflictErrorCode = 86;

		private readonly SemaphoreSlim _indexGate = new(1, 1);
		private volatile bool _indexReady;
		private volatile bool _indexWarningLogged;

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
		/// Creates the session indexes on first use. Deferred out of the constructor so building the
		/// DI container does not block on a network round trip, and a failed attempt is not cached so
		/// a transient outage does not leave the collection permanently unindexed.
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

				// Sparse, so it only covers sessions currently holding the claim. That is what makes
				// creation atomic without penalising the ones that have handed it back.
				await _emailSwitchSessionCollection.Indexes.CreateOneAsync(new CreateIndexModel<EmailSwitchSession>(
					Builders<EmailSwitchSession>.IndexKeys.Ascending(session => session.LiveClaimKey),
					new CreateIndexOptions { Unique = true, Sparse = true }));

				await EnsureRetentionIndex();
				_indexReady = true;
			}
			catch (Exception exception)
			{
				// A missing index costs performance, not correctness, so let the caller proceed. The
				// attempt is not marked ready, so a transient outage does not leave the collection
				// permanently unindexed - but the warning is logged once rather than on every query
				// for as long as the server is unreachable.
				if (!_indexWarningLogged)
				{
					_indexWarningLogged = true;
					_logger.LogWarning(exception, "Unable to ensure the lookup index on {Collection}; queries will scan the collection.", nameof(EmailSwitchSession));
				}
			}
			finally
			{
				_indexGate.Release();
			}
		}

		/// <summary>
		/// Creates the TTL index that reaps sessions <c>SessionRetentionDays</c> after they expire.
		/// Sessions hold the verified address, so keeping them forever is a storage-limitation problem
		/// as much as a disk one; a retention of zero or less is honoured as an explicit choice to
		/// keep them and simply creates no index.
		///
		/// Separate from the lookup index because a TTL index cannot be compound. Left unnamed so the
		/// server derives ExpiryTimeUTC_1 - requesting the same key pattern under a different name is
		/// itself a conflict later.
		/// </summary>
		private async Task EnsureRetentionIndex()
		{
			var sessionRetentionDays = _emailSwitchInitializer.EmailControls.SessionRetentionDays;

			if (sessionRetentionDays <= 0)
			{
				// Not merely "create nothing". On a collection that already carries the TTL index,
				// returning here left it in place, so sessions went on being reaped on the old
				// schedule while the configuration - and the README - said to keep them forever.
				await DropRetentionIndex();
				return;
			}

			var indexModel = new CreateIndexModel<EmailSwitchSession>(
				Builders<EmailSwitchSession>.IndexKeys.Ascending(session => session.ExpiryTimeUTC),
				new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(sessionRetentionDays) });

			try
			{
				await _emailSwitchSessionCollection.Indexes.CreateOneAsync(indexModel);
			}
			catch (MongoCommandException exception) when (exception.Code is IndexOptionsConflictErrorCode or IndexKeySpecsConflictErrorCode)
			{
				// The index exists with a different expireAfterSeconds. MongoDB refuses to recreate
				// it, so amend it in place - otherwise changing SessionRetentionDays, which the README
				// documents doing, would throw on every call.
				await AmendRetentionOnExistingIndex(sessionRetentionDays);
			}
		}

		/// <summary>
		/// The name of the existing single-field ExpiryTimeUTC index, or null if there is not one.
		/// Both collMod and dropIndex address an index by name and fail on a name that is not there,
		/// so neither can assume the server-derived ExpiryTimeUTC_1.
		///
		/// The single-key requirement is the point of this method. The compound lookup index also
		/// contains ExpiryTimeUTC, so a matcher that forgot <c>ElementCount == 1</c> would hand back
		/// the index every read depends on - and its callers would then either put an expiry on it or
		/// drop it outright.
		/// </summary>
		private async Task<string?> FindRetentionIndexName()
		{
			using var cursor = await _emailSwitchSessionCollection.Indexes.ListAsync();
			var indexes = await cursor.ToListAsync();

			var existing = indexes.FirstOrDefault(index =>
				index.TryGetValue("key", out var key)
				&& key is BsonDocument keyDocument
				&& keyDocument.ElementCount == 1
				&& keyDocument.Contains(nameof(EmailSwitchSession.ExpiryTimeUTC)));

			return existing is not null && existing.TryGetValue("name", out var name) ? name.AsString : null;
		}

		/// <summary>
		/// Removes the TTL index so sessions stop being reaped, which is what a retention of zero or
		/// less asks for. A no-op when there is no such index, so a collection that never had one is
		/// left alone.
		/// </summary>
		private async Task DropRetentionIndex()
		{
			var indexName = await FindRetentionIndexName();

			if (indexName is null)
			{
				return;
			}

			await _emailSwitchSessionCollection.Indexes.DropOneAsync(indexName);

			_logger.LogInformation(
				"Session retention is disabled, so the {IndexName} TTL index on {Collection} was dropped; sessions are now kept indefinitely.",
				indexName,
				nameof(EmailSwitchSession));
		}

		/// <summary>
		/// Amends the expiry on the existing index in place. MongoDB refuses to recreate an index with
		/// different options, so changing the setting - which the README documents doing - would
		/// otherwise throw on every call.
		/// </summary>
		private async Task AmendRetentionOnExistingIndex(int sessionRetentionDays)
		{
			var indexName = await FindRetentionIndexName();

			if (indexName is null)
			{
				// Nothing single-field on ExpiryTimeUTC to amend, so the conflict was about something
				// else. Leave the collection alone rather than inventing an index.
				_logger.LogWarning("Unable to apply a session retention of {SessionRetentionDays} days: no single-field {Field} index was found to amend.", sessionRetentionDays, nameof(EmailSwitchSession.ExpiryTimeUTC));
				return;
			}

			await _emailSwitchSessionCollection.Database.RunCommandAsync<BsonDocument>(new BsonDocument
			{
				{ "collMod", _emailSwitchSessionCollection.CollectionNamespace.CollectionName },
				{ "index", new BsonDocument
					{
						{ "name", indexName },
						{ "expireAfterSeconds", TimeSpan.FromDays(sessionRetentionDays).TotalSeconds }
					}
				}
			});
		}

		internal async Task<EmailSwitchSession?> GetOrCreateAndGetLatestSession(EmailIdentifier emailPendingVerification, MobileNumber[] verifiedMobileNumbers, EmailIdentifier[] verifiedEmails, HashSet<LanguageIsoCode> preferredLanguageIsoCodeList, UserAgent userAgent)
		{
			var latestSession = await GetLatestSession(emailPendingVerification);
			if (latestSession != null)
			{
				return latestSession;
			}

			// Nothing live was found, so a predecessor may still be holding the claim for this
			// address. Hand it back before trying to take it, or a user whose session merely timed
			// out could never open another one.
			await ReleaseStaleClaim(emailPendingVerification.ToString(), _emailSwitchInitializer.EmailControls.MaximumFailedAttemptsToVerify);

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
				LiveClaimKey = emailPendingVerification.ToString(),
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

			try
			{
				await _emailSwitchSessionCollection.InsertOneAsync(latestSession);
			}
			catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
			{
				// Another request opened the live session for this address between our lookup and our
				// insert, and the unique claim index arbitrated. Theirs stands; ours was never
				// emailed, and the token minted for it is never handed out and ages out on its own.
				// Without this both callers would send a different code to the same inbox, and only
				// one of the two would verify.
				_logger.LogInformation("A concurrent request already opened the live session for {Email}; reusing it rather than sending a second code.", emailPendingVerification);

				return await GetLatestSession(emailPendingVerification);
			}

			return latestSession;
		}

		/// <summary>
		/// Hands back the live-session claim from a session that can no longer be used - verified,
		/// timed out, or out of verification attempts.
		///
		/// The conditions mirror exactly what <see cref="GetLatestSession"/> will and will not return.
		/// That equivalence is the point: releasing a claim must never resurrect a session the reader
		/// would have refused, and must never withhold a session the caller could already have had.
		/// </summary>
		private async Task ReleaseStaleClaim(string emailId, byte maximumFailedAttemptsToVerify)
		{
			await _emailSwitchSessionCollection.UpdateManyAsync(
				Builders<EmailSwitchSession>.Filter.And(
					Builders<EmailSwitchSession>.Filter.Eq(session => session.LiveClaimKey, emailId),
					Builders<EmailSwitchSession>.Filter.Or(
						Builders<EmailSwitchSession>.Filter.Ne(session => session.SuccessfullyVerifiedTimestampUTC, null),
						Builders<EmailSwitchSession>.Filter.Lte(session => session.ExpiryTimeUTC, DateTime.UtcNow),
						AttemptsExhausted(maximumFailedAttemptsToVerify))),
				Builders<EmailSwitchSession>.Update.Unset(session => session.LiveClaimKey));
		}

		/// <summary>
		/// Sessions with no verification attempt left. The complement of the condition
		/// <see cref="TryReserveVerificationAttempt"/> claims against, expressed the same two ways so
		/// that sessions written before <see cref="EmailSwitchSession.VerificationAttemptsCount"/>
		/// existed are judged by their audit list instead.
		/// </summary>
		private static FilterDefinition<EmailSwitchSession> AttemptsExhausted(byte maximumFailedAttemptsToVerify) =>
			maximumFailedAttemptsToVerify == 0
				// Nothing may be attempted at all, so every session is exhausted by definition - and
				// the array index below has no meaningful form for it.
				? Builders<EmailSwitchSession>.Filter.Empty
				: Builders<EmailSwitchSession>.Filter.Or(
					Builders<EmailSwitchSession>.Filter.Gte(session => session.VerificationAttemptsCount, maximumFailedAttemptsToVerify),
					Builders<EmailSwitchSession>.Filter.Exists(
						$"{nameof(EmailSwitchSession.FailedVerificationAttemptsUTC)}.{maximumFailedAttemptsToVerify - 1}",
						true));

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
				.Limit(MaximumCandidateSessions)
				.ToListAsync();

			return candidates.FirstOrDefault(session => session.HasNotExpired(_emailSwitchInitializer.EmailControls.MaximumFailedAttemptsToVerify));
		}

		private FilterDefinition<EmailSwitchSession> Filter(string sessionId) => Builders<EmailSwitchSession>.Filter.Eq(t => t.SessionId, sessionId);
		private FilterDefinition<EmailSwitchSession> Filter(EmailIdentifier emailPendingVerification) => Builders<EmailSwitchSession>.Filter.Eq(t => t.EmailId, emailPendingVerification.ToString());

		/// <summary>
		/// Records the outcome of a send: what is left of the budget, and the attempts just made.
		///
		/// Deliberately a targeted update rather than replacing the whole document. SendOTP reads the
		/// session, then awaits a provider over the network before writing, and a full replace reverted
		/// everything the server recorded in that window - a failed verification push, the verified
		/// stamp, a logo render. Losing brute-force increments that way is precisely what
		/// <see cref="RegisterFailedVerificationAttempt"/> exists to avoid, so the send path must not
		/// reintroduce it.
		///
		/// Not an upsert. The session is always inserted by GetOrCreateAndGetLatestSession before this
		/// runs, so a filter that matches nothing means the session has since been reaped by the
		/// retention TTL index - and a reaped session must stay reaped rather than be written back.
		/// </summary>
		internal async Task RegisterSendAttempts(string sessionId, Queue<EmailProvider> remainingBudget, IReadOnlyCollection<AttemptDetailsSendOTP> sentAttempts)
		{
			var update = Builders<EmailSwitchSession>.Update.Set(session => session.EmailProvidersQueue, remainingBudget);

			if (sentAttempts.Count > 0)
			{
				update = Builders<EmailSwitchSession>.Update.Combine(
					update,
					Builders<EmailSwitchSession>.Update.PushEach(session => session.SentAttempts, sentAttempts));
			}

			if (remainingBudget.Count == 0)
			{
				// Nothing can send this again, so the cleartext code has no further use. Inferred from
				// the budget rather than asked for by the caller, so it cannot be forgotten at a call
				// site. The session stays verifiable - that goes through the token, not the body.
				update = Builders<EmailSwitchSession>.Update.Combine(update, RetireRenderedEmail);
			}

			await _emailSwitchSessionCollection.UpdateOneAsync(Filter(sessionId), update);
		}

		/// <summary>
		/// Drops the rendered email, and with it the cleartext verification code and the recipient's
		/// verified contact details. See <see cref="EmailSwitchSession.SendOTPEmail"/> for why it must
		/// not outlive its usefulness.
		/// </summary>
		private static UpdateDefinition<EmailSwitchSession> RetireRenderedEmail =>
			Builders<EmailSwitchSession>.Update.Unset(session => session.SendOTPEmail);

		/// <summary>
		/// Claims one verification attempt against this session, or returns null if there is no slot
		/// left - or no live session to claim against.
		///
		/// This is what actually enforces <c>MaximumFailedAttemptsToVerify</c>. Reading the session,
		/// testing <see cref="EmailSwitchSession.HasNotExpired"/> and counting the failure afterwards
		/// is check-then-act: guesses issued in parallel all passed the test before any of them had
		/// been recorded, so the cap held for sequential guesses and not at all for concurrent ones.
		/// Since MongoDbTokenManager 10.2.0 dropped its own attempt limit, that was the only guard on
		/// a six digit code.
		///
		/// The increment therefore happens <em>before</em> the guess is checked, in the same
		/// findAndModify that tests the limit, so the server serialises the claims.
		///
		/// The liveness conditions are repeated here rather than trusted from the caller's earlier
		/// read, because that read is exactly the stale value being guarded against.
		/// </summary>
		internal async Task<EmailSwitchSession?> TryReserveVerificationAttempt(string sessionId, byte maximumFailedAttemptsToVerify)
		{
			if (maximumFailedAttemptsToVerify == 0)
			{
				// No attempt is permitted at all, and the index expression below has no meaningful
				// form for it.
				return null;
			}

			var withinTheCap = Builders<EmailSwitchSession>.Filter.And(
				Builders<EmailSwitchSession>.Filter.Lt(session => session.VerificationAttemptsCount, maximumFailedAttemptsToVerify),
				// The pre-counter equivalent: if the array has an element at index cap-1 it already
				// holds cap of them. Expressed as an existence test so a session written before
				// VerificationAttemptsCount existed is still capped by its audit list.
				Builders<EmailSwitchSession>.Filter.Exists(
					$"{nameof(EmailSwitchSession.FailedVerificationAttemptsUTC)}.{maximumFailedAttemptsToVerify - 1}",
					false));

			return await _emailSwitchSessionCollection.FindOneAndUpdateAsync(
				Builders<EmailSwitchSession>.Filter.And(
					Filter(sessionId),
					Builders<EmailSwitchSession>.Filter.Eq(session => session.SuccessfullyVerifiedTimestampUTC, null),
					Builders<EmailSwitchSession>.Filter.Gt(session => session.ExpiryTimeUTC, DateTime.UtcNow),
					withinTheCap),
				Builders<EmailSwitchSession>.Update.Inc(session => session.VerificationAttemptsCount, 1),
				new FindOneAndUpdateOptions<EmailSwitchSession> { ReturnDocument = ReturnDocument.After });
		}

		/// <summary>
		/// Records a wrong guess and returns how many this session has now had, or null if the
		/// session is gone.
		///
		/// The audit record of guesses that were genuinely wrong. The cap itself is claimed earlier by
		/// <see cref="TryReserveVerificationAttempt"/>, so this no longer gates anything - a correct
		/// guess must not land here, or the trail reports a failure that never happened.
		///
		/// Still a single atomic $push rather than a read-modify-replace of the session. That path lost
		/// concurrent failures - two guesses racing both read the same list, both appended one, and the
		/// later write won - so parallel guesses barely advanced the count.
		/// </summary>
		internal async Task<int?> RegisterFailedVerificationAttempt(string sessionId)
		{
			var updated = await _emailSwitchSessionCollection.FindOneAndUpdateAsync(
				Filter(sessionId),
				Builders<EmailSwitchSession>.Update.Push(session => session.FailedVerificationAttemptsUTC, DateTime.UtcNow),
				new FindOneAndUpdateOptions<EmailSwitchSession> { ReturnDocument = ReturnDocument.After });

			return updated?.FailedVerificationAttemptsUTC.Count;
		}

		/// <summary>
		/// Stamps the session verified, only if it has not been already, and retires the rendered
		/// email in the same operation - a spent code has no reason to stay readable for the retention
		/// period. ConsumeAndValidate already guarantees a single winner for the token itself; the
		/// condition here keeps the stored timestamp honest if that ever changes.
		/// </summary>
		internal async Task RegisterSuccessfulVerification(string sessionId)
		{
			await _emailSwitchSessionCollection.UpdateOneAsync(
				Builders<EmailSwitchSession>.Filter.And(
					Filter(sessionId),
					Builders<EmailSwitchSession>.Filter.Eq(session => session.SuccessfullyVerifiedTimestampUTC, null)),
				Builders<EmailSwitchSession>.Update.Combine(
					Builders<EmailSwitchSession>.Update.Set(session => session.SuccessfullyVerifiedTimestampUTC, DateTime.UtcNow),
					RetireRenderedEmail,
					// Released here rather than left for the next create to reclaim lazily: this
					// session is finished, and the next send for this address deserves a new one.
					Builders<EmailSwitchSession>.Update.Unset(session => session.LiveClaimKey)));
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
