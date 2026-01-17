using System;
using System.Collections.Generic;
using System.Text;

namespace ConsoleGUI.Data
{
	public readonly struct Character
	{
		public readonly char? Content;

		public readonly Color? Foreground;
		public readonly Color? Background;
		public readonly bool IsControl = false;
		public readonly bool? Blink;
		public readonly bool? Invert;
		public readonly bool? Underline;

		public Character(char? content, Color? foreground = null, Color? background = null, bool? isControl = null)
		{
			Content = content;
			Foreground = foreground;
			Background = background;
			IsControl = isControl ?? false;
		}

		public Character(in Color background)
		{
			Content = null;
			Foreground = null;
			Background = background;
		}

		public Character WithContent(char? content) => new Character(content, Foreground, Background);
		public Character WithForeground(in Color? foreground) => new Character(Content, foreground, Background);
		public Character WithBackground(in Color? background) => new Character(Content, Foreground, background);

		public static Character Empty => new Character();

		public static bool operator==(in Character lhs, in Character rhs)
		{
			return lhs.Content == rhs.Content &&
				   lhs.Foreground == rhs.Foreground &&
				   lhs.Background == rhs.Background;
		}

		public static bool operator !=(in Character lhs, in Character rhs) => !(lhs == rhs);

		public override bool Equals(object obj)
		{
			return obj is Character character && this == character;
		}

		public override int GetHashCode()
		{
			var ch1 = Content is not null ? EqualityComparer<char?>.Default.GetHashCode(Content) : 0;
            var ch2 = Foreground is not null ? EqualityComparer<Color?>.Default.GetHashCode(Foreground) : 0;
            var ch3 = Background is not null ? EqualityComparer<Color?>.Default.GetHashCode(Background) : 0;
            var ch4 = EqualityComparer<bool>.Default.GetHashCode(IsControl);
            var ch5 = Blink is not null ? EqualityComparer<bool?>.Default.GetHashCode(Blink) : 0;
            var ch6 = Invert is not null ? EqualityComparer<bool?>.Default.GetHashCode(Invert) : 0;
            var ch7 = Underline is not null ? EqualityComparer<bool?>.Default.GetHashCode(Underline) : 0;           
			var hashCode = -1521134295 * (-1661473088 + ch1 + ch2 + ch3 + ch4 + ch5 + ch6 + ch7);
			return hashCode;
		}
	}
}
