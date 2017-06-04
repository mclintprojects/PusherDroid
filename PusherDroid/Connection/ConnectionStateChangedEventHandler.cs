namespace PusherDroid
{
    /// <summary>
    /// The Event Handler for the Connection State Channged Event on the <see cref="Pusher"/>
    /// </summary>
    /// <param name="sender">The object that subscribed</param>
    /// <param name="state">The new state</param>
    public delegate void ConnectionStateChangedEventHandler(object sender, ConnectionState state);
}