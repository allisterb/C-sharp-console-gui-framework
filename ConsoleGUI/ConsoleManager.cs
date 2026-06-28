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

		/// <summary>
		/// Sink for a built ANSI frame. The default writes it to the system console on a thread-pool thread (so the
		/// blocking console write never stalls the UI thread). Tests swap this to capture the bytes instead. All
		/// frames are serialized through <see cref="Emit"/> regardless of sink, so output can never reorder; await
		/// <see cref="OutputIdle"/> to wait for everything queued so far to finish writing.
		/// </summary>
		public static Func<AnsiControlSequenceBuilder, Task> AnsiOutput = static acsb => Task.Run(acsb.WriteToSystemConsole);

		private static readonly object _outputLock = new object();
		private static Task _outputTail = Task.CompletedTask;

		/// <summary>A task that completes once every ANSI frame queued so far has been written. For deterministic tests.</summary>
		public static Task OutputIdle { get { lock (_outputLock) return _outputTail; } }

		// Serialize frame writes: each frame waits for the previous one before invoking the sink, so frames can never
		// be written out of order even though the write itself runs off the UI thread (the old fire-and-forget
		// Task.Run could reorder). A prior frame's failure is swallowed so one bad write can't stall the chain.
		private static void Emit(AnsiControlSequenceBuilder acsb)
		{
			lock (_outputLock)
				_outputTail = WriteAfter(_outputTail, acsb);

			static async Task WriteAfter(Task previous, AnsiControlSequenceBuilder builder)
			{
				try { await previous.ConfigureAwait(false); } catch { }
				await AnsiOutput(builder).ConfigureAwait(false);
			}
		}

		/// <summary>
		/// When <see langword="true"/>, a blinking cursor style is blinked by us (the cursor is shown steady and
		/// toggled on/off at a fixed wall-clock rate) in both the ANSI and legacy render paths. This keeps the
		/// blink constant even while other controls animate — animation forces per-frame cursor repositioning,
		/// which resets a terminal's <em>native</em> blink phase. When <see langword="false"/> (default), a
		/// blinking cursor style is left to the terminal's native blink (DECSCUSR on ANSI, the System.Console
		/// hardware cursor on legacy): cheaper and friendlier to screen readers, but the blink becomes erratic
		/// under continuous animation. Steady cursor styles are unaffected by this setting.
		/// </summary>
		public static bool EmulateBlinkingCursor = false;

		// Native terminal cursor state, driven by cells flagged with Character.IsCursor during Update.
		private static Position? _cursorPosition;
		private static bool _cursorVisible;
		private static int _cursorStyle = -1;       // last emitted DECSCUSR style; -1 forces first emit
		private static Color? _cursorColor;         // last emitted OSC 12 colour (null = terminal default)
		private static bool _cursorBlinking;        // current cursor's style blinks → we self-blink it (see below)

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

		/// <summary>
		/// Dispatches a wheel rotation to the listener under the current <see cref="MousePosition"/>, if it opts in
		/// via <see cref="IMouseWheelListener"/>. Set <see cref="MousePosition"/> first so the wheel targets the cell
		/// the pointer is over. <paramref name="delta"/> is a signed notch count (negative up, positive down).
		/// </summary>
		public static void MouseWheel(int delta)
		{
			if (MouseContext is MouseContext context && context.MouseListener is IMouseWheelListener wheelListener)
				wheelListener.OnMouseWheel(context.RelativePosition, delta);
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

					// Write the glyph, or a blank to erase a cell whose content was just cleared (e.g. a closed
					// popup). Without the blank the stale glyph persists until a full re-init (resize/clear).
					var character = cell.Character.Content.HasValue
						? cell.Character
						: new Character(' ', cell.Character.Foreground, cell.Character.Background, cell.Character.Decoration);

					if (y != lastY || x != lastX + 1)
					{
						acsb.MoveCursorTo(y, x);
					}
					WriteCharacterAnsiSequence(character, acsb, ref currentFg, ref currentBg, ref currentDecoration);
					lastY = y;
					lastX = x;
					wroteAnything = true;
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
				// emit only on change so we don't churn escape codes every frame.
				if (cursorAt.HasValue)
				{
					var deco = cursorChar.Decoration ?? Decoration.None;
					int rawStyle = CursorEncoding.DecodeStyle(deco);
					// With EmulateBlinkingCursor we blink the cursor ourselves (show/hide at a fixed wall-clock rate
					// below) rather than relying on the terminal's native blink: any animating control forces a
					// per-frame CUP to reposition the cursor (char writes move the real cursor), and most terminals
					// reset their native blink phase on CUP — so a native blink stutters. Emitting the STEADY DECSCUSR
					// variant keeps the cursor solid under those CUPs; our show/hide produces the constant-rate blink.
					// When the flag is off, emit the real (blinking) style and let the terminal blink it natively.
					_cursorBlinking = EmulateBlinkingCursor && IsBlinkingStyle(rawStyle);
					int style = _cursorBlinking ? SteadyCursorStyle(rawStyle) : rawStyle;
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

				// Blinking cursors are shown only during the "on" half of the wall-clock cycle; steady cursors always.
				bool show = !_cursorBlinking || CursorBlinkOn();
				if (show)
				{
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
				_cursorVisible = show;
			}
			else
			{
				if (_cursorVisible)
					acsb.SetCursorVisibility(false);
				_cursorPosition = newCursorPosition;
				_cursorVisible = false;
				_cursorBlinking = false;
			}

			Emit(acsb);
        }

        // Legacy/non-ANSI rendering: write each changed cell through the IConsole (e.g. SimplifiedConsole's
        // 16-colour System.Console output). The cursor is normally a *software* cursor — a specially-rendered cell —
        // so the hardware cursor (which we can't move without it visibly jumping per cell, or blink consistently)
        // stays hidden; shape/blink follow the CursorStyle encoded on the cursor Character. Exception: when
        // EmulateBlinkingCursor is off and the style is a blinking one, the System.Console hardware cursor is shown
        // and left to blink natively (steady styles always stay software, since the hardware cursor can't be steady).
        private static void UpdateLegacy(Rect rect)
        {
            // Hide the hardware cursor while drawing cells (so it doesn't visibly jump per write); it is re-shown
            // below only for the native-blink case.
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
                    // Write the glyph, or a blank to erase a cell whose content was just cleared (e.g. a closed
                    // popup); otherwise the stale glyph persists until a full re-init (resize/clear).
                    Console.Write(position, cell.Character.Content.HasValue
                        ? cell.Character
                        : new Character(' ', cell.Character.Foreground, cell.Character.Background, cell.Character.Decoration));
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

                if (IsBlinkingStyle(style) && !EmulateBlinkingCursor)
                {
                    // Native blink: draw the plain underlying glyph (cursor decoration stripped) and let the
                    // System.Console hardware cursor blink over it. RenderLegacyCursorCell leaves the hardware
                    // cursor just past the glyph, so reposition it onto the cursor cell before showing.
                    _legacyCursorBlinking = false; // not self-blinking — the terminal does it
                    RenderLegacyCursorCell(on: false);
                    SafeConsole.SetCursorPosition(cursorAt.Value.X, cursorAt.Value.Y);
                    SafeConsole.ShowCursor();
                    _cursorVisible = true;
                }
                else
                {
                    // Software cursor (drawn cell): steady styles render solid; blinking styles self-blink when
                    // emulation is on (the only way a blinking style reaches here).
                    _legacyCursorBlinking = IsBlinkingStyle(style);
                    RenderLegacyCursorCell(!_legacyCursorBlinking || CursorBlinkOn());
                }
            }
            else if (fullUpdate)
            {
                // Cursor gone — the cell it was on was redrawn as a normal glyph by the loop above. The hardware
                // cursor (if it was shown for native blink) was already hidden at the top of this method.
                _cursorPosition = null;
                _legacyCursorBlinking = false;
            }
        }

        // ~1Hz blink derived from wall-clock time, so the rate is independent of how often frames are drawn.
        // Shared by the legacy software cursor and the ANSI self-blink.
        private static bool CursorBlinkOn() => (Environment.TickCount64 / BlinkHalfPeriodMs) % 2 == 0;

        // DECSCUSR styles 0/1/3/5 blink (0 = terminal default = blinking block); 2/4/6 are steady.
        private static bool IsBlinkingStyle(int style) => style == 0 || (style % 2 == 1);

        // The steady DECSCUSR variant of a (possibly blinking) style: 0/1 -> 2 (block), 3 -> 4 (underline), 5 -> 6 (bar).
        private static int SteadyCursorStyle(int style) => style == 0 ? 2 : (style % 2 == 1 ? style + 1 : style);

        // Cheap blink tick for the ANSI self-blinking cursor: when the wall-clock phase flips, emit ONLY the cursor
        // visibility toggle (DECTCEM, plus a reposition when showing) — no full-screen scan. Called on idle frames so
        // a blinking cursor keeps ticking with no input/animation, costing a few bytes twice a second instead of a
        // whole-buffer redraw. On animating frames the cursor is handled inline by Update (it rides the redraw that
        // is happening anyway), so this must run ONLY when Update did not (idle frames) to avoid a double-emit.
        public static void TickCursorBlink()
        {
            if (!AnsiEnabled || !_cursorBlinking || !_cursorPosition.HasValue) return;
            bool show = CursorBlinkOn();
            if (show == _cursorVisible) return;

            var acsb = new AnsiControlSequenceBuilder();
            if (show)
            {
                // Nothing else wrote since the last toggle on an idle frame, so the real cursor is still here; the
                // reposition is belt-and-braces and, on a steady cursor, does not disturb our software blink.
                acsb.MoveCursorTo(_cursorPosition.Value.Y, _cursorPosition.Value.X);
                acsb.SetCursorVisibility(true);
            }
            else
            {
                acsb.SetCursorVisibility(false);
            }
            _cursorVisible = show;
            Emit(acsb);
        }

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
            bool on = CursorBlinkOn();
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
