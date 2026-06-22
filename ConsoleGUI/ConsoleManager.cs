using ConsoleGUI.Api;
using ConsoleGUI.Buffering;
using ConsoleGUI.Common;
using ConsoleGUI.Controls;
using ConsoleGUI.Data;
using ConsoleGUI.Input;
using ConsoleGUI.Space;
using ConsoleGUI.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vezel.Cathode.Text.Control;

namespace ConsoleGUI
{
	public static class ConsoleManager
	{
		private class ConsoleManagerDrawingContextListener : IDrawingContextListener
		{
			void IDrawingContextListener.OnRedraw(DrawingContext drawingContext)
			{
				if (_freezeLock.IsFrozen) return;
				Redraw();
			}

			void IDrawingContextListener.OnUpdate(DrawingContext drawingContext, Rect rect)
			{
				if (_freezeLock.IsFrozen) return;
				Update(rect);
			}
		}

		private static readonly ConsoleBuffer _buffer = new ConsoleBuffer();
		private static FreezeLock _freezeLock;

		/// <summary>
		/// When <see langword="true"/> (default), cells are rendered with ANSI escape sequences (truecolor SGR,
		/// cursor positioning, DECSCUSR cursor). When <see langword="false"/>, rendering falls back to the
		/// <see cref="IConsole.Write(Position, in Character)"/> path (e.g. <see cref="Api.SimplifiedConsole"/>'s
		/// 16-colour System.Console output) for legacy terminals that don't interpret ANSI.
		/// </summary>
		public static bool AnsiEnabled = true;

		// Native terminal cursor state, driven by cells flagged with Character.IsCursor during Update.
		private static Position? _cursorPosition;
		private static bool _cursorVisible;
		private static int _cursorStyle = -1;       // last emitted DECSCUSR style; -1 forces first emit
		private static Color? _cursorColor;         // last emitted OSC 12 colour (null = terminal default)

		// Legacy (non-ANSI) software-cursor state: the cursor is drawn as a cell and blinked by us.
		private const long BlinkHalfPeriodMs = 530;
		private static Character _legacyCursorChar;  // source cursor cell (glyph + encoded style)
		private static bool _legacyCursorBlinking;   // the current cursor's style blinks
		private static bool _legacyCursorShown;      // current blink phase as last rendered

		private static DrawingContext _contentContext = DrawingContext.Dummy;
		private static DrawingContext ContentContext
		{
			get => _contentContext;
			set => Setter
				.SetDisposable(ref _contentContext, value)
				.Then(Initialize);
		}

		private static IControl _content;
		public static IControl Content
		{
			get => _content;
			set => Setter
				.Set(ref _content, value)
				.Then(BindContent);
		}

		private static IConsole _console = new StandardConsole();
		public static IConsole Console
		{
			get => _console;
			set => Setter
				.Set(ref _console, value)
				.Then(Initialize);
		}

		private static Position? _mousePosition;
		public static Position? MousePosition
		{
			get => _mousePosition;
			set => Setter
				.Set(ref _mousePosition, value)
				.Then(UpdateMouseContext);
		}

		private static bool _mouseDown;
		public static bool MouseDown
		{
			get => _mouseDown;
			set
			{
				if (_mouseDown && !value)
					MouseContext?.MouseListener?.OnMouseUp(MouseContext.Value.RelativePosition);
				if (!_mouseDown && value)
					MouseContext?.MouseListener?.OnMouseDown(MouseContext.Value.RelativePosition);

				_mouseDown = value;
			}
		}

		private static MouseContext? _mouseContext;
		private static MouseContext? MouseContext
		{
			get => _mouseContext;
			set
			{
				if (value?.MouseListener != _mouseContext?.MouseListener)
				{
					_mouseContext?.MouseListener.OnMouseLeave();
					value?.MouseListener.OnMouseEnter();
					value?.MouseListener.OnMouseMove(value.Value.RelativePosition);
				}
				else if (value.HasValue && value.Value.RelativePosition != _mouseContext?.RelativePosition)
				{
					value.Value.MouseListener.OnMouseMove(value.Value.RelativePosition);
				}

				_mouseContext = value;
			}
		}

		public static Size WindowSize => Console.Size;
		public static Size BufferSize => _buffer.Size;

