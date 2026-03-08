using Godot;

namespace SharpIDE.Godot;

public static class EditorThemeColours
{
    public static readonly EditorThemeColorSet Light = new EditorThemeColorSet
    {
        Orange = TextEditorDotnetColoursLight.Orange,
        White = TextEditorDotnetColoursLight.White,
        Yellow = TextEditorDotnetColoursLight.Yellow,
        CommentGreen = TextEditorDotnetColoursLight.CommentGreen,
        KeywordBlue = TextEditorDotnetColoursLight.KeywordBlue,
        LightOrangeBrown = TextEditorDotnetColoursLight.LightOrangeBrown,
        NumberGreen = TextEditorDotnetColoursLight.NumberGreen,
        InterfaceGreen = TextEditorDotnetColoursLight.InterfaceGreen,
        ClassGreen = TextEditorDotnetColoursLight.ClassGreen,
        VariableBlue = TextEditorDotnetColoursLight.VariableBlue,
        Gray = TextEditorDotnetColoursLight.Gray,
        Pink = TextEditorDotnetColoursLight.Pink,
        ErrorRed = TextEditorDotnetColoursLight.ErrorRed,
        
        RazorComponentGreen = TextEditorDotnetColoursLight.RazorComponentGreen,
        RazorMetaCodePurple = TextEditorDotnetColoursLight.RazorMetaCodePurple,
        HtmlDelimiterGray = TextEditorDotnetColoursLight.HtmlDelimiterGray
    };
    
    public static readonly EditorThemeColorSet Dark = new EditorThemeColorSet
    {
        Orange = TextEditorDotnetColoursDark.Orange,
        White = TextEditorDotnetColoursDark.White,
        Yellow = TextEditorDotnetColoursDark.Yellow,
        CommentGreen = TextEditorDotnetColoursDark.CommentGreen,
        KeywordBlue = TextEditorDotnetColoursDark.KeywordBlue,
        LightOrangeBrown = TextEditorDotnetColoursDark.LightOrangeBrown,
        NumberGreen = TextEditorDotnetColoursDark.NumberGreen,
        InterfaceGreen = TextEditorDotnetColoursDark.InterfaceGreen,
        ClassGreen = TextEditorDotnetColoursDark.ClassGreen,
        VariableBlue = TextEditorDotnetColoursDark.VariableBlue,
        Gray = TextEditorDotnetColoursDark.Gray,
        Pink = TextEditorDotnetColoursDark.Pink,
        ErrorRed = TextEditorDotnetColoursDark.ErrorRed,
        
        RazorComponentGreen = TextEditorDotnetColoursDark.RazorComponentGreen,
        RazorMetaCodePurple = TextEditorDotnetColoursDark.RazorMetaCodePurple,
        HtmlDelimiterGray = TextEditorDotnetColoursDark.HtmlDelimiterGray
    };
}

public class EditorThemeColorSet
{
    public required Color Orange;
    public required Color White;
    public required Color Yellow;
    public required Color CommentGreen;
    public required Color KeywordBlue;
    public required Color LightOrangeBrown;
    public required Color NumberGreen;
    public required Color InterfaceGreen;
    public required Color ClassGreen;
    public required Color VariableBlue;
    public required Color Gray;
    public required Color Pink;
    public required Color ErrorRed;
    
    public required Color RazorComponentGreen;
    public required Color RazorMetaCodePurple;
    public required Color HtmlDelimiterGray;
}

public static class TextEditorDotnetColoursDark
{
    public static readonly Color Orange = new("f27718");
    public static readonly Color White = new("dcdcdc");
    public static readonly Color Yellow = new("dcdcaa");
    public static readonly Color CommentGreen = new("57a64a");
    public static readonly Color KeywordBlue = new("569cd6");
    public static readonly Color LightOrangeBrown = new("d69d85");
    public static readonly Color NumberGreen = new("b5cea8");
    public static readonly Color InterfaceGreen = new("b8d7a3");
    public static readonly Color ClassGreen = new("4ec9b0");
    public static readonly Color VariableBlue = new("9cdcfe");
    public static readonly Color Gray = new("a9a9a9");
    public static readonly Color Pink = new("c586c0");
    public static readonly Color ErrorRed = new("da5b5a");
    
    public static readonly Color RazorComponentGreen = new("0b7f7f");
    public static readonly Color RazorMetaCodePurple = new("a699e6");
    public static readonly Color HtmlDelimiterGray = new("808080");
}

public static class TextEditorDotnetColoursLight
{
    public static readonly Color Orange = new("b776fb"); //
    public static readonly Color White = new("000000"); //
    public static readonly Color Yellow = new("74531f"); //
    public static readonly Color CommentGreen = new("008000"); //
    public static readonly Color KeywordBlue = new("0000ff"); //
    public static readonly Color LightOrangeBrown = new("a31515"); //
    public static readonly Color NumberGreen = new("000000"); //
    public static readonly Color InterfaceGreen = new("2b91af"); //
    public static readonly Color ClassGreen = new("2b91af"); //
    public static readonly Color VariableBlue = new("1f377f"); //
    public static readonly Color Gray = new("a9a9a9"); //
    public static readonly Color Pink = new("c586c0"); //
    public static readonly Color ErrorRed = new("da5b5a"); //
    
    public static readonly Color RazorComponentGreen = new("0b7f7f");
    public static readonly Color RazorMetaCodePurple = new("826ee6");
    public static readonly Color HtmlDelimiterGray = new("808080");
}