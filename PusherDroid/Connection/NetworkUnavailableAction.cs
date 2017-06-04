using System;
namespace PusherDroid
{
	public enum NetworkUnavailableAction
	{
		/// <summary>
		/// Leave connection open and continue checking for network availability
		/// </summary>
		Ignore,

		/// <summary>
		/// Stop checking for network availability but leave connection open
		/// </summary>
		StopCheckingForAvailability,

		/// <summary>
		/// Disconnect the connection and stop checking for network availability
		/// </summary>
		CloseConnection
	}
}
