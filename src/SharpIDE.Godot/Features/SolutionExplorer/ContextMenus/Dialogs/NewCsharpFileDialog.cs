using Godot;
using SharpIDE.Application.Features.FileWatching;
using SharpIDE.Application.Features.SolutionDiscovery;
using SharpIDE.Application.Features.SolutionDiscovery.VsPersistence;

namespace SharpIDE.Godot.Features.SolutionExplorer.ContextMenus.Dialogs;

public partial class NewCsharpFileDialog : ConfirmationDialog
{
    private const string ClassType = "Class";
    private const string InterfaceType = "Interface";
    private const string RecordType = "Record";
    private const string StructType = "Struct";
    private const string EnumType = "Enum";
    
    private LineEdit _nameLineEdit = null!;
    private ItemList _fileTypeItemList = null!;

    public IFolderOrProject ParentNode { get; set; } = null!;

    [Inject] private readonly IdeFileOperationsService _ideFileOperationsService = null!;
    
    private Texture2D _classIcon = GD.Load<Texture2D>("uid://do0edciarrnp0");

    public override void _Ready()
    {
        _nameLineEdit = GetNode<LineEdit>("%CSharpFileNameLineEdit");
        _nameLineEdit.GrabFocus();
        _nameLineEdit.SelectAll();
        _fileTypeItemList = GetNode<ItemList>("%FileTypeItemList");
        _fileTypeItemList.AddItem(ClassType, _classIcon);
        _fileTypeItemList.AddItem(InterfaceType, _classIcon);
        _fileTypeItemList.AddItem(RecordType, _classIcon);
        _fileTypeItemList.AddItem(StructType, _classIcon);
        _fileTypeItemList.AddItem(EnumType, _classIcon);
        _fileTypeItemList.Select(0);
        _fileTypeItemList.ItemSelected += FileTypeItemListOnItemSelected;
        Confirmed += OnConfirmed;
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Keycode: Key.Enter })
        {
            OnConfirmed();
        }
        else if (@event is InputEventKey { Pressed: true, Keycode: Key.Down })
        {
            var selectedIndex = _fileTypeItemList.GetSelectedItems()[0];
            var nextIndex = (selectedIndex + 1) % _fileTypeItemList.GetItemCount();
            _fileTypeItemList.Select(nextIndex);
        }
        else if (@event is InputEventKey { Pressed: true, Keycode: Key.Up })
        {
            var selectedIndex = _fileTypeItemList.GetSelectedItems()[0];
            var previousIndex = (selectedIndex - 1 + _fileTypeItemList.GetItemCount()) % _fileTypeItemList.GetItemCount();
            _fileTypeItemList.Select(previousIndex);
        }
    }

    private void FileTypeItemListOnItemSelected(long index)
    {
        GD.Print("Selected file type index: " + index);
    }

    private void OnConfirmed()
    {
        var fileName = _nameLineEdit.Text.Trim();
        if (IsNameInvalid(fileName))
        {
            GD.PrintErr("File name cannot be empty.");
            return;
        }

        if (!fileName.EndsWith(".cs"))
        {
            fileName += ".cs";
        }

        var selectedIndex = _fileTypeItemList.GetSelectedItems().Single();
        var selectedFileType = _fileTypeItemList.GetItemText(selectedIndex);
        
        var typeName = GetCsharpTypeName(selectedFileType);

        _ = Task.GodotRun(async () =>
        {
           var sharpIdeFile = await _ideFileOperationsService.CreateCsFile(ParentNode, fileName, typeName);
           GodotGlobalEvents.Instance.FileExternallySelected.InvokeParallelFireAndForget(sharpIdeFile, null);
        });
        QueueFree();
    }
    
    private static bool IsNameInvalid(string name)
    {
        return string.IsNullOrWhiteSpace(name);
    }

    private static string GetCsharpTypeName(string fileType) => fileType switch
    {
        ClassType => "class",
        InterfaceType => "interface", 
        RecordType => "record",
        StructType => "struct",
        EnumType => "enum",
        
        _ => throw new ArgumentException($"The file type '{fileType}' is not supported.", nameof(fileType))
    };
}
