namespace EmailSwitch.Common
{
	/// <summary>
	/// Shared by the providers reached over HTTP. SendGrid keeps its own copy because its SDK hands
	/// back a different response type.
	/// </summary>
	internal static class HttpResponseLogging
	{
		/// <summary>
		/// Diagnostics only, so a body that cannot be read must not turn a failed send into a thrown
		/// one - the caller is already on its unhappy path.
		/// </summary>
		internal static async Task<string> ReadBodyForLogging(HttpResponseMessage response)
		{
			try
			{
				return await response.Content.ReadAsStringAsync();
			}
			catch (Exception)
			{
				return "<could not be read>";
			}
		}
	}
}
