using Godot;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

namespace SharpIDE.Godot.Features.Git;

public static class GitColours
{
    private const string NewFileColourHexCode = "50964c";
    private const string EditedFileColourHexCode = "6496ba";
    private const string UnalteredFileColourHexCode = "d4d4d4";
    
    public static readonly Color GitNewFileColour = new Color(NewFileColourHexCode);
    public static readonly Color GitEditedFileColour = new Color(EditedFileColourHexCode);
    public static readonly Color GitUnalteredFileColour = new Color(UnalteredFileColourHexCode);

    public static readonly Color GitNewFileTransparentColour = new Color(NewFileColourHexCode, 0.4f);
    public static readonly Color GitEditedFileTransparentColour = new Color(EditedFileColourHexCode, 0.4f);
    public static readonly Color GitUnalteredFileTransparentColour = new Color(UnalteredFileColourHexCode, 0.4f);
    
    public static Color GetColorForGitFileStatus(GitFileStatus fileStatus) => fileStatus switch
    {
        GitFileStatus.Added => GitNewFileColour,
        GitFileStatus.Modified => GitEditedFileColour,
        GitFileStatus.Unaltered => GitUnalteredFileColour,
        _ => GitUnalteredFileColour
    };
    
    public static Color GetColorForGitLineStatus(GitLineStatus lineStatus) => lineStatus switch
    {
        GitLineStatus.Added => GitNewFileTransparentColour,
        GitLineStatus.Modified => GitEditedFileTransparentColour,
        GitLineStatus.Unaltered => GitUnalteredFileTransparentColour,
        _ => GitUnalteredFileTransparentColour
    };
}