using ConsoleGUI.Data;
using ConsoleGUI.Space;
using System;
using System.Collections.Generic;
using System.Text;

namespace ConsoleGUI.Api
{
	public interface IConsole
	{
		Size Size { get; set; }
		bool KeyAvailable { get; }

		void Initialize();
		void OnRefresh();
		void Write(Position position, in Character character);
		ConsoleKeyInfo ReadKey();

		/// <summary>
		/// Called when the app adopts a size the terminal reported, so the console can bring its own state in line —
		/// without the caller commanding the window. Default: nothing.
		/// </summary>
		/// <remarks>
		/// Distinct from the <see cref="Size"/> setter, which drives the window. Setting the window in response to a
		/// window resize is the feedback loop that never converges; matching the scroll BUFFER to it is not, and a
		/// full-screen UI wants them equal so the host draws no scrollbar over the app.
		/// </remarks>
		void AdoptSize(in Size size) { }
	}
}
