using System;
using System.Diagnostics;
using System.Net.Http;
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
        private bool _allowReconnect = true, reconnectAlreadyInProgress;

        private int _backOffMillis;

        private static readonly int MAX_BACKOFF_MILLIS = 10000;
        private static readonly int BACK_OFF_MILLIS_INCREMENT = 1000;

        internal string SocketId => _socketId;

        internal ConnectionState State => _state;

        internal bool IsConnected => State == ConnectionState.Connected;

        private System.Timers.Timer timeoutTimer, networkTimer
;

        private long connectionTimeout, networkTimeout;
        private ConnectionTimeoutAction connectionTimeoutAction;
        private NetworkUnavailableAction networkUnavailableAction;

        private HttpClient client = new HttpClient();

        public Connection(IPusher pusher, string url)
        {
            _pusher = pusher;
            _url = url;
        }

        internal void Connect(long connectionTimeout, ConnectionTimeoutAction timeoutAction, long networkTimeout, NetworkUnavailableAction networkUnavailableAction)
        {
            try
            {
                // TODO: Add 'connecting_in' event
                Log.Info(Constants.LOG_NAME, $"Connecting to: {_url}");

                ChangeState(ConnectionState.Initialized);
                _allowReconnect = true;
                this.connectionTimeout = connectionTimeout;
                this.connectionTimeoutAction = timeoutAction;
                this.networkTimeout = networkTimeout;
                this.networkUnavailableAction = networkUnavailableAction;
                timeoutTimer = new System.Timers.Timer(connectionTimeout);
                networkTimer = new System.Timers.Timer(networkTimeout);

                _websocket = new WebSocket(_url)
                {
                    EnableAutoSendPing = true,
                    AutoSendPingInterval = 8
                };
                _websocket.Opened += websocket_Opened;
                _websocket.Error += websocket_Error;
                _websocket.Closed += websocket_Closed;
                _websocket.MessageReceived += websocket_MessageReceived;

                ChangeState(ConnectionState.Connecting);

                if (connectionTimeout != -1) // If the user provided a timeout value
                    StartTimeoutCountdown();

                // Websocket does not notice when the internet dies, this is a temp workaround until there's a fix
                if (networkTimeout != -1)
                    StartCheckForInternetConnection();

                _websocket.Open();

                reconnectAlreadyInProgress = false;
            }
            catch (Exception e)
            {
                Log.Error(Constants.LOG_NAME, $"{e.Message}");
            }
        }

        private void StartCheckForInternetConnection()
        {
            Task.Run(() =>
            {
                networkTimer.AutoReset = true;
                networkTimer.Elapsed += NetworkTimer_Elapsed;
                networkTimer.Enabled = true;
            });
        }

        private async void NetworkTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (!await IsInternetAvailable())
            {
                ChangeState(ConnectionState.NetworkUnavailable);
                switch (networkUnavailableAction)
                {
                    case NetworkUnavailableAction.CloseConnection:
                        networkTimer.Elapsed -= NetworkTimer_Elapsed;
                        Disconnect();
                        networkTimer.Enabled = false;
                        break;

                    case NetworkUnavailableAction.StopCheckingForAvailability:
                        networkTimer.Enabled = false;
                        break;
                }
            }
            else
            {
                // Sometimes the timer fires when reconnection is already in progress which causes it to fail due to too many connection attempts.
                if (!reconnectAlreadyInProgress && !IsConnected)
                {
                    reconnectAlreadyInProgress = true;
                    networkTimer.Elapsed -= NetworkTimer_Elapsed;
                    Disconnect();
                    ChangeState(ConnectionState.NetworkAvailable);
                    Connect(connectionTimeout, connectionTimeoutAction, networkTimeout, networkUnavailableAction);
                }
            }
        }

        private void StartTimeoutCountdown()
        {
            Task.Run(() =>
            {
                timeoutTimer.AutoReset = false;
                timeoutTimer.Elapsed += TimeoutTimer_Elapsed;
                timeoutTimer.Enabled = true;
            });
        }

        private void TimeoutTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            // If the connection attempt still hasn't connected after the specified time
            if (!IsConnected)
            {
                ChangeState(ConnectionState.TimedOut);
                timeoutTimer.Enabled = false;
                if (connectionTimeoutAction == ConnectionTimeoutAction.CloseConnection)
                    Disconnect();
            }
        }

        private async Task<bool> IsInternetAvailable()
        {
            try
            {
                var response = await client.GetAsync(@"http://www.google.com");
                return response.IsSuccessStatusCode;
            }
            catch (Exception e)
            {
                Log.Warn(Constants.LOG_NAME, $"Something went wrong. {e.Message}.");
                return false;
            }
        }

        internal void Disconnect()
        {
            // Prevent disconnection when its already disconnection which leads to a null reference exception since _websocket is set to null
            if (_websocket != null)
            {
                ChangeState(ConnectionState.Disconnecting);

                _allowReconnect = false;

                _websocket.Opened -= websocket_Opened;
                _websocket.Error -= websocket_Error;
                _websocket.Closed -= websocket_Closed;
                _websocket.MessageReceived -= websocket_MessageReceived;
                timeoutTimer.Elapsed -= TimeoutTimer_Elapsed;

                _websocket.Dispose();
                _websocket = null;

                ChangeState(ConnectionState.Disconnected);
            }
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
                Connect(connectionTimeout, connectionTimeoutAction, networkTimeout, networkUnavailableAction);
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