		private static void Initialize()
		{
			var consoleSize = BufferSize;

			Console.Initialize();
			_buffer.Clear();
			// Console.Initialize() hides the native cursor (and resize routes through here); forget our cached
			// visibility/style so the next draw re-shows and re-applies the cursor if one is present.
			_cursorVisible = false;
			_cursorStyle = -1;

			_freezeLock.Freeze();
			ContentContext.SetLimits(consoleSize, consoleSize);
			_freezeLock.Unfreeze();

			Redraw();
		}

		public static void Redraw()
		{
			Update(ContentContext.Size.AsRect());
		}

		private static void Update(Rect rect)
		{
            Console.OnRefresh();
			rect = Rect.Intersect(rect, Rect.OfSize(BufferSize));
			rect = Rect.Intersect(rect, Rect.OfSize(WindowSize));

			if (!AnsiEnabled) { UpdateLegacy(rect); return; }

			Color? currentFg = null;
			Color? currentBg = null;
			Decoration? currentDecoration = null;

			int lastY = -1;
			int lastX = -1;

            var acsb = new AnsiControlSequenceBuilder();

            // Track the cell flagged as the cursor (Character.IsCursor). Checked before the diff skip so the
            // cursor is found even on frames where its cell is otherwise unchanged.
            Position? cursorAt = null;
            Character cursorChar = default;
            bool wroteAnything = false;

            for (int y = rect.Top; y <= rect.Bottom; y++)
			{
				for (int x = rect.Left; x <= rect.Right; x++)
				{
					var position = new Position(x, y);

					var cell = ContentContext[position];

					if (cell.Character.IsCursor) { cursorAt = position; cursorChar = cell.Character; }

					if (!_buffer.Update(position, cell)) continue;

					if (cell.Character.Content.HasValue)
					{
						if (y != lastY || x != lastX + 1)
						{
							acsb.MoveCursorTo(y, x);
						}
						WriteCharacterAnsiSequence(cell.Character, acsb, ref currentFg, ref currentBg, ref currentDecoration);
						lastY = y;
						lastX = x;
						wroteAnything = true;
					}
                }
            }
			acsb.ResetAttributes();

			// Position the terminal's native cursor. Only a full scan is authoritative about the cursor being
			// gone; a partial update keeps the last known cursor so writes elsewhere don't drop it.
			var bufferRect = Rect.OfSize(BufferSize);
			bool fullUpdate = rect.Left <= bufferRect.Left && rect.Top <= bufferRect.Top
				&& rect.Right >= bufferRect.Right && rect.Bottom >= bufferRect.Bottom;
			Position? newCursorPosition = cursorAt.HasValue ? cursorAt : (fullUpdate ? null : _cursorPosition);

			// Only touch the cursor when something actually changed; otherwise leave it alone so the terminal's
			// native blink isn't reset every frame. A reposition is needed only when the cursor moved or our
			// character writes this frame moved the real terminal cursor; visibility is emitted only on change.

			if (newCursorPosition.HasValue)
			{
				// Style (DECSCUSR) and colour (OSC 12) ride the cursor cell's high decoration bits / Foreground;
				// emit only on change so the native blink phase isn't reset every frame.
				if (cursorAt.HasValue)
				{
					var deco = cursorChar.Decoration ?? Decoration.None;
					int style = CursorEncoding.DecodeStyle(deco);
					if (style != _cursorStyle)
					{
						acsb.SetCursorStyle((CursorStyle)style);
						_cursorStyle = style;
					}

					Color? color = CursorEncoding.HasColor(deco) ? cursorChar.Foreground : null;
					if (color != _cursorColor)
					{
						if (color.HasValue)
							acsb.Print($"\x1b]12;#{color.Value.Red:X2}{color.Value.Green:X2}{color.Value.Blue:X2}\x1b\\");
						else
							acsb.Print("\x1b]112\x1b\\"); // reset cursor colour to terminal default
						_cursorColor = color;
					}
				}

				if (newCursorPosition != _cursorPosition || wroteAnything)
					acsb.MoveCursorTo(newCursorPosition.Value.Y, newCursorPosition.Value.X);
				if (!_cursorVisible)
					acsb.SetCursorVisibility(true);
			}
			else if (_cursorVisible)
			{
				acsb.SetCursorVisibility(false);
			}
			_cursorPosition = newCursorPosition;
			_cursorVisible = newCursorPosition.HasValue;

			Task.Run(acsb.WriteToSystemConsole);
        }

