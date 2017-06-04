using Android.App;
using Android.Widget;
using Android.OS;
using PusherDroid;
using System;
using Newtonsoft.Json;

namespace PusherTest
{
	[Activity(Label = "PusherTest", MainLauncher = true, Icon = "@mipmap/icon")]
	public class MainActivity : Activity
	{
		Pusher pusher;
		protected override void OnCreate(Bundle savedInstanceState)
		{
			base.OnCreate(savedInstanceState);
			SetContentView(Resource.Layout.Main);
			Toast.MakeText(this, $"Layout inflated.", ToastLength.Long).Show();
			var infoTb = FindViewById<EditText>(Resource.Id.infoTb);

			pusher = new Pusher("dc777df44df8be24ae85", new Handler(Looper.MainLooper));
			pusher.ConnectAsync();
			pusher.Connected += (sender) =>
			{
				RunOnUiThread(() =>
				{
					infoTb.AppendLine("Connected!");
					var channel = pusher.Subscribe("my-channel");
					channel.Bind("my-event", (obj) =>
					{
						var car = JsonConvert.DeserializeAnonymousType(obj.message, new Car());
						infoTb.AppendLine($"{car.name}, {car.color}, {car.make}");
					});
				});
			};
			pusher.ConnectionStateChanged += (s, state) =>
			{
				infoTb.AppendLine($"Connection changed: {state.ToString("G")}");
			};
		}

		protected override void OnDestroy()
		{
			base.OnDestroy();
			pusher.UnbindAll();
			pusher.Disconnect();
		}
	}
}

