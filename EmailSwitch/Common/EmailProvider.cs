namespace EmailSwitch.Common
{
	/// <summary>
	/// These values are persisted by number inside <c>EmailSwitchSession.EmailProvidersQueue</c>, so
	/// they are part of the stored data. Append new providers at the end; renumbering or inserting a
	/// member silently reinterprets every session already in the database.
	/// </summary>
	public enum EmailProvider
	{
		SendGrid = 0,
		DevConsole = 1
	}
}
