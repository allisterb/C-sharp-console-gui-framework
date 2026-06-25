using ConsoleGUI.Space;

namespace ConsoleGUI.Input
{
	/// <summary>
	/// Optional mouse-wheel extension to <see cref="IMouseListener"/>. A control that tags its cells with a mouse
	/// listener (see <c>Cell.WithMouseListener</c>) and also implements this interface receives wheel notches that
	/// occur while the pointer is over it. Kept separate from <see cref="IMouseListener"/> so existing listeners are
	/// not forced to implement it.
	/// </summary>
	public interface IMouseWheelListener
	{
		/// <summary>
		/// Called when the wheel is rotated over the control. <paramref name="delta"/> is a signed notch count:
		/// negative scrolls up (toward earlier content), positive scrolls down.
		/// </summary>
		void OnMouseWheel(Position position, int delta);
	}
}
