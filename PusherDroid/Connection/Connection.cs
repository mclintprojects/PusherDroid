using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Android.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PusherDroid.Util;
using WebSocket4Net;

namespace PusherDroid
{
	internal class Connection
	{
		private WebSocket _websocket;
		private string _socketId;

		private readonly string _url;
		private readonly IPusher _pusher;
		private ConnectionState _state = ConnectionState.Uninitialized;
		private bool _allowReconnect = true;

		private int _backOffMillis;

		private static readonly int MAX_BACKOFF_MILLIS = 10000;
		private static readonly int BACK_OFF_MILLIS_INCREMENT = 1000;

		internal string SocketId => _socketId;

		internal ConnectionState State => _state;

		internal bool IsConnected => State == ConnectionState.Connected;

		private System.Timers.Timer timer;

		private long timeout;
		private TimeoutAction timeoutAction;

		public Connection(IPusher pusher, string url)
		{
			_pusher = pusher;
			_url = url;
		}

		internal void Connect(long timeout = -1, TimeoutAction timeoutAction = TimeoutAction.Ignore)
		{
			// TODO: Add 'connecting_in' event
			Log.Info(Constants.LOG_NAME, $"Connecting to: {_url}");

			ChangeState(ConnectionState.Initialized);
			_allowReconnect = true;
			this.timeout = timeout;
			this.timeoutAction = timeoutAction;
			timer = new System.Timers.Timer(timeout);

			_websocket = new WebSocket(_url)
			{
				EnableAutoSendPing = true,
				AutoSendPingInterval = 1
			};
			_websocket.Opened += websocket_Opened;
			_websocket.Error += websocket_Error;
			_websocket.Closed += websocket_Closed;
			_websocket.MessageReceived += websocket_MessageReceived;

			ChangeState(ConnectionState.Connecting);

			_websocket.Open();

			if (timeout != -1) // If the user provided a timeout value
				StartTimeoutCountdown();
		}

		void StartTimeoutCountdown()
		{
			Task.Run(() =>
			{
				timer.Enabled = true;
				timer.AutoReset = false;
				timer.Elapsed += (sender, e) =>
				{
					// If the connection still hasn't connected aftert he specified time
					if (!IsConnected)
					{
						ChangeState(ConnectionState.TimedOut);
						timer.Enabled = false;
						if (timeoutAction == TimeoutAction.CloseConnection)
							Disconnect();
					}
				};
			});
		}

		internal void Disconnect()
		{
			ChangeState(ConnectionState.Disconnecting);

			_allowReconnect = false;

			_websocket.Opened -= websocket_Opened;
			_websocket.Error -= websocket_Error;
			_websocket.Closed -= websocket_Closed;
			_websocket.MessageReceived -= websocket_MessageReceived;

			_websocket.Close();

			ChangeState(ConnectionState.Disconnected);
		}

		internal void Send(string message)
		{
			if (IsConnected)
			{
				Log.Info(Constants.LOG_NAME, $"Sending: {message}");
				_websocket?.Send(message);
			}
		}

		private void websocket_MessageReceived(object sender, MessageReceivedEventArgs e)
		{
			Log.Info(Constants.LOG_NAME, $"Websocket message received: {e.Message} ");

			// DeserializeAnonymousType will throw an error when an error comes back from pusher
			// It stems from the fact that the data object is a string normally except when an error is sent back
			// then it's an object.

			// bad:  "{\"event\":\"pusher:error\",\"data\":{\"code\":4201,\"message\":\"Pong reply not received\"}}"
			// good: "{\"event\":\"pusher:error\",\"data\":\"{\\\"code\\\":4201,\\\"message\\\":\\\"Pong reply not received\\\"}\"}";

			var jObject = JObject.Parse(e.Message);

			if (jObject["data"] != null && jObject["data"].Type != JTokenType.String)
				jObject["data"] = jObject["data"].ToString(Formatting.None);

			var jsonMessage = jObject.ToString(Formatting.None);
			var template = new { @event = string.Empty, data = string.Empty, channel = string.Empty };

			var message = JsonConvert.DeserializeAnonymousType(jsonMessage, template);

			_pusher.EmitPusherEvent(message.@event, message.data);

			if (message.@event.StartsWith(Constants.PUSHER_MESSAGE_PREFIX))
			{
				// Assume Pusher event
				switch (message.@event)
				{
					// TODO - Need to handle Error on subscribing to a channel

					case Constants.ERROR:
						ParseError(message.data);
						break;

					case Constants.CONNECTION_ESTABLISHED:
						ParseConnectionEstablished(message.data);
						break;

					case Constants.CHANNEL_SUBSCRIPTION_SUCCEEDED:
						_pusher.SubscriptionSuceeded(message.channel, message.data);
						break;

					case Constants.CHANNEL_SUBSCRIPTION_ERROR:
						RaiseError(new PusherException("Error received on channel subscriptions: " + e.Message, ErrorCodes.SubscriptionError));
						break;

					case Constants.CHANNEL_MEMBER_ADDED:
						_pusher.AddMember(message.channel, message.data);

						Log.Info(Constants.LOG_NAME, $"Received a presence event on channel {message.channel}, however there is no presence channel which matches.");
						break;

					case Constants.CHANNEL_MEMBER_REMOVED:
						_pusher.RemoveMember(message.channel, message.data);

						Log.Info(Constants.LOG_NAME, $"Received a presence event on channel {message.channel} however there is no presence channel which matches.");
						break;
				}
			}
			else // Assume channel event
			{
				_pusher.EmitChannelEvent(message.channel, message.@event, message.data);
			}
		}

		private void websocket_Opened(object sender, EventArgs e)
		{
			Log.Info(Constants.LOG_NAME, "Websocket opened OK.");
		}

		private void websocket_Closed(object sender, EventArgs e)
		{
			Log.Info(Constants.LOG_NAME, "Websocket connection has been closed");

			ChangeState(ConnectionState.Disconnected);
			_websocket.Dispose();

			if (_allowReconnect)
			{
				ChangeState(ConnectionState.WaitingToReconnect);
				Thread.Sleep(_backOffMillis);
				_backOffMillis = Math.Min(MAX_BACKOFF_MILLIS, _backOffMillis + BACK_OFF_MILLIS_INCREMENT);
				Connect();
			}
		}

		private void websocket_Error(object sender, SuperSocket.ClientEngine.ErrorEventArgs e)
		{
			Log.Error(Constants.LOG_NAME, $"Error: {e.Exception}");
			ChangeState(ConnectionState.Failed);

			// TODO: What happens here? Do I need to re-connect, or do I just log the issue?
		}

		private void ParseConnectionEstablished(string data)
		{
			var template = new { socket_id = string.Empty };
			var message = JsonConvert.DeserializeAnonymousType(data, template);
			_socketId = message.socket_id;

			ChangeState(ConnectionState.Connected);
		}

		private void ParseError(string data)
		{
			var template = new { message = string.Empty, code = (int?)null };
			var parsed = JsonConvert.DeserializeAnonymousType(data, template);

			ErrorCodes error = ErrorCodes.Unkown;

			if (parsed.code != null && Enum.IsDefined(typeof(ErrorCodes), parsed.code))
			{
				error = (ErrorCodes)parsed.code;
			}

			RaiseError(new PusherException(parsed.message, error));
		}

		private void ChangeState(ConnectionState state)
		{
			_state = state;
			_pusher.ConnectionStateChanged(state);
		}

		private void RaiseError(PusherException error)
		{
			_pusher.ErrorOccured(error);
		}
	}
}