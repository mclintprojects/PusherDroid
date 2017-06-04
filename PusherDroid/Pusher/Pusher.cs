using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Android.OS;
using Newtonsoft.Json;
using PusherDroid.Util;
using Android.Util;

namespace PusherDroid
{
	/* TODO: Write tests
     * - Websocket disconnect
        - Connection lost, not cleanly closed
        - MustConnectOverSSL = 4000,
        - App does not exist
        - App disabled
        - Over connection limit
        - Path not found
        - Client over rate limie
        - Conditions for client event triggering
     */
	// TODO: Implement connection fallback strategy

	/// <summary>
	/// The Pusher Client object
	/// </summary>
	public class Pusher : EventEmitter, IPusher, ITriggerChannels
	{
		/// <summary>
		/// Fires when a connection has been established with the Pusher Server
		/// </summary>
		public event ConnectedEventHandler Connected;

		/// <summary>
		/// Fires when the connection is disconnection from the Pusher Server
		/// </summary>
		public event ConnectedEventHandler Disconnected;

		/// <summary>
		/// Fires when the connection state changes
		/// </summary>
		public event ConnectionStateChangedEventHandler ConnectionStateChanged;

		/// <summary>
		/// Fire when an error occurs
		/// </summary>
		public event ErrorEventHandler Error;

		private readonly string _applicationKey;
		private readonly PusherOptions _options;

		private Connection _connection;

		private readonly object _lockingObject = new object();

		private readonly List<string> _pendingChannelSubscriptions = new List<string>();

		/// <summary>
		/// Gets the Socket ID
		/// </summary>
		public string SocketID => _connection?.SocketId;

		/// <summary>
		/// Gets the current connection state
		/// </summary>
		public ConnectionState State => _connection?.State ?? ConnectionState.Disconnected;

		/// <summary>
		/// Gets the channels in use by the Client
		/// </summary>
		public Dictionary<string, Channel> Channels { get; private set; } = new Dictionary<string, Channel>();

		/// <summary>
		/// Gets the Options in use by the Client
		/// </summary>
		internal PusherOptions Options => _options;

		public static Handler Handler { get; private set; }

		/// <summary>
		/// Initializes a new instance of the <see cref="Pusher" /> class.
		/// </summary>
		/// <param name="applicationKey">The application key.</param>
		/// <param name="options">The options.</param>
		/// <param name="handler">Handler to post events on the UI threads.</param>
		public Pusher(string applicationKey, Handler handler, PusherOptions options = null)
		{
			if (string.IsNullOrWhiteSpace(applicationKey))
				throw new ArgumentException(ErrorConstants.ApplicationKeyNotSet, nameof(applicationKey));

			Handler = handler;
			_applicationKey = applicationKey;
			_options = null;

			_options = options ?? new PusherOptions { Encrypted = false };
		}

		public static T ParseMessageToObject<T>(string json, T objectLikeThis)
		{
			if (String.IsNullOrWhiteSpace(json))
				throw new ArgumentNullException(nameof(json));

			var actualJson = json.Remove(0, 1).Remove(json.Length - 2, 1);
			return JsonConvert.DeserializeAnonymousType(actualJson, objectLikeThis);
		}

		void IPusher.ConnectionStateChanged(ConnectionState state)
		{
			if (state == ConnectionState.Connected)
			{
				SubscribeExistingChannels();
				Handler?.Post(() => Connected?.Invoke(this));
			}
			else if (state == ConnectionState.Disconnected)
			{
				MarkChannelsAsUnsubscribed();

				Disconnected?.Invoke(this);
				Handler?.Post(() => ConnectionStateChanged?.Invoke(this, state));
			}
			else
			{
				Handler?.Post(() => ConnectionStateChanged?.Invoke(this, state));
			}
		}

		void IPusher.ErrorOccured(PusherException pusherException)
		{
			RaiseError(pusherException);
		}

		void IPusher.EmitPusherEvent(string eventName, string data)
		{
			EmitEvent(eventName, data);
		}

		void IPusher.EmitChannelEvent(string channelName, string eventName, string data)
		{
			if (Channels.ContainsKey(channelName))
			{
				Channels[channelName].EmitEvent(eventName, data);
			}
		}

		void IPusher.AddMember(string channelName, string member)
		{
			if (Channels.Keys.Contains(channelName) && Channels[channelName] is PresenceChannel)
			{
				((PresenceChannel)Channels[channelName]).AddMember(member);
			}
		}

		void IPusher.RemoveMember(string channelName, string member)
		{
			if (Channels.Keys.Contains(channelName) && Channels[channelName] is PresenceChannel)
			{
				((PresenceChannel)Channels[channelName]).RemoveMember(member);
			}
		}

		void IPusher.SubscriptionSuceeded(string channelName, string data)
		{
			if (_pendingChannelSubscriptions.Contains(channelName))
				_pendingChannelSubscriptions.Remove(channelName);

			if (Channels.Keys.Contains(channelName))
			{
				Channels[channelName].SubscriptionSucceeded(data);
			}
		}

