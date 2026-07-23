using ConsoleGUI.Data;
using ConsoleGUI.Space;

namespace ConsoleGUI.Api
{
	/// <summary>
	/// A cell-addressable render target: read and write a <see cref="Character"/> at a column/row. Lets a drawing
	/// producer (e.g. ConsolePlot) render straight into a host buffer instead of into its own scratch array that must
	/// then be copied cell-by-cell. Coordinates are top-down (row 0 at the top), matching the console.
	/// </summary>
	public interface IConsoleBuffer
	{
		/// <summary>The logical size of the target (columns × rows). Writes/reads outside this are the caller's responsibility.</summary>
		Size Size { get; }

		/// <summary>The <see cref="Character"/> currently at (<paramref name="x"/>, <paramref name="y"/>). Returns just the
		/// glyph/colours, not the whole cell, so a hot draw loop reading cells back doesn't copy any per-cell listener state.</summary>
		Character CharacterAt(int x, int y);

		/// <summary>Writes <paramref name="character"/> at column <paramref name="x"/>, row <paramref name="y"/>.</summary>
		void Write(int x, int y, in Character character);
	}
}
