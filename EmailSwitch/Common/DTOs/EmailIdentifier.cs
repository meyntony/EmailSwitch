using System.Net.Mail;

namespace EmailSwitch.Common.DTOs
{
	/// <summary>
	/// A struct, so <c>default(EmailIdentifier)</c> exists whether or not it is ever meant to - an
	/// uninitialised array element or field reaches the accessors below with every field null. They
	/// are written to survive that rather than throwing NullReferenceException from somewhere
	/// unhelpful, which is what happens when the fields are treated as if a constructor had always
	/// run.
	/// </summary>
	public struct EmailIdentifier
	{
		private readonly string? idValue;
		private readonly string? rawValue;
		private readonly MailAddress? mailAddress;

		public EmailIdentifier(string email)
		{

			if (string.IsNullOrWhiteSpace(email)) { throw new Exception($"Invalid Email>>{email}<<"); }
			mailAddress = new MailAddress(email.ToLowerInvariant());
			rawValue = email;
			mailAddress = new MailAddress($"{mailAddress.User.Split('+').First()}@{mailAddress.Host}");
			idValue = mailAddress.Host == "gmail.com" ? $"{string.Join("", mailAddress.User.Split('.'))}@{mailAddress.Host}" : mailAddress.ToString();
		}
		public EmailIdentifier(string email, string emailId) : this(email)
		{
			if (emailId.ToLowerInvariant() != idValue) { throw new Exception($"Email ID mismatch>>{emailId} != {idValue}<<"); }
		}

		public override bool Equals(object? obj)
		{
			if (obj is EmailIdentifier tokenIdentifier)
			{
				return Equals(tokenIdentifier);
			}

			return false;
		}
		public override int GetHashCode()
		{
			// A default instance hashes to zero rather than throwing. Equals already treats an empty
			// id as equal to nothing, including another default, so this only has to be consistent.
			return idValue?.GetHashCode() ?? 0;
		}
		public bool Equals(EmailIdentifier other)
		{
			return !string.IsNullOrEmpty(idValue) && idValue == other.idValue;
		}

		/// <summary>
		/// The normalised address this is keyed by. Empty for a default instance - never null, which
		/// matters because sessions are stored under this and the field is not nullable.
		/// </summary>
		public override string ToString()
		{
			return idValue ?? string.Empty;
		}

		/// <summary>The address exactly as supplied, which is what actually gets emailed.</summary>
		public string GetRawValue()
		{
			return rawValue ?? string.Empty;
		}

		/// <summary>
		/// Throws on a default instance rather than handing back a null through a non-nullable return
		/// type, so the failure names the cause instead of surfacing as a NullReferenceException in
		/// whatever tried to read the host.
		/// </summary>
		public MailAddress GetMailAddress()
		{
			return mailAddress ?? throw new InvalidOperationException($"This {nameof(EmailIdentifier)} was never initialised with an address.");
		}

		public static implicit operator EmailIdentifier(string value)
		{
			return new EmailIdentifier(value);
		}

		public static explicit operator string(EmailIdentifier tokenIdentifier)
		{
			return tokenIdentifier.ToString();
		}


		public static bool operator ==(EmailIdentifier left, EmailIdentifier right)
		{
			return left.Equals(right);
		}

		public static bool operator !=(EmailIdentifier left, EmailIdentifier right)
		{
			return !left.Equals(right);
		}
	}
}