		/// <summary>
		/// Start the connection to the Pusher Server.  When completed, the <see cref="Connected"/> event will fire.
		/// <param name="timeout">Time in milliseconds that if connection isn't established should timeout.</param>
		/// <param name="timeoutAction">The action that should happen when the connection times out.</param>
		/// </summary>
		public Task ConnectAsync(long connectionTimeout = -1, ConnectionTimeoutAction timeoutAction = ConnectionTimeoutAction.Ignore, long networkTimeout = -1, NetworkUnavailableAction networkUnavailableAction = NetworkUnavailableAction.Ignore)
		{
			// Prevent multiple concurrent connections
			lock (_lockingObject)
			{
				return Task.Run(() =>
				{
					// Ensure we only ever attempt to connect once
					if (_connection != null)
					{
						Log.Warn(Constants.LOG_NAME, ErrorConstants.ConnectionAlreadyConnected);
						return;
					}

					var scheme = _options.Encrypted ? Constants.SECURE_SCHEMA : Constants.INSECURE_SCHEMA;

					// TODO: Fallback to secure?

					var url = $"{scheme}{_options.Host}/app/{_applicationKey}?protocol={Settings.ProtocolVersion}&client={Settings.ClientName}&version={Settings.VersionNumber}";

					_connection = new Connection(this, url);
					_connection.Connect(connectionTimeout, timeoutAction, networkTimeout, networkUnavailableAction);
				});
			}
		}

		/// <summary>
		/// Start the disconnection from the Pusher Server.  When completed, the <see cref="Disconnected"/> event will fire.
		/// </summary>
		public Task DisconnectAsync()
		{
			return Task.Run(() =>
			{
				if (_connection != null)
				{
					MarkChannelsAsUnsubscribed();
					_connection.Disconnect();
					_connection = null;
				}

			});
		}

		/// <summary>
		/// Subscribes to the given channel, unless the channel already exists, in which case the xisting channel will be returned.
		/// </summary>
		/// <param name="channelName">The name of the Channel to subsribe to</param>
		/// <returns>The Channel that is being subscribed to</returns>
		public Channel Subscribe(string channelName)
		{
			if (string.IsNullOrWhiteSpace(channelName))
			{
				throw new ArgumentException("The channel name cannot be null or whitespace", nameof(channelName));
			}

			if (AlreadySubscribed(channelName))
			{
				Log.Warn(Constants.LOG_NAME, $"Channel {channelName} is already subscribed to. Subscription event has been ignored.");
				return Channels[channelName];
			}

			_pendingChannelSubscriptions.Add(channelName);

			return SubscribeToChannel(channelName);
		}

		private Channel SubscribeToChannel(string channelName)
		{
			var channelType = GetChannelType(channelName);

			if (!Channels.ContainsKey(channelName))
				CreateChannel(channelType, channelName);

			if (State == ConnectionState.Connected)
			{
				if (channelType == ChannelTypes.Presence || channelType == ChannelTypes.Private)
				{
					var jsonAuth = _options.Authorizer.Authorize(channelName, _connection.SocketId);

					var template = new { auth = string.Empty, channel_data = string.Empty };
					var message = JsonConvert.DeserializeAnonymousType(jsonAuth, template);

					_connection.Send(JsonConvert.SerializeObject(new { @event = Constants.CHANNEL_SUBSCRIBE, data = new { channel = channelName, auth = message.auth, channel_data = message.channel_data } }));
				}
				else
				{
					// No need for auth details. Just send subscribe event
					_connection.Send(JsonConvert.SerializeObject(new { @event = Constants.CHANNEL_SUBSCRIBE, data = new { channel = channelName } }));
				}
			}

			return Channels[channelName];
		}

		private static ChannelTypes GetChannelType(string channelName)
		{
			// If private or presence channel, check that auth endpoint has been set
			var channelType = ChannelTypes.Public;

			if (channelName.ToLowerInvariant().StartsWith(Constants.PRIVATE_CHANNEL))
			{
				channelType = ChannelTypes.Private;
			}
			else if (channelName.ToLowerInvariant().StartsWith(Constants.PRESENCE_CHANNEL))
			{
				channelType = ChannelTypes.Presence;
			}
			return channelType;
		}

		private void CreateChannel(ChannelTypes type, string channelName)
		{
			switch (type)
			{
				case ChannelTypes.Public:
					Channels.Add(channelName, new Channel(channelName, this));
					break;
				case ChannelTypes.Private:
					AuthEndpointCheck();
					Channels.Add(channelName, new PrivateChannel(channelName, this));
					break;
				case ChannelTypes.Presence:
					AuthEndpointCheck();
					Channels.Add(channelName, new PresenceChannel(channelName, this));
					break;
			}
		}

		private void AuthEndpointCheck()
		{
			if (_options.Authorizer == null)
			{
				var pusherException = new PusherException("You must set a ChannelAuthorizer property to use private or presence channels", ErrorCodes.ChannelAuthorizerNotSet);
				RaiseError(pusherException);
				throw pusherException;
			}
		}

		void ITriggerChannels.Trigger(string channelName, string eventName, object obj)
		{
			_connection.Send(JsonConvert.SerializeObject(new { @event = eventName, channel = channelName, data = obj }));
		}

		void ITriggerChannels.Unsubscribe(string channelName)
		{
			if (_connection.IsConnected)

				_connection.Send(JsonConvert.SerializeObject(new { @event = Constants.CHANNEL_UNSUBSCRIBE, data = new { channel = channelName } }));
		}

		private void RaiseError(PusherException error)
		{
			Error?.Invoke(this, error);
		}

		private bool AlreadySubscribed(string channelName)
		{
			return _pendingChannelSubscriptions.Contains(channelName) || (Channels.ContainsKey(channelName) && Channels[channelName].IsSubscribed);
		}

		private void MarkChannelsAsUnsubscribed()
		{
			foreach (var channel in Channels)
			{
				channel.Value.Unsubscribe();
			}
		}

		private void SubscribeExistingChannels()
		{
			foreach (var channel in Channels)
			{
				SubscribeToChannel(channel.Key);
			}
		}
	}
}