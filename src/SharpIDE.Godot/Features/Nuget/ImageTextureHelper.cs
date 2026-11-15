using Godot;
using SharpIDE.Application.Features.Nuget;

namespace SharpIDE.Godot.Features.Nuget;

public static class ImageTextureHelper
{
	public static ImageTexture? GetImageTextureFromBytes(byte[]? imageBytes, NugetPackageIconFormat? format)
	{
		if (imageBytes is null || format is null) return null;
		var image = new Image();
		var error = format switch
		{
			NugetPackageIconFormat.Png => image.LoadPngFromBuffer(imageBytes),
			NugetPackageIconFormat.Jpg => image.LoadJpgFromBuffer(imageBytes),
			_ => Error.FileUnrecognized
		};
		if (error is Error.Ok)
		{
			image.Resize(32, 32, Image.Interpolation.Lanczos); // Probably should cache resized images instead
			return ImageTexture.CreateFromImage(image);
		}
		return null!;
	}
}
