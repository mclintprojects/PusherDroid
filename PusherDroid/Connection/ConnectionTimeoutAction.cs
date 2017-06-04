using System;
namespace PusherDroid
{
	public enum ConnectionTimeoutAction
	{
		/// <summary>
		/// Ignore the connection timeout.
		/// </summary>
		Ignore,

		/// <summary>
		/// Close the connection if the connection times out.
		/// </summary>
		CloseConnection
	}
}
