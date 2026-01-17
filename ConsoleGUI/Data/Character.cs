using System;
using System.Collections.Generic;
using System.Text;

namespace ConsoleGUI.Data
{
    /// <summary>
    /// Represents text decoration.
    /// </summary>
    /// <remarks>
    /// Support for text decorations is up to the terminal.
    /// </remarks>
    [Flags]
    public enum Decoration
    {
        /// <summary>
        /// No text decoration.
        /// </summary>
        None = 0,

        /// <summary>
        /// Bold text.
        /// Not supported in every environment.
        /// </summary>
        Bold = 1 << 0,

        /// <summary>
        /// Dim or faint text.
        /// Not supported in every environment.
        /// </summary>
        Dim = 1 << 1,

        /// <summary>
        /// Italic text.
        /// Not supported in every environment.
        /// </summary>
        Italic = 1 << 2,

        /// <summary>
        /// Underlined text.
        /// Not supported in every environment.
        /// </summary>
        Underline = 1 << 3,

        /// <summary>
        /// Swaps the foreground and background colors.
        /// Not supported in every environment.
        /// </summary>
        Invert = 1 << 4,

        /// <summary>
        /// Hides the text.
        /// Not supported in every environment.
        /// </summary>
        Conceal = 1 << 5,

        /// <summary>
        /// Makes text blink.
        /// Normally less than 150 blinks per minute.
        /// Not supported in every environment.
        /// </summary>
        SlowBlink = 1 << 6,

        /// <summary>
        /// Makes text blink.
        /// Normally more than 150 blinks per minute.
        /// Not supported in every environment.
        /// </summary>
        RapidBlink = 1 << 7,

        /// <summary>
        /// Shows text with a horizontal line through the center.
        /// Not supported in every environment.
        /// </summary>
        Strikethrough = 1 << 8,
    }

    public readonly struct Character
	{
		public readonly char? Content;

		public readonly Color? Foreground;
		public readonly Color? Background;
        public readonly Decoration? Decoration;
		public readonly bool IsControl = false;

		public Character(char? content, Color? foreground = null, Color? background = null, Decoration? decoration = null, bool? isControl = null)
		{
			Content = content;
			Foreground = foreground;
			Background = background;
            Decoration = decoration;
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
				   lhs.Background == rhs.Background &&
                   lhs.Decoration == rhs.Decoration &&
                   lhs.IsControl == rhs.IsControl;
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
            var ch5 = Decoration is not null ? EqualityComparer<Decoration?>.Default.GetHashCode(Decoration) : 0;
                   
			var hashCode = -1521134295 * (-1661473088 + ch1 + ch2 + ch3 + ch4 + ch5);
			return hashCode;
		}
	}
}
