using System;
using System.Collections.Generic;
using System.Text;

namespace ConsoleGUI.Space
{
	public readonly struct Offset : IEquatable<Offset>
	{
		public int Left { get; }
		public int Top { get; }
		public int Right { get; }
		public int Bottom { get; }

		public Offset(int left, int top, int right, int bottom)
		{
			Left = left;
			Top = top;
			Right = right;
			Bottom = bottom;
		}

		// Value equality. Offset had none, so ControlFrame's `_margin.Equals(value)` guard bound to the boxing
		// ValueType.Equals(object); with this it binds to the typed overload at compile time. See Size.
		public bool Equals(Offset other) => this == other;

		public override bool Equals(object obj) => obj is Offset offset && this == offset;

		public override int GetHashCode() => HashCode.Combine(Left, Top, Right, Bottom);

		public static bool operator ==(in Offset lhs, in Offset rhs) =>
			lhs.Left == rhs.Left && lhs.Top == rhs.Top && lhs.Right == rhs.Right && lhs.Bottom == rhs.Bottom;

		public static bool operator !=(in Offset lhs, in Offset rhs) => !(lhs == rhs);

		public override string ToString() => $"({Left}, {Top}, {Right}, {Bottom})";
	}
}
