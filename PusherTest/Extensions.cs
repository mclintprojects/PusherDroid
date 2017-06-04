using System;
using Android.Widget;

namespace PusherTest
{
	public static class Extensions
	{
		public static void AppendLine(this EditText @this, string text)
		{
			@this.Append($"{text}\r\n");
		}
	}
}