        // Legacy/non-ANSI rendering: write each changed cell through the IConsole (e.g. SimplifiedConsole's
        // 16-colour System.Console output). The cursor is a *software* cursor — a specially-rendered cell — so
        // the hardware cursor (which we can't move without it visibly jumping per cell, or blink consistently)
        // stays hidden. Shape and blink follow the CursorStyle encoded on the cursor Character.
        private static void UpdateLegacy(Rect rect)
        {
            // Keep the hardware cursor hidden; the software cursor is drawn as a cell.
            if (_cursorVisible) { SafeConsole.HideCursor(); _cursorVisible = false; }

            Position? cursorAt = null;
            Character cursorChar = default;

            for (int y = rect.Top; y <= rect.Bottom; y++)
            {
                for (int x = rect.Left; x <= rect.Right; x++)
                {
                    var position = new Position(x, y);
                    var cell = ContentContext[position];

                    if (cell.Character.IsCursor) { cursorAt = position; cursorChar = cell.Character; }

                    if (!_buffer.Update(position, cell)) continue;
                    if (cell.Character.IsCursor) continue;   // drawn as the software cursor below, not as a raw glyph
                    if (cell.Character.Content.HasValue) Console.Write(position, cell.Character);
                }
            }

            // Only a full scan is authoritative about the cursor being gone; a partial update that doesn't
            // include the cursor cell leaves the software cursor untouched.
            var bufferRect = Rect.OfSize(BufferSize);
            bool fullUpdate = rect.Left <= bufferRect.Left && rect.Top <= bufferRect.Top
                && rect.Right >= bufferRect.Right && rect.Bottom >= bufferRect.Bottom;

            if (cursorAt.HasValue)
            {
                int style = CursorEncoding.DecodeStyle(cursorChar.Decoration ?? Decoration.None);
                _cursorPosition = cursorAt;
                _legacyCursorChar = cursorChar;
                _legacyCursorBlinking = style == 0 || (style % 2 == 1);
                RenderLegacyCursorCell(!_legacyCursorBlinking || LegacyBlinkOn());
            }
            else if (fullUpdate)
            {
                // Cursor gone — the cell it was on was redrawn as a normal glyph by the loop above.
                _cursorPosition = null;
                _legacyCursorBlinking = false;
            }
        }

        // ~1Hz blink derived from wall-clock time, so the rate is independent of how often frames are drawn.
        private static bool LegacyBlinkOn() => (Environment.TickCount64 / BlinkHalfPeriodMs) % 2 == 0;

        // Renders the software cursor cell: when "on", tailored to its CursorStyle (block = inverted cell,
        // underline = '_', bar = '|'); when "off" (blink phase), the plain glyph.
        private static void RenderLegacyCursorCell(bool on)
        {
            if (!_cursorPosition.HasValue) return;
            int style = CursorEncoding.DecodeStyle(_legacyCursorChar.Decoration ?? Decoration.None);
            var c = _legacyCursorChar;
            var content = c.Content ?? ' ';
            // Resolve to concrete colours: a block cursor inverts fg/bg, and on an empty/default cell both are
            // null. Inverting null<->null renders a plain space (invisible). Default to white-on-black so the
            // block flips to a visible cell.
            var fg = c.Foreground ?? Color.White;
            var bg = c.Background ?? Color.Black;
            Character display = !on
                ? new Character(content, c.Foreground, c.Background, CursorEncoding.StripCursorBits(c.Decoration ?? Decoration.None))
                : style <= 2 ? new Character(content, bg, fg)   // block: invert fg/bg
                : style <= 4 ? new Character('_', fg, bg)        // underline
                             : new Character('|', fg, bg);       // bar
            Console.Write(_cursorPosition.Value, display);
            _legacyCursorShown = on;
        }

        // Called once per frame (via AdjustBufferSize) on the UI thread; flips a blinking software cursor at the
        // wall-clock rate even on idle frames, with no extra timer/thread and without touching the UI frame loop.
        private static void TickLegacyCursorBlink()
        {
            if (AnsiEnabled || !_legacyCursorBlinking || !_cursorPosition.HasValue) return;
            bool on = LegacyBlinkOn();
            if (on != _legacyCursorShown) RenderLegacyCursorCell(on);
        }

