# PusherDroid
An fork of Pusher .NET that works in Xamarin Android. Forked from https://www.github.com/imaji's repository https://github.com/imaji/pusher-websocket-dotnet.

This is a .NET Xamarin Android library for interacting with the Pusher WebSocket API.

Register at http://pusher.com and use the application credentials within your app as shown below.

More general documentation can be found at http://pusher.com/docs/.

## Installation

Clone or download the github project, build the solution and add a reference of the generated PusherDroid.dll to your Xamarin Android project.

## Connect

```cs
_pusher = new Pusher("YOUR_APP_KEY", new Handler(Looper.MainLooper));
_pusher.ConnectionStateChanged += Pusher_ConnectionStateChanged;
_pusher.Error += Pusher_Error;

// This is the default method which automatically sets the defaults for the its parameters -> (-1, TimeoutAction.Ignore)
_pusher.ConnectAsync(); 

or

Use this instead if you want to set a timeout.
/// <summary>
/// <param name="timeout">Time in milliseconds that if connection isn't established should timeout.</param>
/// <param name="timeoutAction">The action that should happen when the connection times out. Either `TimeoutAction.Ignore` or `TimeoutAction.CloseConnection`</param>
/// </summary>
_pusher.ConnectAsync(3000, TimeoutAction.CloseConnection); 
```

where `_pusher_ConnectionStateChanged` and `_pusher_Error` are custom event handlers such as

```cs
static void Pusher_ConnectionStateChanged(object sender, ConnectionState state)
{
    Console.WriteLine("Connection state: " + state.ToString());
}

static void Pusher_Error(object sender, PusherException error)
{
    Console.WriteLine("Pusher Error: " + error.ToString());
}
```
and `Handler(Looper.MainLooper)` allows events to be posted on the UI thread automatically to prevent you from littering your code with multiple `RunOnUiThread(() => {});` statements.

Or if you have an authentication endpoint for private or presence channels:

```cs
_pusher = new Pusher("YOUR_APP_KEY", new Handler(Looper.MainLooper) new PusherOptions(){
    Authorizer = new HttpAuthorizer("YOUR_ENDPOINT")
});
_pusher.ConnectionStateChanged += _pusher_ConnectionStateChanged;
_pusher.Error += _pusher_Error;
_pusher.Connect();
```

Or if you are on a non default cluster (e.g. eu):

```cs
_pusher = new Pusher("YOUR_APP_KEY", new PusherOptions(){
    Cluster = "eu"
});
_pusher.ConnectionStateChanged += _pusher_ConnectionStateChanged;
_pusher.Error += _pusher_Error;
_pusher.Connect();
```

### Subscribe to a public or private channel

```cs
_myChannel = _pusher.Subscribe("my-channel");
_myChannel.Subscribed += _myChannel_Subscribed;
```
where `_myChannel_Subscribed` is a custom event handler such as

```cs
static void _myChannel_Subscribed(object sender)
{
    Console.WriteLine("Subscribed!");
}
```

### Bind to an event

```cs
_myChannel.Bind("my-event", (data) =>
{
    Console.WriteLine(data.message);
});
```

### Subscribe to a presence channel

```cs
_presenceChannel = (PresenceChannel)_pusher.Subscribe("presence-channel");
_presenceChannel.Subscribed += _presenceChannel_Subscribed;
_presenceChannel.MemberAdded += _presenceChannel_MemberAdded;
_presenceChannel.MemberRemoved += _presenceChannel_MemberRemoved;
```

Where `_presenceChannel_Subscribed`, `_presenceChannel_MemberAdded`, and `_presenceChannel_MemberRemoved` are custom event handlers such as

```cs
static void _presenceChannel_MemberAdded(object sender, KeyValuePair<string, dynamic> member)
{
    Console.WriteLine((string)member.Value.name.Value + " has joined");
    ListMembers();
}

static void _presenceChannel_MemberRemoved(object sender)
{
    ListMembers();
}
```

### Unbind

Remove a specific callback:

```cs
_myChannel.Unbind("my-event", callback);
```

Remove all callbacks for a specific event:

```cs
_myChannel.Unbind("my-event");
```

Remove all bindings on the channel:

```cs
_myChannel.UnbindAll();
```

