using ConsoleGUI.Data;
using ConsoleGUI.Space;
using ConsoleGUI.Utils;
using System;
using System.Collections.Generic;
using System.Text;

namespace ConsoleGUI.Api
{
	public class StandardConsole : IConsole
	{
		public Size Size
		{
			get => new Size(Console.WindowWidth, Console.WindowHeight);
			set
			{
				SafeConsole.SetCursorPosition(0, 0);
				SafeConsole.SetWindowPosition(0, 0);
				// Windows refuses a buffer smaller than the window, so the window has to shrink first — but only to
				// the target size, never to 1x1. SafeConsole swallows failures, so if either call below failed the
				// old 1x1 step left the console window collapsed to a single cell: the app looks like it vanished
				// while the process is still running.
				if (!(Size <= value))
				{
					var current = Size;
					SafeConsole.SetWindowSize(Math.Min(current.Width, value.Width), Math.Min(current.Height, value.Height));
				}

				SafeConsole.SetBufferSize(value.Width, value.Height);
				if (Size != value) SafeConsole.SetWindowSize(value.Width, value.Height);
				Initialize();
			}
		}

		// Match the scroll buffer to the window the terminal has already settled on, so the host shows no scrollbar
		// over a full-screen UI. Only the buffer — never SetWindowSize, which is what fights the user's window.
		public void AdoptSize(in Size size) => SafeConsole.SetBufferSize(size.Width, size.Height);

		public bool KeyAvailable => Console.KeyAvailable;

		public virtual void Initialize()
		{
			SafeConsole.SetUtf8();
			SafeConsole.HideCursor();
			SafeConsole.Clear();
		}

		public virtual void OnRefresh()
		{
			
		}

		public virtual void Write(Position position, in Character character)
		{
			var content = character.Content ?? ' ';
			var foreground = character.Foreground ?? Color.White;
			var background = character.Background ?? Color.Black;

			if (content == '\n') content = ' ';

			SafeConsole.WriteOrThrow(position.X, position.Y, $"\x1b[38;2;{foreground.Red};{foreground.Green};{foreground.Blue}m\x1b[48;2;{background.Red};{background.Green};{background.Blue}m{content}");
		}

		public ConsoleKeyInfo ReadKey()
		{
			return Console.ReadKey(true);
		}
	}
}