        public static void Setup()
        {
            Resize(WindowSize);
        }

        private static void WriteCharacterAnsiSequence(
			in Character character, 
			AnsiControlSequenceBuilder acsb,
			ref Color? currentFg,
			ref Color? currentBg,
			ref Decoration? currentDecoration)
		{
			// Strip the cursor style/colour bits encoded into the high decoration bits so they never leak into SGR.
			var decoration = CursorEncoding.StripCursorBits(character.Decoration ?? Decoration.None);
			if (decoration != currentDecoration)
			{
				var d = decoration;
				acsb.SetDecorations(
					intense: (d & Decoration.Bold) != 0,
					faint: (d & Decoration.Dim) != 0,
					italic: (d & Decoration.Italic) != 0,
					underline: (d & Decoration.Underline) != 0,
					invert: (d & Decoration.Invert) != 0,
					invisible: (d & Decoration.Conceal) != 0,
					blink: (d & Decoration.SlowBlink) != 0,
					rapidBlink: (d & Decoration.RapidBlink) != 0,
					strikethrough: (d & Decoration.Strikethrough) != 0);

				currentDecoration = decoration;
			}

			if (character.Foreground != currentFg)
			{
				if (character.Foreground.HasValue)
					acsb.SetForegroundColor(character.Foreground.Value.Red, character.Foreground.Value.Green, character.Foreground.Value.Blue);
				else
					acsb.Print("\x1b[39m"); // Default foreground

				currentFg = character.Foreground;
			}

			if (character.Background != currentBg)
			{
				if (character.Background.HasValue)
					acsb.SetBackgroundColor(character.Background.Value.Red, character.Background.Value.Green, character.Background.Value.Blue);
				else
					acsb.Print("\x1b[49m"); // Default background

				currentBg = character.Background;
			}

			acsb.PrintChar(character.Content ?? ' ');
		}

        public static void Resize(in Size size)
        {
            Console.Size = size;
            _buffer.Initialize(size);

            Initialize();
        }

        public static bool AdjustBufferSize()
        {
            if (WindowSize != BufferSize)
            {
                Resize(WindowSize);
                return true;
            }

            // Runs every frame on the UI thread (this is called by both Draw and the idle path), so the legacy
            // software cursor blinks at a steady rate without a separate timer or any change to the frame loop.
            TickLegacyCursorBlink();
            return false;
        }

        public static void AdjustWindowSize()
        {
            if (WindowSize != BufferSize)
                Resize(BufferSize);
        }

		public static void Draw()
		{
            StartDrawTimer();

            // Resize and redraw UI on screen if console size changed
            bool resized = AdjustBufferSize();

            // Resizing will automatically redraw, so just redraw if resize not needed.
            if (!resized) Redraw();

			StopDrawTimer();	
        }

        public static void ReadInput(IReadOnlyCollection<IInputListener> controls)
        {
            while (Console.KeyAvailable)
            {
                var key = Console.ReadKey();
                var inputEvent = new InputEvent(key);

                foreach (var control in controls)
                {
                    control?.OnInput(inputEvent);
                    if (inputEvent.Handled) break;
                }
            }
        }

        private static void BindContent()
        {
            ContentContext = new DrawingContext(new ConsoleManagerDrawingContextListener(), Content);
        }

        private static void UpdateMouseContext()
        {
            MouseContext = MousePosition.HasValue
                ? _buffer.GetMouseContext(MousePosition.Value)
                : null;
        }

        public static double AverageDrawTime
        {
            get
            {
                long total = 0;
                int count = 0;
                foreach (var time in drawTimes)
                {
                    if (time > 0)
                    {
                        total += time;
                        count++;
                    }
                }
                return count > 0 ? (double)total / count : 0;
            }
        }

        public static void StartDrawTimer() => drawTimer.Restart();

        public static void StopDrawTimer()
        {
            drawTimer.Stop();
            drawTimes[drawTimeIndex] = drawTimer.ElapsedMilliseconds;
            drawTimeIndex = (drawTimeIndex + 1) % drawTimeSamples;
        }
		
		private static readonly int drawTimeSamples = 60;	
        private static readonly long[] drawTimes = new long[drawTimeSamples];
		private static readonly Stopwatch drawTimer = new Stopwatch();		
        private static int drawTimeIndex = 0;
    }

	
}
