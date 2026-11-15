using System.Security.Cryptography;
using System.Text;
using Godot;

namespace SharpIDE.Godot.Features.SlnPicker;

public static class RandomRecentSlnColours
{
	public static readonly Color Teal = new Color("24a0a7");
	public static readonly Color Purple = new Color("9b55e0");
	public static readonly Color Pink = new Color("bd4fa4");
	public static readonly Color Blue = new Color("5390ce");
	public static readonly Color LightGreen = new Color("8fa759");
	public static readonly Color Orange = new Color("e3855e");
	public static readonly Color Green = new Color("53a472");
	public static readonly Color DarkYellow = new Color("b48615");
	public static readonly Color DarkBlue = new Color("4e6ef0");

	public static readonly List<Color> AllColours =
	[
		Teal,
		Purple,
		Pink,
		Blue,
		LightGreen,
		Orange,
		Green,
		DarkYellow,
		DarkBlue
	];

	public static Color GetColourForFilePath(string filePath)
	{
		var hashBytes = MD5.HashData(Encoding.UTF8.GetBytes(filePath));

		// Use all 16 bytes instead of just the first 4
		var hash = BitConverter.ToUInt32(hashBytes, 12) ^
				   BitConverter.ToUInt32(hashBytes, 8) ^
				   BitConverter.ToUInt32(hashBytes, 4) ^
				   BitConverter.ToUInt32(hashBytes, 0);

		var random = new Random((int)hash);
		var index = random.Next(AllColours.Count);
		return AllColours[index];
	}
}
